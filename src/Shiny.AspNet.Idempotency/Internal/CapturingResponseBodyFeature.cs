using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;

namespace Shiny.AspNet.Idempotency.Internal;

/// <summary>
/// Copies the response body as it goes out, so it can be stored for replay.
/// <para>
/// Installed as the request's <see cref="IHttpResponseBodyFeature"/> for the duration of the handler,
/// so <c>Response.Body</c>, <c>Response.BodyWriter</c> and <c>SendFileAsync</c> all come through here.
/// It is a tee, not a buffer: every byte is written through as the handler writes it, and a copy is
/// kept on the side. Holding the body back instead is not an option on ASP.NET Core —
/// <c>Response.WriteAsync</c> calls <c>StartAsync</c> on every write to a response that has not
/// started, so a deliberate "start streaming now" cannot be told apart from an ordinary write, and
/// holding would turn an event stream into a hang. The same choice ASP.NET's own output cache makes.
/// </para>
/// <para>
/// What the handler declared is what goes out: no <c>Content-Length</c> is added or changed, and
/// <c>StartAsync</c>, flushes and <c>DisableBuffering</c> are passed straight on. The copy has a hard
/// limit — past it, the copy is dropped and the rest streams untouched, and the response cannot be
/// replayed.
/// </para>
/// </summary>
sealed class CapturingResponseBodyFeature : IHttpResponseBodyFeature
{
    readonly IHttpResponseBodyFeature inner;
    readonly int maxBytes;

    ArrayBufferWriter<byte>? copy = new(1024);
    TeeStream? stream;
    PipeWriter? writer;
    bool finished;

    public CapturingResponseBodyFeature(IHttpResponseBodyFeature inner, int maxBytes)
    {
        this.inner = inner;
        this.maxBytes = maxBytes;
    }

    /// <summary>The complete body, or null when it was larger than the limit.</summary>
    public byte[]? Captured { get; private set; }

    public Stream Stream => this.stream ??= new TeeStream(this);

    // Over the tee rather than the inner writer, so bytes written through BodyWriter (JSON results,
    // WriteAsync) are copied too.
    public PipeWriter Writer => this.writer ??= PipeWriter.Create(this.Stream, new StreamPipeWriterOptions(leaveOpen: true));

    public void DisableBuffering() => this.inner.DisableBuffering();

    public Task StartAsync(CancellationToken cancellationToken = default) => this.inner.StartAsync(cancellationToken);

    public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
    {
        if (this.copy is null || this.finished)
        {
            await this.FlushWriterAsync().ConfigureAwait(false);
            await this.inner.SendFileAsync(path, offset, count, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Read through the tee so the file is copied like any other body. A file past the limit
        // costs the zero-copy send; idempotent endpoints are meant for small confirmations.
        await SendFileFallback.SendFileAsync(this.Stream, path, offset, count, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync()
    {
        await this.FinishAsync().ConfigureAwait(false);
        await this.inner.CompleteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Pushes out anything still sitting in <see cref="Writer"/> and settles <see cref="Captured"/>.
    /// Called by the middleware once the handler returns, before the original feature is put back —
    /// anything left in this feature's pipe after that would never reach the client.
    /// </summary>
    public async Task FinishAsync()
    {
        if (this.finished)
            return;

        await this.FlushWriterAsync().ConfigureAwait(false);

        this.finished = true;
        this.Captured = this.copy?.WrittenSpan.ToArray();
        this.copy = null;
    }

    async Task FlushWriterAsync()
    {
        // Completing a StreamPipeWriter flushes it into the tee and leaves the stream open (leaveOpen).
        // It is a no-op when the handler already completed it.
        if (this.writer is { } pipe)
        {
            this.writer = null;
            await pipe.CompleteAsync().ConfigureAwait(false);
        }
    }

    async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (this.copy is { } held && !this.finished)
        {
            if (held.WrittenCount + data.Length > this.maxBytes)
                this.copy = null; // Too big to store; the rest just streams.
            else
                held.Write(data.Span);
        }

        await this.inner.Stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    void Write(ReadOnlySpan<byte> data)
    {
        if (this.copy is { } held && !this.finished)
        {
            if (held.WrittenCount + data.Length > this.maxBytes)
                this.copy = null;
            else
                held.Write(data);
        }

        // Synchronous IO is the server's call (AllowSynchronousIO); the inner stream enforces it.
        this.inner.Stream.Write(data);
    }

    sealed class TeeStream(CapturingResponseBodyFeature owner) : Stream
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

        public override void Flush() => owner.inner.Stream.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => owner.inner.Stream.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
