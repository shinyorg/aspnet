namespace Shiny.AspNet.Idempotency;

/// <summary>
/// What an endpoint asked for, read from endpoint metadata by <see cref="IdempotencyMiddleware"/>.
/// <para>
/// Implemented by <see cref="IdempotentAttribute"/>, <see cref="DisableIdempotencyAttribute"/> and
/// <see cref="IdempotencyMetadata"/> (what <c>RequireIdempotencyKey()</c>, <c>WithIdempotency()</c> and
/// <c>DisableIdempotency()</c> attach). When an endpoint carries more than one, the most specific wins —
/// ASP.NET orders metadata group → endpoint, controller → action, so the last one is the one that counts.
/// </para>
/// </summary>
public interface IIdempotencyMetadata
{
    /// <summary>
    /// True when a request without a key is refused with a 400. False honours a key when one is sent
    /// and lets a request without one through untouched.
    /// </summary>
    bool Required { get; }

    /// <summary>Overrides <see cref="IdempotencyOptions.Expiration"/> for this endpoint.</summary>
    TimeSpan? Expiration { get; }

    /// <summary>True when the endpoint opted out, including out of a group's or controller's convention.</summary>
    bool Disabled { get; }
}

/// <summary>
/// What an endpoint asked for, attached by <c>RequireIdempotencyKey</c>, <c>WithIdempotency</c> or
/// <c>DisableIdempotency</c>.
/// </summary>
public sealed class IdempotencyMetadata : IIdempotencyMetadata
{
    /// <inheritdoc />
    public bool Required { get; init; }

    /// <inheritdoc />
    public TimeSpan? Expiration { get; init; }

    /// <inheritdoc />
    public bool Disabled { get; init; }
}

/// <summary>
/// Makes an endpoint honour <c>Idempotency-Key</c>, so a retried POST gets the stored original response
/// instead of running twice. Usable on MVC controllers and actions, and on minimal-API handlers.
/// <code>
/// [ApiController, Route("api/payments")]
/// [Idempotent]
/// public class PaymentsController : ControllerBase
/// {
///     [HttpPost] public IActionResult Charge(ChargeRequest request) => ...;
///     [HttpPost("quote")] [DisableIdempotency] public QuoteResponse Quote(QuoteRequest request) => ...;
/// }
///
/// app.MapPost("/orders", handler).WithMetadata(new IdempotentAttribute());
/// </code>
/// <para>
/// Needs <c>services.AddIdempotency()</c> and <c>app.UseIdempotency()</c>. On an action it replaces the
/// controller's settings rather than adding to them.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class IdempotentAttribute : Attribute, IIdempotencyMetadata
{
    /// <summary>
    /// Whether a request without a key is refused with a 400. On by default — an endpoint that is
    /// worth protecting is usually one where an unprotected call is the bug.
    /// </summary>
    public bool Required { get; set; } = true;

    /// <summary>How long the response is kept for replay, overriding the app-wide expiration. Zero keeps the default.</summary>
    public int ExpirationSeconds { get; set; }

    TimeSpan? IIdempotencyMetadata.Expiration => this.ExpirationSeconds > 0 ? TimeSpan.FromSeconds(this.ExpirationSeconds) : null;

    bool IIdempotencyMetadata.Disabled => false;
}

/// <summary>Exempts an endpoint from idempotency, including from a controller-level <c>[Idempotent]</c> or a group convention.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class DisableIdempotencyAttribute : Attribute, IIdempotencyMetadata
{
    bool IIdempotencyMetadata.Required => false;

    TimeSpan? IIdempotencyMetadata.Expiration => null;

    bool IIdempotencyMetadata.Disabled => true;
}
