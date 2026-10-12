using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;

namespace Shiny.AspNet.ContentDigest.Internal;

/// <summary>
/// Hashes the response body on its way out and attaches the digest — as a header when the body was
/// small enough to hold, as a trailer when it streamed. Swapped in as the
/// <see cref="IHttpResponseBodyFeature"/> for the length of the request, so <c>Response.Body</c>,
/// <c>Response.BodyWriter</c>, <c>SendFileAsync</c> and <c>StartAsync</c> all come through here.
/// <para>
/// Holding the body first is what makes the header form possible at all: a header goes out before
/// the first byte of body, and a digest cannot be known until after the last. A small response is
/// held, hashed and sent with the digest in its headers — and with a <c>Content-Length</c>, as a
/// side effect. A large one, or one whose handler started the response deliberately, streams and gets
/// the digest in the trailer section instead.
/// </para>
/// </summary>
sealed class DigestingResponseBody : IHttpResponseBodyFeature
{
    readonly HttpResponse response;
    readonly IHttpResponseBodyFeature inner;
    readonly int maxBuffered;
    readonly string? contentAlgorithm;
    readonly string? reprAlgorithm;

    // Set when this sits inside response compression, which means the bytes seen here are not the
    // bytes on the wire. A digest of the wrong bytes is worse than none. ASP.NET's compression body is
    // both the body feature and the IHttpsCompressionFeature, which is how it is recognised.
    readonly bool insideCompression;

    ArrayBufferWriter<byte>? buffer = new(1024);
    IncrementalHash? contentHash;
    IncrementalHash? reprHash;
    DigestBodyStream? stream;
    PipeWriter? writer;
    bool streaming;
    bool bufferingDisabled;
    bool trailersAllowed;
    bool finished;

    public DigestingResponseBody(
        HttpResponse response,
        IHttpResponseBodyFeature inner,
        int maxBuffered,
        string? contentAlgorithm,
        string? reprAlgorithm
    )
    {
        this.response = response;
        this.inner = inner;
        this.insideCompression = inner is IHttpsCompressionFeature;
        this.maxBuffered = maxBuffered;
        this.contentAlgorithm = contentAlgorithm;
        this.reprAlgorithm = reprAlgorithm;
    }

    // Always our own stream, even once streaming: every byte has to pass the hash.
    public Stream Stream => this.stream ??= new DigestBodyStream(this);

    public PipeWriter Writer => this.writer ??= PipeWriter.Create(this.Stream, new StreamPipeWriterOptions(leaveOpen: true));

    /// <summary>
    /// A handler turning buffering off (server-sent events, long polling) wants every write on the
    /// wire as it happens, so holding stops at the next write or <see cref="StartAsync"/>, and the
    /// digest — if any — becomes a trailer.
    /// </summary>
    public void DisableBuffering()
    {
        this.bufferingDisabled = true;
        this.inner.DisableBuffering();
    }

    /// <summary>
    /// Starting the response is <b>not</b> taken as a wish to stream: ASP.NET's own
    /// <c>Response.WriteAsync(string)</c> starts the response before every first write, so while the
    /// body is being held this does nothing, and the headers go when the held body does. A handler
    /// that needs its headers on the wire now — server-sent events, long polling — turns buffering off
    /// first (<see cref="DisableBuffering"/>), which every streaming result in ASP.NET already does
    /// for response compression's sake.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!this.streaming && !this.finished && !this.bufferingDisabled)
            return;

        await this.SpillAsync(cancellationToken).ConfigureAwait(false);
        await this.inner.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Files go through the hash like any other body, so they cannot use the server's zero-copy path.</summary>
    public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
        => SendFileFallback.SendFileAsync(this.Stream, path, offset, count, cancellationToken);

    /// <summary>
    /// A handler completing the response early: whatever was held goes out with its digest first,
    /// then the response is completed underneath.
    /// </summary>
    public async Task CompleteAsync()
    {
        if (this.writer is { } pipe)
            await pipe.FlushAsync().ConfigureAwait(false);

        await this.FinishAsync(this.response.HttpContext.RequestAborted).ConfigureAwait(false);
        await this.inner.CompleteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Sends whatever was held, with its digest in the headers, or attaches the digest of a streamed
    /// body as a trailer. Called by the middleware once the handler returns, before it puts the
    /// original body feature back.
    /// </summary>
    public async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (this.finished)
            return;

        // Anything still sitting in the PipeWriter belongs to the body, and so to the digest.
        if (this.writer is { } pipe)
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

        this.finished = true;

        if (this.streaming)
        {
            if (this.trailersAllowed && this.CanDescribe(bodyWritten: true))
            {
                if (this.contentHash is { } content)
                    this.response.AppendTrailer(DigestFields.ContentDigest, DigestFields.Format(this.contentAlgorithm!, content.GetHashAndReset()));

                if (this.reprHash is { } repr && this.IsFullRepresentation())
                    this.response.AppendTrailer(DigestFields.ReprDigest, DigestFields.Format(this.reprAlgorithm!, repr.GetHashAndReset()));
            }

            this.DisposeHashes();
            return;
        }

        var held = this.buffer!;
        this.buffer = null;

        if (!this.response.HasStarted)
        {
            if (this.CanDescribe(bodyWritten: false))
            {
                var headers = this.response.Headers;

                // A digest the handler set itself — a precomputed one for a file, say — is left alone.
                if (this.contentAlgorithm is { } content && !headers.ContainsKey(DigestFields.ContentDigest))
                    headers[DigestFields.ContentDigest] = DigestFields.Compute(held.WrittenSpan, content);

                if (this.reprAlgorithm is { } repr && this.IsFullRepresentation() && !headers.ContainsKey(DigestFields.ReprDigest))
                    headers[DigestFields.ReprDigest] = DigestFields.Compute(held.WrittenSpan, repr);
            }

            if (held.WrittenCount > 0)
                this.response.ContentLength ??= held.WrittenCount;
        }

        if (held.WrittenCount > 0)
            await this.inner.Stream.WriteAsync(held.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Switches to streaming: sends what was held and hashes from here on.</summary>
    async ValueTask SpillAsync(CancellationToken cancellationToken)
    {
        if (this.streaming || this.finished)
            return;

        this.streaming = true;

        // A trailer needs a server that sends them — Kestrel does on HTTP/2 and HTTP/3, not on HTTP/1.1 —
        // and, on HTTP/1.1, a chunked body, which it is exactly when no length was declared. HTTP/1.0
        // has no trailers. Without one, a streamed body goes out with no digest: a trailer announced
        // and never sent would read as a digest that was stripped on the way.
        var protocol = this.response.HttpContext.Request.Protocol;

        this.trailersAllowed = this.response.SupportsTrailers()
            && !HttpProtocol.IsHttp10(protocol)
            && (!HttpProtocol.IsHttp11(protocol) || this.response.ContentLength is null);

        if (this.trailersAllowed)
        {
            if (this.contentAlgorithm is { } content)
                this.contentHash = DigestFields.CreateHash(content);

            if (this.reprAlgorithm is { } repr)
                this.reprHash = DigestFields.CreateHash(repr);

            // An HTTP/1.1 intermediary may drop a trailer it was not told to expect. Announcing it is
            // only possible before the headers go, which is now or never.
            if (!this.response.HasStarted)
            {
                if (this.contentHash is not null)
                    this.response.DeclareTrailer(DigestFields.ContentDigest);

                if (this.reprHash is not null)
                    this.response.DeclareTrailer(DigestFields.ReprDigest);
            }
        }

        var held = this.buffer!;
        this.buffer = null;

        if (held.WrittenCount > 0)
        {
            this.Hash(held.WrittenSpan);
            await this.inner.Stream.WriteAsync(held.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }
    }

    async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!this.streaming && !this.finished)
        {
            if (!this.bufferingDisabled && this.buffer!.WrittenCount + data.Length <= this.maxBuffered)
            {
                this.buffer.Write(data.Span);
                return;
            }

            await this.SpillAsync(cancellationToken).ConfigureAwait(false);
        }

        this.Hash(data.Span);
        await this.inner.Stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    void Write(ReadOnlySpan<byte> data)
    {
        if (!this.streaming && !this.finished && !this.bufferingDisabled && this.buffer!.WrittenCount + data.Length <= this.maxBuffered)
        {
            this.buffer.Write(data);
            return;
        }

        // Kestrel refuses synchronous I/O unless AllowSynchronousIO is on; it is the handler's choice
        // to write synchronously, so the same rules apply as if this stream were not here.
        this.WriteAsync(data.ToArray(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    Task FlushAsync(CancellationToken cancellationToken)
    {
        // Deliberately not forwarded while holding: a flush would start the response and defeat the
        // point of holding it. A handler that needs the bytes out calls StartAsync.
        return this.streaming
            ? this.inner.Stream.FlushAsync(cancellationToken)
            : Task.CompletedTask;
    }

    void Hash(ReadOnlySpan<byte> data)
    {
        this.contentHash?.AppendData(data);
        this.reprHash?.AppendData(data);
    }

    /// <summary>
    /// Whether a digest can honestly describe this response: it has content, and the content is the
    /// bytes this body saw rather than a compressed version of them.
    /// <para>
    /// Compression decides on the first byte it is given, so before the held body has been written
    /// through there is no way to know whether it will compress — and a guess that is wrong ships a
    /// digest that fails every check. Inside compression, a held response goes without; a streamed one
    /// gets its trailer only if compression declined (no <c>Content-Encoding</c> on the response).
    /// </para>
    /// </summary>
    bool CanDescribe(bool bodyWritten)
    {
        var status = this.response.StatusCode;

        if (status is < 200 or StatusCodes.Status204NoContent or StatusCodes.Status304NotModified)
            return false;

        if (!this.insideCompression)
            return true;

        return bodyWritten && !this.response.Headers.ContainsKey(HeaderNames.ContentEncoding);
    }

    /// <summary>
    /// A digest of the content is also a digest of the representation when the content is the whole
    /// of it — not a range of it.
    /// </summary>
    bool IsFullRepresentation()
        => this.response.StatusCode != StatusCodes.Status206PartialContent
            && !this.response.Headers.ContainsKey(HeaderNames.ContentRange);

    void DisposeHashes()
    {
        this.contentHash?.Dispose();
        this.reprHash?.Dispose();
    }

    sealed class DigestBodyStream(DigestingResponseBody owner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
            => owner.Write(new ReadOnlySpan<byte>(buffer, offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => owner.Write(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => owner.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => owner.WriteAsync(buffer, cancellationToken);

        public override void Flush() => owner.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override Task FlushAsync(CancellationToken cancellationToken) => owner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
