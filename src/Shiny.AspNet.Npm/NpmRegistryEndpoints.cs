using Microsoft.AspNetCore.Http.Metadata;
using Shiny.AspNet.Npm.Internal;

namespace Shiny.AspNet.Npm;

/// <summary>
/// What one <c>MapNpmRegistry</c> call returns: the registry's route group, so a policy is stated
/// once for the whole registry with ASP.NET's own extensions - or for the half that reads
/// (<c>GET</c>/<c>HEAD</c>) or the half that writes.
/// <code>
/// app.MapNpmRegistry("/npm", o => { ... })
///    .ForWrites(w => w.RequireRateLimiting("publish"));
/// </code>
/// </summary>
public sealed class NpmRegistryEndpointConventionBuilder : IEndpointConventionBuilder
{
    readonly RouteGroupBuilder registry;

    internal NpmRegistryEndpointConventionBuilder(RouteGroupBuilder registry, RouteGroupBuilder reads, RouteGroupBuilder writes)
    {
        this.registry = registry;
        this.Reads = reads;
        this.Writes = writes;
    }

    /// <summary>Install, metadata, search, whoami.</summary>
    public IEndpointConventionBuilder Reads { get; }

    /// <summary>Publish, unpublish, deprecate, dist-tag and owner changes - and <c>npm login</c>, which is a <c>PUT</c>.</summary>
    public IEndpointConventionBuilder Writes { get; }

    /// <summary>Runs <paramref name="configure"/> against the read routes.</summary>
    public NpmRegistryEndpointConventionBuilder ForReads(Action<IEndpointConventionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(this.Reads);
        return this;
    }

    /// <summary>Runs <paramref name="configure"/> against the write routes.</summary>
    public NpmRegistryEndpointConventionBuilder ForWrites(Action<IEndpointConventionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(this.Writes);
        return this;
    }

    /// <inheritdoc />
    public void Add(Action<EndpointBuilder> convention) => ((IEndpointConventionBuilder)this.registry).Add(convention);

    /// <inheritdoc />
    public void Finally(Action<EndpointBuilder> finallyConvention) => ((IEndpointConventionBuilder)this.registry).Finally(finallyConvention);
}

/// <summary>
/// A private npm registry - the API npm, pnpm and Yarn use - mapped onto the app.
/// <code>
/// app.MapNpmRegistry("/npm", o =>
/// {
///     o.Store = new DiskNpmPackageStore(Path.Combine(dataDir, "npm"));
///     o.Tokens[builder.Configuration["Npm:Token"]!] = "ci";
/// });
///
/// // .npmrc
/// // @acme:registry=http://host:5000/npm/
/// // //host:5000/npm/:_authToken=${NPM_TOKEN}
/// </code>
/// </summary>
public static class NpmRegistryExtensions
{
    /// <summary>Maps an npm registry at <paramref name="prefix"/>.</summary>
    public static NpmRegistryEndpointConventionBuilder MapNpmRegistry(this IEndpointRouteBuilder endpoints, string prefix, Action<NpmRegistryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new NpmRegistryOptions();
        configure(options);

        return endpoints.MapNpmRegistry(prefix, options);
    }

    /// <summary>Maps an npm registry from options built elsewhere.</summary>
    public static NpmRegistryEndpointConventionBuilder MapNpmRegistry(this IEndpointRouteBuilder endpoints, string prefix, NpmRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxTarballSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(NpmRegistryOptions.MaxTarballSize)} must be positive.");

        // A registry anyone can publish to is a supply-chain incident waiting for a name.
        if (!options.ReadOnly && options.Tokens.Count == 0 && options.ValidateTokenAsync is null)
            throw new InvalidOperationException(
                $"An npm registry that accepts publishes needs {nameof(NpmRegistryOptions.Tokens)} or {nameof(NpmRegistryOptions.ValidateTokenAsync)}. " +
                $"Set {nameof(NpmRegistryOptions.ReadOnly)} for one nobody publishes to."
            );

        var handler = new NpmRegistryHandler(options, options.ResolveStore());
        RequestDelegate handle = handler.HandleAsync;

        // Two empty-prefix groups inside the registry's group: conventions on the registry reach
        // both, and each half can be given its own.
        var registry = endpoints.MapGroup(NormalizePrefix(prefix));
        var reads = registry.MapGroup(string.Empty);
        var writes = registry.MapGroup(string.Empty);

        reads.MapMethods("/{*path}", [HttpMethods.Get, HttpMethods.Head], handle);
        writes.MapMethods("/{*path}", [HttpMethods.Put, HttpMethods.Post, HttpMethods.Delete], handle);

        // A publish is the tarball in base64 inside JSON: a third bigger than the tarball, plus the
        // manifest. Kestrel's default limit (30 MB) would refuse it long before MaxTarballSize had a
        // say, so the write routes carry their own; routing applies it before the body is read, and
        // a later .WithMetadata(new RequestSizeLimitAttribute(...)) on Writes overrides it.
        writes.WithMetadata(new RequestSizeLimit(NpmRegistryHandler.MaxPublishBodySize(options)));

        // A protocol documented by npm rather than by an OpenAPI schema no npm client would read.
        registry.ExcludeFromDescription();

        return new NpmRegistryEndpointConventionBuilder(registry, reads, writes);
    }

    static string NormalizePrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var trimmed = prefix.Trim().Trim('/');
        return trimmed.Length == 0 ? "/" : "/" + trimmed;
    }

    sealed class RequestSizeLimit(long maxRequestBodySize) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize { get; } = maxRequestBodySize;
    }
}
