namespace Shiny.AspNet.Webhooks;

/// <summary>
/// Endpoint metadata asking <c>UseWebhookVerification()</c> to verify deliveries to the endpoint.
/// Implemented by <see cref="RequireWebhookSignatureAttribute"/> and <see cref="WebhookMetadata"/>;
/// when an endpoint carries more than one, the last one added wins — so an action's attribute
/// replaces its controller's, and a convention applied to a single route replaces a group's.
/// </summary>
public interface IRequireWebhookSignatureMetadata
{
    /// <summary>A verifier registered by name with <see cref="WebhookOptions.AddVerifier"/>.</summary>
    string? VerifierName { get; }

    /// <summary>A verifier given directly. Wins over <see cref="VerifierName"/>.</summary>
    IWebhookVerifier? Verifier { get; }
}

/// <summary>
/// Requires a verified webhook signature on a controller, an action, or a minimal-API endpoint.
/// <code>
/// [ApiController, Route("hooks")]
/// public class HookController : ControllerBase
/// {
///     [HttpPost("github")]
///     [RequireWebhookSignature("github")]
///     public IActionResult GitHub()
///     {
///         var webhook = HttpContext.GetWebhook();
///         ...
///     }
/// }
///
/// builder.Services.AddWebhooks(o => o.AddVerifier("github", WebhookSignature.GitHub(secret)));
/// app.UseWebhookVerification();
/// </code>
/// <para>
/// Enforced by <c>UseWebhookVerification()</c>, the way <c>[Authorize]</c> is enforced by
/// <c>UseAuthorization()</c>. A minimal-API handler can take a <see cref="WebhookContext"/>
/// parameter, which is bound from the verified delivery and throws when there is none — so an
/// endpoint that takes one cannot run unverified even when the middleware was forgotten.
/// </para>
/// <para>
/// On an action it replaces the controller's verifier: a delivery comes from one sender.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequireWebhookSignatureAttribute(string verifier) : Attribute, IRequireWebhookSignatureMetadata
{
    /// <summary>Name of a verifier registered with <c>AddWebhooks(o => o.AddVerifier(...))</c>.</summary>
    public string Verifier { get; } = verifier;

    string? IRequireWebhookSignatureMetadata.VerifierName => this.Verifier;

    IWebhookVerifier? IRequireWebhookSignatureMetadata.Verifier => null;
}
