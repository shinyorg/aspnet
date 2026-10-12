using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Shiny.AspNet.FileSync.Internal;
using Shiny.AspNet.Tus;

namespace Shiny.AspNet.FileSync;

/// <summary>
/// The routes one <c>MapFileSync</c> call registered - its own and the tus upload routes, which
/// live in the same route group - so a policy is stated once for all of them. Every ASP.NET
/// convention (<c>RequireAuthorization</c>, <c>RequireCors</c>, <c>RequireRateLimiting</c>, ...)
/// applies.
/// <code>
/// app.MapFileSync("/sync", o => o.StoreSelector = ...).RequireAuthorization();
/// </code>
/// </summary>
public sealed class FileSyncEndpointConventionBuilder : IEndpointConventionBuilder
{
    readonly IEndpointConventionBuilder group;
    readonly FileSyncHandler handler;
    readonly FileSyncOptions options;
    readonly TusEndpointConventionBuilder? tus;

    internal FileSyncEndpointConventionBuilder(
        IEndpointConventionBuilder group,
        FileSyncHandler handler,
        FileSyncOptions options,
        TusEndpointConventionBuilder? tus
    )
    {
        this.group = group;
        this.handler = handler;
        this.options = options;
        this.tus = tus;
    }

    /// <summary>
    /// Deletes chunks no current file refers to and that are older than
    /// <see cref="FileSyncOptions.GarbageGracePeriod"/>: the chunks of deleted files and of old
    /// versions, and uploads that were never committed. Run it from a timer or a background job
    /// (a <c>BackgroundService</c>, say). Returns how many were removed.
    /// </summary>
    public Task<int> CollectGarbageAsync(IFileSyncStore? store = null, CancellationToken cancellationToken = default)
        => this.handler.CollectGarbageAsync(
            store ?? this.options.Store ?? throw new ArgumentNullException(nameof(store), "This endpoint picks stores per request; name the one to collect."),
            cancellationToken
        );

    /// <summary>
    /// Deletes tus pack uploads that were started and abandoned more than a day ago, now rather
    /// than at the next sweep. Zero when <see cref="FileSyncOptions.EnableTusUploads"/> is off.
    /// </summary>
    public Task<int> RemoveExpiredUploadsAsync(CancellationToken cancellationToken = default)
        => this.tus?.RemoveExpiredUploadsAsync(cancellationToken) ?? Task.FromResult(0);

    /// <inheritdoc />
    public void Add(Action<EndpointBuilder> convention)
        => this.group.Add(convention);

    /// <inheritdoc />
    public void Finally(Action<EndpointBuilder> finallyConvention)
        => this.group.Finally(finallyConvention);
}

/// <summary>
/// Dropbox-style file sync. Files are kept as content-defined chunks addressed by their SHA-256, so
/// a client that edits a page of a 500 MB file sends the few chunks that changed, and a copy or an
/// earlier version of a file costs nothing to send again. Pair it with
/// <c>Shiny.Net.HttpServer.FileSync.Client</c>.
/// <code>
/// app.MapFileSync("/sync", o => o.Store = new DiskFileSyncStore(Path.Combine(dataDir, "sync")))
///    .RequireAuthorization();
/// </code>
/// <para>Routes, under the prefix:</para>
/// <code>
/// GET    changes?cursor=&amp;limit=        what changed after a cursor
/// GET    changes/wait?cursor=&amp;timeout= long-poll until something does
/// GET    files/manifest?path=           a file's current version and chunk list
/// PUT    files/manifest?path=           commit a new version (409 stale, 422 missing chunks)
/// DELETE files/manifest?path=&amp;revision= delete (leaves a tombstone)
/// GET    files/content?path=            the whole file, for anything that is not a sync client
/// POST   chunks/missing                 which of these chunks the server lacks
/// PUT    chunks/pack                    upload a pack of chunks in one request
/// GET    chunks/pack?h=a,b,c            download chunks as a pack (resumable with Range)
/// GET    chunks/{hash}                  one chunk
/// *      uploads                        tus 1.0.0, for resumable pack uploads
/// </code>
/// Every <c>GET</c> answers <c>HEAD</c> too.
/// </summary>
public static class FileSyncEndpointRouteBuilderExtensions
{
    static readonly string[] Get = [HttpMethods.Get, HttpMethods.Head];

    /// <summary>Maps a sync endpoint at <paramref name="prefix"/>.</summary>
    public static FileSyncEndpointConventionBuilder MapFileSync(this IEndpointRouteBuilder endpoints, string prefix, Action<FileSyncOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new FileSyncOptions();
        configure(options);

        return endpoints.MapFileSync(prefix, options);
    }

    /// <summary>Maps a sync endpoint from options built elsewhere.</summary>
    public static FileSyncEndpointConventionBuilder MapFileSync(this IEndpointRouteBuilder endpoints, string prefix, FileSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Store is null && options.StoreSelector is null)
            throw new InvalidOperationException($"{nameof(FileSyncOptions)} needs a {nameof(FileSyncOptions.Store)} or a {nameof(FileSyncOptions.StoreSelector)}.");

        if (options.MaxChunkSize <= 0 || options.MaxPackSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Chunk and pack limits must be positive.");

        var group = endpoints.MapGroup(NormalizePrefix(prefix));
        var handler = new FileSyncHandler(options);

        group.MapMethods("/changes", Get, handler.Guard(handler.ChangesAsync));
        group.MapMethods("/changes/wait", Get, handler.Guard(handler.WaitAsync));
        group.MapMethods("/files/manifest", Get, handler.Guard(handler.GetManifestAsync));
        group.MapMethods("/files/manifest", [HttpMethods.Put], handler.Guard(handler.CommitAsync));
        group.MapMethods("/files/manifest", [HttpMethods.Delete], handler.Guard(handler.DeleteAsync));
        group.MapMethods("/files/content", Get, handler.Guard(handler.ContentAsync));
        group.MapMethods("/chunks/missing", [HttpMethods.Post], handler.Guard(handler.MissingAsync));
        group.MapMethods("/chunks/pack", [HttpMethods.Put], handler.Guard(handler.UploadPackAsync));
        group.MapMethods("/chunks/pack", Get, handler.Guard(handler.DownloadPackAsync));
        group.MapMethods("/chunks/{hash}", Get, handler.Guard(handler.DownloadChunkAsync));

        TusEndpointConventionBuilder? tus = null;

        // Nested in the same group, so whatever is applied to the sync endpoint - authorization
        // above all - covers the uploads too.
        if (options.EnableTusUploads)
        {
            tus = group.MapTus("/uploads", new TusOptions
            {
                Store = options.TusStore ?? DefaultTusStore(options),
                MaxSize = options.MaxPackSize,
                Expiration = TimeSpan.FromDays(1),
                OnUploadCompleteAsync = handler.IngestTusAsync
            });
        }

        // A protocol with its own client; an OpenAPI schema of it would help nobody.
        group.ExcludeFromDescription();

        return new FileSyncEndpointConventionBuilder(group, handler, options, tus);
    }

    static DiskTusStore DefaultTusStore(FileSyncOptions options)
        => new(options.Store is DiskFileSyncStore disk
            ? Path.Combine(disk.RootPath, "uploads")
            : Path.Combine(Path.GetTempPath(), "shiny-filesync-uploads"));

    static string NormalizePrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var trimmed = prefix.Trim().Trim('/');
        return trimmed.Length == 0 ? "/" : "/" + trimmed;
    }
}
