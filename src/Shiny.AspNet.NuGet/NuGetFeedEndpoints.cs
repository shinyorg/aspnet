using Microsoft.AspNetCore.Http.Metadata;
using Shiny.AspNet.NuGet.Internal;

namespace Shiny.AspNet.NuGet;

/// <summary>
/// What one <c>MapNuGetFeed</c> call returns: the feed's route group, so a policy can be stated once
/// for the whole feed with ASP.NET's own extensions - or only for the half that reads, or the half
/// that writes.
/// <code>
/// app.MapNuGetFeed("/nuget", o => { ... })
///    .ForReads(r => r.RequireAuthorization())   // a private feed: Basic credentials in nuget.config
///    .ForWrites(r => r.RequireRateLimiting("push"));
/// </code>
/// Anything applied to the builder itself (<c>.RequireAuthorization()</c>, <c>.RequireCors()</c>,
/// <c>.WithMetadata()</c>, ...) covers every route of the feed.
/// </summary>
public sealed class NuGetFeedEndpointConventionBuilder : IEndpointConventionBuilder
{
    readonly RouteGroupBuilder feed;

    internal NuGetFeedEndpointConventionBuilder(RouteGroupBuilder feed, RouteGroupBuilder reads, RouteGroupBuilder writes, string serviceIndexPath)
    {
        this.feed = feed;
        this.Reads = reads;
        this.Writes = writes;
        this.ServiceIndexPath = serviceIndexPath;
    }

    /// <summary>
    /// The path of the service index, <c>{prefix}/v3/index.json</c>, relative to the route builder
    /// the feed was mapped onto - the URL a client is given as the package source; it finds every
    /// other route from there.
    /// </summary>
    public string ServiceIndexPath { get; }

    /// <summary>Service index, package content, registrations, search and autocomplete.</summary>
    public IEndpointConventionBuilder Reads { get; }

    /// <summary>Push, delete and relist. Holds no routes for a <see cref="NuGetFeedOptions.ReadOnly"/> feed.</summary>
    public IEndpointConventionBuilder Writes { get; }

    /// <summary>Runs <paramref name="configure"/> against the read routes.</summary>
    public NuGetFeedEndpointConventionBuilder ForReads(Action<IEndpointConventionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(this.Reads);
        return this;
    }

    /// <summary>Runs <paramref name="configure"/> against the write routes.</summary>
    public NuGetFeedEndpointConventionBuilder ForWrites(Action<IEndpointConventionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(this.Writes);
        return this;
    }

    /// <inheritdoc />
    public void Add(Action<EndpointBuilder> convention) => ((IEndpointConventionBuilder)this.feed).Add(convention);

    /// <inheritdoc />
    public void Finally(Action<EndpointBuilder> finallyConvention) => ((IEndpointConventionBuilder)this.feed).Finally(finallyConvention);
}

/// <summary>
/// A private NuGet feed (https://learn.microsoft.com/nuget/api/overview) - the V3 protocol that
/// <c>dotnet restore</c>, <c>dotnet nuget push</c>, Visual Studio and Rider speak, with nothing to
/// install on the client side beyond a package source.
/// <code>
/// app.MapNuGetFeed("/nuget", o =>
/// {
///     o.Store = new DiskNuGetPackageStore(Path.Combine(dataDir, "packages"));
///     o.ApiKeys.Add(builder.Configuration["NuGet:ApiKey"]!);
/// });
///
/// // dotnet nuget add source http://host:5000/nuget/v3/index.json -n mine
/// // dotnet nuget push My.Package.1.0.0.nupkg -s mine -k {key}
/// </code>
/// </summary>
public static class NuGetFeedExtensions
{
    /// <summary>Maps a NuGet feed at <paramref name="prefix"/>.</summary>
    public static NuGetFeedEndpointConventionBuilder MapNuGetFeed(this IEndpointRouteBuilder endpoints, string prefix, Action<NuGetFeedOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new NuGetFeedOptions();
        configure(options);

        return endpoints.MapNuGetFeed(prefix, options);
    }

    /// <summary>Maps a NuGet feed from options built elsewhere.</summary>
    public static NuGetFeedEndpointConventionBuilder MapNuGetFeed(this IEndpointRouteBuilder endpoints, string prefix, NuGetFeedOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxPackageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(NuGetFeedOptions.MaxPackageSize)} must be positive.");

        // A feed that takes pushes from anyone is almost never what was meant, and the failure is
        // silent until someone else's package turns up in a build.
        if (!options.ReadOnly && options.RequireApiKey && options.ApiKeys.Count == 0 && options.ValidateApiKeyAsync is null)
            throw new InvalidOperationException(
                $"A NuGet feed that accepts pushes needs {nameof(NuGetFeedOptions.ApiKeys)} or {nameof(NuGetFeedOptions.ValidateApiKeyAsync)}. " +
                $"Set {nameof(NuGetFeedOptions.ReadOnly)} for a feed nobody pushes to, or {nameof(NuGetFeedOptions.RequireApiKey)} = false " +
                "when the write routes are protected by authorization instead."
            );

        var normalized = NormalizePrefix(prefix);
        var handler = new NuGetFeedHandler(options, options.ResolveStore());

        // Two empty-prefix groups inside the feed's group: conventions on the feed reach both, and
        // each half can be given its own.
        var feed = endpoints.MapGroup(normalized);
        var reads = feed.MapGroup(string.Empty);
        var writes = feed.MapGroup(string.Empty);

        // GET and HEAD: a client may ask whether a package is there without downloading it.
        string[] read = [HttpMethods.Get, HttpMethods.Head];

        reads.MapMethods(NuGetFeedHandler.ServiceIndexPath, read, handler.Guard(handler.ServiceIndexAsync));
        reads.MapMethods(NuGetFeedHandler.PackageBasePath + "/{id}/index.json", read, handler.Guard(handler.VersionListAsync));
        reads.MapMethods(NuGetFeedHandler.PackageBasePath + "/{id}/{version}/{file}", read, handler.Guard(handler.DownloadAsync));
        reads.MapMethods(NuGetFeedHandler.RegistrationPath + "/{id}/{leaf}", read, handler.Guard(handler.RegistrationAsync));
        reads.MapMethods(NuGetFeedHandler.SearchPath, read, handler.Guard(handler.SearchAsync));
        reads.MapMethods(NuGetFeedHandler.AutocompletePath, read, handler.Guard(handler.AutocompleteAsync));

        if (!options.ReadOnly)
        {
            writes.MapPut(NuGetFeedHandler.PublishPath, handler.Guard(handler.PushAsync));
            writes.MapDelete(NuGetFeedHandler.PublishPath + "/{id}/{version}", handler.Guard(handler.DeleteAsync));
            writes.MapPost(NuGetFeedHandler.PublishPath + "/{id}/{version}", handler.Guard(handler.RelistAsync));

            // Kestrel's own limit (30 MB) would refuse most packages long before MaxPackageSize had
            // a say, so the write routes carry a limit of their own: the package plus room for the
            // multipart framing around it. Routing applies it before the body is read; a later
            // .WithMetadata(new RequestSizeLimitAttribute(...)) on Writes overrides it.
            writes.WithMetadata(new RequestSizeLimit(options.MaxPackageSize + NuGetFeedHandler.MultipartAllowance));
        }

        // A protocol documented by NuGet rather than by an OpenAPI schema no NuGet client would read.
        feed.ExcludeFromDescription();

        return new NuGetFeedEndpointConventionBuilder(feed, reads, writes, normalized.TrimEnd('/') + NuGetFeedHandler.ServiceIndexPath);
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
