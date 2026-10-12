using System.Buffers;
using System.Buffers.Binary;
using System.Net.Security;
using System.Text;

namespace Shiny.AspNet.Acme.Internal;

/// <summary>
/// What a client said in its TLS ClientHello, read before the handshake begins.
/// <para>
/// .NET hands a server the SNI name at certificate-selection time, but never the ALPN protocols the
/// client offered — and a TLS-ALPN-01 challenge (RFC 8737) is recognised by exactly that: a
/// ClientHello offering <c>acme-tls/1</c>. Kestrel sniffs the hello for us
/// (<c>HttpsConnectionAdapterOptions.TlsClientHelloBytesCallback</c>) without consuming it; this is
/// parsed from those bytes and parked on the connection until <c>OnAuthenticate</c> builds the
/// handshake options.
/// </para>
/// </summary>
sealed class TlsClientHello
{
    public TlsClientHello(string? serverName, IReadOnlyList<SslApplicationProtocol> applicationProtocols)
    {
        this.ServerName = serverName;
        this.ApplicationProtocols = applicationProtocols;
    }

    /// <summary>The SNI host name, or null when the client sent none.</summary>
    public string? ServerName { get; }

    /// <summary>The ALPN protocols the client offered, in its order of preference. Empty when it offered none.</summary>
    public IReadOnlyList<SslApplicationProtocol> ApplicationProtocols { get; }
}

/// <summary>
/// Parses the ClientHello record Kestrel hands over. Anything it does not understand — a malformed or
/// truncated hello, one split across records — yields null, and the handshake carries on exactly as
/// it would have without this in front of it: this is an observer, never a gatekeeper.
/// </summary>
static class TlsClientHelloParser
{
    const byte HandshakeRecord = 22;
    const byte ClientHelloMessage = 1;
    const ushort ServerNameExtension = 0;
    const ushort AlpnExtension = 16;

    /// <summary>Parses a TLS record (5-byte record header included) holding a ClientHello.</summary>
    public static TlsClientHello? Parse(ReadOnlySequence<byte> record)
    {
        // A hello is a few hundred bytes, a couple of KiB with post-quantum key shares — almost always
        // one segment, so copying is the exception.
        ReadOnlySpan<byte> bytes = record.IsSingleSegment ? record.FirstSpan : record.ToArray();
        return Parse(bytes);
    }

    internal static TlsClientHello? Parse(ReadOnlySpan<byte> record)
    {
        if (record.Length < 9 || record[0] != HandshakeRecord || record[5] != ClientHelloMessage)
            return null;

        var messageLength = (record[6] << 16) | (record[7] << 8) | record[8];
        if (record.Length < 9 + messageLength)
            return null;

        return ParseBody(record.Slice(9, messageLength));
    }

    internal static TlsClientHello? ParseBody(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new SpanReader(body);

            reader.Skip(2);                      // legacy_version
            reader.Skip(32);                     // random
            reader.Skip(reader.ReadUInt8());     // legacy_session_id
            reader.Skip(reader.ReadUInt16());    // cipher_suites
            reader.Skip(reader.ReadUInt8());     // legacy_compression_methods

            string? serverName = null;
            var protocols = new List<SslApplicationProtocol>();

            if (reader.Remaining == 0)
                return new TlsClientHello(null, protocols);

            var extensions = new SpanReader(reader.ReadBytes(reader.ReadUInt16()));
            while (extensions.Remaining > 0)
            {
                var type = extensions.ReadUInt16();
                var data = new SpanReader(extensions.ReadBytes(extensions.ReadUInt16()));

                if (type == ServerNameExtension)
                {
                    var list = new SpanReader(data.ReadBytes(data.ReadUInt16()));
                    while (list.Remaining > 0)
                    {
                        var nameType = list.ReadUInt8();
                        var name = list.ReadBytes(list.ReadUInt16());
                        if (nameType == 0)
                            serverName = Encoding.ASCII.GetString(name);
                    }
                }
                else if (type == AlpnExtension)
                {
                    var list = new SpanReader(data.ReadBytes(data.ReadUInt16()));
                    while (list.Remaining > 0)
                        protocols.Add(new SslApplicationProtocol(list.ReadBytes(list.ReadUInt8()).ToArray()));
                }
            }

            return new TlsClientHello(serverName, protocols);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Truncated or malformed. Not ours to reject — SslStream will, on the same bytes.
            return null;
        }
    }

    ref struct SpanReader(ReadOnlySpan<byte> span)
    {
        ReadOnlySpan<byte> span = span;

        public readonly int Remaining => this.span.Length;

        public byte ReadUInt8()
        {
            var value = this.span[0];
            this.span = this.span[1..];
            return value;
        }

        public ushort ReadUInt16()
        {
            if (this.span.Length < 2)
                throw new ArgumentOutOfRangeException(nameof(span));

            var value = BinaryPrimitives.ReadUInt16BigEndian(this.span);
            this.span = this.span[2..];
            return value;
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            var value = this.span.Slice(0, count);
            this.span = this.span[count..];
            return value;
        }

        public void Skip(int count) => this.span = this.span[count..];
    }
}
