using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace Shiny.AspNet.Idempotency;

/// <summary>
/// Keeps keys and their responses in an <see cref="IDistributedCache"/> — Redis, SQL Server, or anything
/// else registered as one — so a retry that lands on a different instance behind a load balancer still
/// finds the first request.
/// <code>
/// services.AddStackExchangeRedisCache(o => o.Configuration = "...");
/// services.AddIdempotency().AddDistributedCacheIdempotencyStore();
/// </code>
/// <para>
/// <b>Atomicity.</b> <see cref="IDistributedCache"/> has no "set if absent", so a reservation here is
/// check, write, then read back: the write that is still there on the read-back holds the key, and any
/// other claimant backs off. Within one process reservations are additionally serialised, so they are
/// exact. Across processes two claims can still both win if each one's read-back lands before the
/// other's write — a window of one cache round trip. That is far narrower than having no shared store,
/// but it is not the guarantee <see cref="IIdempotencyStore"/> asks for. Where a double execution is
/// unacceptable (money moving), implement <see cref="IIdempotencyStore"/> over a primitive that is atomic
/// — Redis <c>SET NX</c>, a unique-key insert — instead.
/// </para>
/// <para>
/// Records are written in a small versioned binary format, and cache keys are a SHA-256 of the scoped
/// key, so user identifiers never appear in the cache's key space.
/// </para>
/// </summary>
public sealed class DistributedCacheIdempotencyStore : IIdempotencyStore
{
    const byte FormatVersion = 1;

    readonly IDistributedCache cache;
    readonly string keyPrefix;
    readonly SemaphoreSlim gate = new(1, 1);

    /// <param name="cache">Where the records live.</param>
    /// <param name="keyPrefix">Prepended to every cache key, to keep these apart from anything else in the same cache.</param>
    public DistributedCacheIdempotencyStore(IDistributedCache cache, string keyPrefix = "shiny:idempotency:")
    {
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.keyPrefix = keyPrefix ?? throw new ArgumentNullException(nameof(keyPrefix));
    }

    public async ValueTask<IdempotencyRecord?> TryReserveAsync(string key, string fingerprint, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var cacheKey = this.CacheKey(key);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;

            if (Read(await this.cache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false)) is { } existing
                && !existing.Record.IsExpired(now))
            {
                return existing.Record;
            }

            var owner = Guid.NewGuid();
            var claim = new IdempotencyRecord(fingerprint, null, now + lease);

            await this.cache.SetAsync(
                cacheKey,
                Write(claim, owner),
                new DistributedCacheEntryOptions { AbsoluteExpiration = claim.Expires },
                cancellationToken
            ).ConfigureAwait(false);

            // Read back: if another instance wrote its claim over ours in the meantime, it holds the key.
            if (Read(await this.cache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false)) is { } after
                && after.Owner != owner)
            {
                return after.Record;
            }

            return null;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask CompleteAsync(string key, IdempotencyRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(record);

        await this.cache.SetAsync(
            this.CacheKey(key),
            Write(record, Guid.Empty),
            new DistributedCacheEntryOptions { AbsoluteExpiration = record.Expires },
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async ValueTask ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        var cacheKey = this.CacheKey(key);

        // Only a claim is released. A completed record under the same key belongs to a request that
        // finished; releasing it would let the next retry run the handler again.
        if (Read(await this.cache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false)) is { Record.InFlight: true })
            await this.cache.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);
    }

    string CacheKey(string key)
        => this.keyPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    internal static byte[] Write(IdempotencyRecord record, Guid owner)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FormatVersion);
            writer.Write(owner.ToByteArray());
            writer.Write(record.Fingerprint);
            writer.Write(record.Expires.UtcTicks);

            if (record.Response is not { } response)
            {
                writer.Write(false);
            }
            else
            {
                writer.Write(true);
                writer.Write(response.StatusCode);
                writer.Write(response.Created.UtcTicks);
                writer.Write(response.Headers.Count);

                foreach (var (name, value) in response.Headers)
                {
                    writer.Write(name);
                    writer.Write(value);
                }

                writer.Write(response.Body.Length);
                writer.Write(response.Body);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Null for nothing, or for anything this version cannot read — treated as no record at all.</summary>
    internal static (IdempotencyRecord Record, Guid Owner)? Read(byte[]? data)
    {
        if (data is not { Length: > 0 } || data[0] != FormatVersion)
            return null;

        try
        {
            using var reader = new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8);
            reader.ReadByte();

            var owner = new Guid(reader.ReadBytes(16));
            var fingerprint = reader.ReadString();
            var expires = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            IdempotentResponse? response = null;

            if (reader.ReadBoolean())
            {
                var status = reader.ReadInt32();
                var created = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
                var count = reader.ReadInt32();
                var headers = new List<KeyValuePair<string, string>>(count);

                for (var i = 0; i < count; i++)
                    headers.Add(new KeyValuePair<string, string>(reader.ReadString(), reader.ReadString()));

                var body = reader.ReadBytes(reader.ReadInt32());
                response = new IdempotentResponse(status, headers, body, created);
            }

            return (new IdempotencyRecord(fingerprint, response, expires), owner);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }
}
