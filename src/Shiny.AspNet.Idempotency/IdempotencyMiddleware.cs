using System.Buffers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shiny.AspNet.Idempotency.Internal;

namespace Shiny.AspNet.Idempotency;

/// <summary>
/// Answers a retried request with the response its first attempt got, instead of running the
/// handler twice.
/// <para>
/// Reads the endpoint's <see cref="IIdempotencyMetadata"/>, so it has to run after routing; and it
/// belongs after <c>UseAuthorization()</c> because the caller is part of the key: a replay must never
/// hand one user the response to another user's request just because their clients picked the same key.
/// </para>
/// <para>
/// A request carrying a key goes one of four ways:
/// <list type="bullet">
/// <item>The key is new — the handler runs, and its response is stored.</item>
/// <item>The key's first request finished — the stored response is replayed, marked as such.</item>
/// <item>The key's first request is still running — <b>409</b>. Running a second copy alongside is
/// the double charge this exists to prevent; waiting for it would hold a connection open for as long
/// as the first one takes.</item>
/// <item>The key was used with a different body — <b>422</b>. That is a client bug, and replaying
/// the first answer would hide it.</item>
/// </list>
/// </para>
/// </summary>
public sealed class IdempotencyMiddleware
{
    /// <summary>Separates the parts of a store key. Not a character any of them can contain.</summary>
    const char KeySeparator = '\u001f';

    readonly RequestDelegate next;
    readonly IdempotencyOptions options;
    readonly IIdempotencyStore store;
    readonly ILogger logger;

    public IdempotencyMiddleware(
        RequestDelegate next,
        IOptions<IdempotencyOptions> options,
        IIdempotencyStore store,
        ILogger<IdempotencyMiddleware> logger
    )
    {
        this.next = next ?? throw new ArgumentNullException(nameof(next));
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<IIdempotencyMetadata>();

        if (metadata is null or { Disabled: true } || !this.options.Methods.Contains(context.Request.Method))
        {
            await this.next(context).ConfigureAwait(false);
            return;
        }

        var rawKey = context.Request.Headers[this.options.HeaderName].ToString();

        if (rawKey.Length == 0 && !context.Request.Headers.ContainsKey(this.options.HeaderName))
        {
            if (metadata.Required)
            {
                await ProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Idempotency key required",
                    $"This endpoint requires an '{this.options.HeaderName}' header, so a retry of the request cannot be applied twice."
                ).ConfigureAwait(false);
                return;
            }

            await this.next(context).ConfigureAwait(false);
            return;
        }

        if (!TryParseKey(rawKey, this.options.MaxKeyLength, out var clientKey))
        {
            await ProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Invalid idempotency key",
                $"The '{this.options.HeaderName}' header must be a non-empty string of at most {this.options.MaxKeyLength} characters."
            ).ConfigureAwait(false);
            return;
        }

        // The body is read before anything is reserved: a request that turns out to be too large, or
        // whose body fails to read (a Content-Digest that does not verify, a client that went away),
        // must not leave a claim behind that locks out the client's corrected retry.
        var body = await ReadBodyAsync(context, this.options.MaxRequestBodyBytes).ConfigureAwait(false);

        if (body is null)
        {
            await ProblemAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "Request body too large for an idempotent request",
                $"A request carrying '{this.options.HeaderName}' is limited to {this.options.MaxRequestBodyBytes} bytes, because its body has to be held to fingerprint it."
            ).ConfigureAwait(false);
            return;
        }

        var fingerprint = Convert.ToBase64String(SHA256.HashData(body));
        var key = BuildKey(context, clientKey);
        var abort = context.RequestAborted;

        var existing = await this.store.TryReserveAsync(key, fingerprint, this.options.InFlightTimeout, abort).ConfigureAwait(false);

        if (existing is not null)
        {
            await this.AnswerFromExistingAsync(context, existing, fingerprint).ConfigureAwait(false);
            return;
        }

        var request = context.Request;
        var response = context.Response;
        var originalBody = request.Body;
        var originalFeature = context.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
        var capture = new CapturingResponseBodyFeature(originalFeature, this.options.MaxResponseBodyBytes);

        // Handed on rewound, so the handler reads exactly what was fingerprinted.
        request.Body = new MemoryStream(body, writable: false);
        context.Features.Set<IHttpResponseBodyFeature>(capture);

        try
        {
            try
            {
                await this.next(context).ConfigureAwait(false);
            }
            catch
            {
                // A failure is not an answer. The claim goes, so the client's retry gets to run. The
                // original body feature is back in place before the exception reaches whatever handles
                // it further out, so an error page is written to the real response, not the copy.
                await this.store.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            await capture.FinishAsync().ConfigureAwait(false);
        }
        finally
        {
            context.Features.Set(originalFeature);
            request.Body = originalBody;
        }

        if (capture.Captured is { } captured && this.options.ShouldStoreStatusCode(response.StatusCode))
        {
            var now = DateTimeOffset.UtcNow;
            var stored = new IdempotentResponse(response.StatusCode, Storable(response.Headers), captured, now);
            var record = new IdempotencyRecord(fingerprint, stored, now + (metadata.Expiration ?? this.options.Expiration));

            await this.store.CompleteAsync(key, record, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (capture.Captured is null)
        {
            this.logger.LogWarning(
                "The response to {Method} {Path} was larger than {Limit} bytes, so it cannot be replayed; a retry with the same key will run the handler again",
                context.Request.Method,
                context.Request.Path,
                this.options.MaxResponseBodyBytes
            );
        }

        await this.store.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);
    }

    async Task AnswerFromExistingAsync(HttpContext context, IdempotencyRecord existing, string fingerprint)
    {
        // Compared before anything else: a different payload under a key that is still running is
        // still a misused key, and saying "conflict, retry later" would send the client round again
        // to be told the same thing.
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(existing.Fingerprint), Encoding.ASCII.GetBytes(fingerprint)))
        {
            await ProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "Idempotency key reused",
                $"The '{this.options.HeaderName}' was already used for a request with a different body. Use a new key for a new request."
            ).ConfigureAwait(false);
            return;
        }

        if (existing.Response is not { } stored)
        {
            context.Response.Headers.RetryAfter = "1";

            await ProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "Request already in progress",
                $"A request with this '{this.options.HeaderName}' is still being processed. Retry once it has finished to receive its response."
            ).ConfigureAwait(false);
            return;
        }

        await this.ReplayAsync(context, stored).ConfigureAwait(false);
    }

    async Task ReplayAsync(HttpContext context, IdempotentResponse stored)
    {
        var response = context.Response;

        response.StatusCode = stored.StatusCode;

        foreach (var header in stored.Headers)
            response.Headers.Append(header.Key, header.Value);

        if (this.options.ReplayedHeaderName is { Length: > 0 } marker)
            response.Headers[marker] = "true";

        response.ContentLength = stored.Body.Length;

        if (stored.Body.Length == 0)
        {
            await response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await response.Body.WriteAsync(stored.Body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts the draft's form — a structured-field string, <c>"8e03978e-…"</c> — and the unquoted
    /// form most clients actually send. Either way the key is opaque; nothing about its shape is
    /// checked beyond being printable and bounded.
    /// </summary>
    internal static bool TryParseKey(string raw, int maxLength, out string key)
    {
        var value = raw.Trim();

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

        key = value;

        if (value.Length == 0 || value.Length > maxLength)
            return false;

        foreach (var c in value)
        {
            if (c < 0x20 || c > 0x7e)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The client's key, scoped by method, path and caller.
    /// <para>
    /// Scoped by path because the draft makes a key unique per resource, not per server — two
    /// endpoints are allowed to see the same key. Scoped by caller because a key is chosen by a
    /// client, and two clients choosing the same one must not be able to read each other's replies.
    /// </para>
    /// </summary>
    static string BuildKey(HttpContext context, string clientKey)
    {
        var request = context.Request;
        var path = request.PathBase.Add(request.Path).Value ?? string.Empty;
        var builder = new StringBuilder(request.Method.Length + path.Length + clientKey.Length + 32)
            .Append(request.Method)
            .Append(KeySeparator)
            .Append(path)
            .Append(KeySeparator);

        if (context.User.Identity is { IsAuthenticated: true } identity)
        {
            builder
                .Append(identity.AuthenticationType)
                .Append(':')
                .Append(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? identity.Name);
        }

        return builder.Append(KeySeparator).Append(clientKey).ToString();
    }

    /// <summary>Reads the whole body, or returns null once it passes <paramref name="limit"/>.</summary>
    static async Task<byte[]?> ReadBodyAsync(HttpContext context, int limit)
    {
        var request = context.Request;

        if (request.ContentLength == 0
            || context.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false })
        {
            return [];
        }

        if (request.ContentLength > limit)
            return null;

        var buffer = new ArrayBufferWriter<byte>((int)Math.Min(request.ContentLength ?? 4096, limit) + 1);

        while (true)
        {
            var memory = buffer.GetMemory(4096);
            var read = await request.Body.ReadAsync(memory, context.RequestAborted).ConfigureAwait(false);

            if (read == 0)
                return buffer.WrittenSpan.ToArray();

            buffer.Advance(read);

            if (buffer.WrittenCount > limit)
                return null;
        }
    }

    /// <summary>
    /// Everything except the headers that describe this connection rather than this response.
    /// Replaying a stored <c>Transfer-Encoding</c> or <c>Connection</c> would frame the reply for a
    /// connection that no longer exists; <c>Content-Length</c> is set again from the stored body.
    /// </summary>
    internal static List<KeyValuePair<string, string>> Storable(IHeaderDictionary headers)
    {
        var stored = new List<KeyValuePair<string, string>>(headers.Count);

        foreach (var header in headers)
        {
            if (IsHopByHop(header.Key))
                continue;

            foreach (var value in header.Value)
            {
                if (value is not null)
                    stored.Add(new KeyValuePair<string, string>(header.Key, value));
            }
        }

        return stored;
    }

    static bool IsHopByHop(string name)
        => string.Equals(name, HeaderNames.Connection, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HeaderNames.KeepAlive, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HeaderNames.Date, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Problem details through <c>Results.Problem</c>, which writes through <see cref="IProblemDetailsService"/>
    /// when one is registered (so <c>AddProblemDetails</c> customisations apply) and as plain
    /// <c>application/problem+json</c> when not.
    /// </summary>
    static Task ProblemAsync(HttpContext context, int status, string title, string detail)
        => Results.Problem(detail: detail, statusCode: status, title: title).ExecuteAsync(context);
}
