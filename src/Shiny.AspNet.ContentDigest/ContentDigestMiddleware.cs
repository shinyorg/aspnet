using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AspNet.ContentDigest.Internal;

namespace Shiny.AspNet.ContentDigest;

/// <summary>
/// Checks <c>Content-Digest</c> on request bodies and adds it — and <c>Repr-Digest</c> — to responses
/// (RFC 9530).
/// <para>
/// Must be registered ahead of <c>UseRequestDecompression</c> and <c>UseResponseCompression</c>: a
/// <c>Content-Digest</c> is over the bytes on the wire, content coding included, so it has to see them
/// before decompression and after compression. Per-endpoint settings come from
/// <see cref="IContentDigestMetadata"/> on the endpoint routing selected, so it also has to come after
/// routing; with no endpoint (static files, fallbacks served by middleware) the server-wide options
/// apply.
/// </para>
/// </summary>
public sealed class ContentDigestMiddleware(
    ContentDigestOptions options,
    ILogger<ContentDigestMiddleware>? logger = null
) : IMiddleware
{
    readonly ContentDigestOptions options = options ?? throw new ArgumentNullException(nameof(options));
    readonly ILogger logger = logger ?? NullLogger<ContentDigestMiddleware>.Instance;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<IContentDigestMetadata>();

        if (metadata is { Disabled: true })
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var request = context.Request;
        var hasBody = HasBody(context);
        DigestVerifyingStream? verifier = null;

        if (this.options.VerifyRequests)
        {
            var header = DigestFields.Join(request.Headers[DigestFields.ContentDigest]);

            if (header.Length > 0)
            {
                var (rejection, stream) = this.PrepareVerification(request, header, hasBody);

                if (rejection is not null)
                {
                    await this.RejectAsync(context, rejection).ConfigureAwait(false);
                    return;
                }

                verifier = stream;
            }
            else if (hasBody && (metadata?.RequireRequestDigest ?? this.options.RequireRequestDigest))
            {
                await this.RejectAsync(
                    context,
                    $"This endpoint requires a '{DigestFields.ContentDigest}' header on the request body."
                ).ConfigureAwait(false);
                return;
            }
        }

        var originalRequestBody = request.Body;
        if (verifier is not null)
            request.Body = verifier;

        var response = context.Response;
        var originalBody = context.Features.Get<IHttpResponseBodyFeature>()!;
        var control = this.CreateBodyControl(context, originalBody, metadata, holdForVerdict: verifier is not null);

        if (control is not null)
            context.Features.Set<IHttpResponseBodyFeature>(control);

        try
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (Exception ex) when (!response.HasStarted)
            {
                // Nothing reached the wire, so whatever the handler had written is dropped and the
                // error goes on with the server's own body rather than into one that is being held.
                if (control is not null)
                    context.Features.Set(originalBody);

                if (verifier?.Failure is { } failure && ReferenceEquals(ex, failure))
                {
                    response.Clear();
                    await this.RejectAsync(context, failure.Message).ConfigureAwait(false);
                    return;
                }

                throw;
            }

            // A handler that caught the mismatch and answered anyway has answered a request whose body
            // is not what the client sent. While nothing has reached the wire, that answer is replaced.
            if (verifier?.Failure is { } swallowed && !response.HasStarted)
            {
                if (control is not null)
                    context.Features.Set(originalBody);

                response.Clear();
                await this.RejectAsync(context, swallowed.Message).ConfigureAwait(false);
                return;
            }

            if (control is not null)
                await control.FinishAsync(context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            // Middleware further out sees the request and response as they were handed in.
            if (control is not null)
                context.Features.Set(originalBody);

            if (verifier is not null)
            {
                request.Body = originalRequestBody;
                await verifier.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Parses the request's digest and either returns why it is refused or a stream that checks the
    /// body against it.
    /// </summary>
    (string? Rejection, DigestVerifyingStream? Stream) PrepareVerification(HttpRequest request, string header, bool hasBody)
    {
        if (!DigestFields.TryParse(header, out var digests))
            return ($"The '{DigestFields.ContentDigest}' header is not a valid structured-field dictionary of byte sequences.", null);

        var supported = new List<KeyValuePair<string, byte[]>>(digests.Count);

        foreach (var digest in digests)
        {
            if (DigestFields.IsSupported(digest.Key))
                supported.Add(digest);
        }

        if (supported.Count == 0)
        {
            if (!this.options.RejectUnsupportedAlgorithms)
                return (null, null);

            return ($"None of the algorithms in '{DigestFields.ContentDigest}' are supported. Use '{DigestFields.Sha256}' or '{DigestFields.Sha512}'.", null);
        }

        // No body: the digest is of empty content, and can be checked right now.
        if (!hasBody)
        {
            foreach (var (algorithm, expected) in supported)
            {
                using var hash = DigestFields.CreateHash(algorithm);

                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
                    return ($"The request body does not match its Content-Digest ({algorithm}).", null);
            }

            return (null, null);
        }

        return (null, new DigestVerifyingStream(request.Body, supported, request.ContentLength));
    }

    /// <summary>
    /// Decides which digests this response gets, and wraps the body to compute them. Null when it gets
    /// none, so a response nobody asked a digest of pays nothing.
    /// <para>
    /// A request whose body is being verified has its response held too, digest or not
    /// (<paramref name="holdForVerdict"/>). The verdict only exists once the body has been read to the
    /// end, and a handler that catches the failure and answers anyway would otherwise have its 200
    /// on the wire before anything could replace it.
    /// </para>
    /// </summary>
    DigestingResponseBody? CreateBodyControl(
        HttpContext context,
        IHttpResponseBodyFeature inner,
        IContentDigestMetadata? metadata,
        bool holdForVerdict
    )
    {
        var mode = metadata?.ResponseDigest ?? this.options.ResponseDigest;
        var request = context.Request;

        // HEAD has no content to digest, and an upgrade's body belongs to another protocol.
        if (HttpMethods.IsHead(request.Method)
            || HttpMethods.IsConnect(request.Method)
            || request.Headers.ContainsKey(HeaderNames.Upgrade))
        {
            return null;
        }

        if (mode == DigestEmission.Never)
        {
            return holdForVerdict
                ? new DigestingResponseBody(context.Response, inner, this.options.MaxBufferedResponseBytes, null, null)
                : null;
        }

        var content = DigestFields.SelectPreferred(DigestFields.Join(request.Headers[DigestFields.WantContentDigest]));
        var repr = DigestFields.SelectPreferred(DigestFields.Join(request.Headers[DigestFields.WantReprDigest]));

        if (content is null && mode == DigestEmission.Always)
            content = this.options.DefaultAlgorithm;

        if (content is null && repr is null && !holdForVerdict)
            return null;

        return new DigestingResponseBody(context.Response, inner, this.options.MaxBufferedResponseBytes, content, repr);
    }

    /// <summary>
    /// Whether the request has a body to verify. The server knows best (a declared length, chunked
    /// framing, an HTTP/2 stream left open); hosts that do not say fall back to the headers.
    /// </summary>
    static bool HasBody(HttpContext context)
    {
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>() is { } detection)
            return detection.CanHaveBody;

        var request = context.Request;
        return request.ContentLength > 0 || request.Headers.ContainsKey(HeaderNames.TransferEncoding);
    }

    async Task RejectAsync(HttpContext context, string detail)
    {
        this.logger.LogWarning(
            "Refused {Method} {Path}: {Detail}",
            context.Request.Method,
            context.Request.Path,
            detail
        );

        // Says what would have been accepted, which is how RFC 9530 has a server ask for a digest.
        context.Response.Headers[DigestFields.WantContentDigest] = DigestFields.WantValue(this.options.DefaultAlgorithm);

        await Results
            .Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: "Content digest check failed")
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }
}
