using Microsoft.AspNetCore.Http;
using Shiny.AspNet.Tus;

namespace Shiny.AspNet.FileSync;

/// <summary>How a sync endpoint stores files and what it accepts.</summary>
public sealed class FileSyncOptions
{
    /// <summary>
    /// The store every request uses. Set this or <see cref="StoreSelector"/> - normally a
    /// <see cref="DiskFileSyncStore"/>.
    /// </summary>
    public IFileSyncStore? Store { get; set; }

    /// <summary>
    /// Picks a store per request instead of one for everybody - one per signed-in user, as Dropbox
    /// keeps one tree per account:
    /// <code>
    /// o.StoreSelector = ctx => stores.GetOrAdd(ctx.User.Identity!.Name!, name => new DiskFileSyncStore(Path.Combine(root, name)));
    /// </code>
    /// Chunks are never shared between stores, so one user cannot learn whether another holds a
    /// file by asking whether its chunks are missing.
    /// </summary>
    public Func<HttpContext, IFileSyncStore>? StoreSelector { get; set; }

    /// <summary>
    /// Largest chunk accepted. Default 4 MB, well above the client's default maximum of 1 MB.
    /// </summary>
    public int MaxChunkSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Largest pack accepted, through tus or a plain <c>PUT</c>. Default 64 MB. This is also the body
    /// limit of a plain <c>PUT</c>: the route raises Kestrel's <c>MaxRequestBodySize</c> (30 MB by
    /// default) to it for that one request, unless the app put a <c>RequestSizeLimitAttribute</c> of
    /// its own on the endpoint.
    /// </summary>
    public long MaxPackSize { get; set; } = 64L * 1024 * 1024;

    /// <summary>Most chunks one pack download may name. Default 256.</summary>
    public int MaxChunksPerDownload { get; set; } = 256;

    /// <summary>Most entries one page of the change feed returns. Default 1000.</summary>
    public int MaxChangesPerPage { get; set; } = 1000;

    /// <summary>The longest a long-poll for changes is held open. Default 90 seconds.</summary>
    public TimeSpan MaxWaitTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Accepts packs over tus at <c>{prefix}/uploads</c>, which is what the client uses by default so
    /// an interrupted upload resumes. On by default.
    /// </summary>
    public bool EnableTusUploads { get; set; } = true;

    /// <summary>
    /// Where packs are kept while their tus upload is in progress. Null uses <c>{root}/uploads</c>
    /// of a <see cref="DiskFileSyncStore"/>, or a folder under the temp directory otherwise.
    /// </summary>
    public ITusStore? TusStore { get; set; }

    /// <summary>
    /// How long a chunk nothing refers to is kept before <see cref="FileSyncEndpointConventionBuilder.CollectGarbageAsync"/>
    /// removes it. It covers a client that has uploaded a file's chunks but not yet committed it.
    /// Default one day.
    /// </summary>
    public TimeSpan GarbageGracePeriod { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Runs after every committed change - a new version or a delete - before the client hears back.</summary>
    public Func<FileSyncChangeContext, Task>? OnChangedAsync { get; set; }

    /// <summary>The clock used for garbage collection. Replace it in tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>A committed change, for <see cref="FileSyncOptions.OnChangedAsync"/>.</summary>
public sealed class FileSyncChangeContext
{
    internal FileSyncChangeContext(HttpContext httpContext, IFileSyncStore store, FileSyncEntry entry, FileSyncEntry? previous)
    {
        this.HttpContext = httpContext;
        this.Store = store;
        this.Entry = entry;
        this.Previous = previous;
    }

    public HttpContext HttpContext { get; }

    public IFileSyncStore Store { get; }

    /// <summary>The new version, or the tombstone of a delete.</summary>
    public FileSyncEntry Entry { get; }

    /// <summary>What was there before, if anything.</summary>
    public FileSyncEntry? Previous { get; }
}
