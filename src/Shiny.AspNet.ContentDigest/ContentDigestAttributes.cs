namespace Shiny.AspNet.ContentDigest;

/// <summary>
/// RFC 9530 digests for an MVC controller, one of its actions, or a minimal-API handler.
/// <code>
/// [Route("api/sync")]
/// public class SyncController : ControllerBase
/// {
///     [HttpPost("batch")] [ContentDigest(RequireRequestDigest = true)] public IActionResult Batch(...) => ...;
///     [HttpGet("snapshot")] [ContentDigest] public Snapshot Get() => ...;
/// }
///
/// app.MapGet("/snapshot", [ContentDigest] () => snapshot);
/// </code>
/// <para>
/// Needs <c>app.UseContentDigest()</c>. On an action it replaces the controller's settings rather than
/// adding to them — the most specific piece of <see cref="IContentDigestMetadata"/> on an endpoint is
/// the one that applies.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ContentDigestAttribute : Attribute, IContentDigestMetadata
{
    /// <summary>Refuses requests with a body but no verifiable <c>Content-Digest</c>. Off by default.</summary>
    public bool RequireRequestDigest { get; set; }

    /// <summary>Gives every response a <c>Content-Digest</c>, asked for or not. On by default.</summary>
    public bool AlwaysEmitResponseDigest { get; set; } = true;

    // Unset means "fall back to the options", which is what an attribute property left false says.
    bool? IContentDigestMetadata.RequireRequestDigest => this.RequireRequestDigest ? true : null;
    DigestEmission? IContentDigestMetadata.ResponseDigest => this.AlwaysEmitResponseDigest ? DigestEmission.Always : null;
    bool IContentDigestMetadata.Disabled => false;
}

/// <summary>Exempts an endpoint from digests, including from a controller-level <c>[ContentDigest]</c> or a group's convention.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DisableContentDigestAttribute : Attribute, IContentDigestMetadata
{
    bool? IContentDigestMetadata.RequireRequestDigest => null;
    DigestEmission? IContentDigestMetadata.ResponseDigest => null;
    bool IContentDigestMetadata.Disabled => true;
}

/// <summary>
/// What an endpoint asked for. <see cref="ContentDigestMiddleware"/> reads the last one in the
/// endpoint's metadata — the most specific — and null properties fall back to
/// <see cref="ContentDigestOptions"/>.
/// <para>
/// Implemented by <see cref="ContentDigestAttribute"/>, <see cref="DisableContentDigestAttribute"/>
/// and <see cref="ContentDigestMetadata"/> (what the <c>RequireContentDigest()</c>,
/// <c>WithContentDigest()</c> and <c>DisableContentDigest()</c> conventions add).
/// </para>
/// </summary>
public interface IContentDigestMetadata
{
    /// <summary>Overrides <see cref="ContentDigestOptions.RequireRequestDigest"/>.</summary>
    bool? RequireRequestDigest { get; }

    /// <summary>Overrides <see cref="ContentDigestOptions.ResponseDigest"/>.</summary>
    DigestEmission? ResponseDigest { get; }

    /// <summary>True when the endpoint opted out of both directions.</summary>
    bool Disabled { get; }
}
