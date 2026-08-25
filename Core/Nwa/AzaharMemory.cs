using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Nwa;

// The 3DS, spoken natively.
//
// Azahar (the living Citra fork) serves a UDP request/response protocol on
// 127.0.0.1:45987 when [Debugging] enable_rpc_server is on — the azahar
// bridge extension switches that on before every launch. This class is the
// launcher-side client of that protocol, shaped as ISnesMemory so the very
// same Snes9xLuaBridge that drives snes9x over NWA and SNI over usb2snes can
// drive a 3DS game too. One logic engine, three wires.
//
// The protocol, measured from Citra's own rpc/udp_server.cpp and the ALBW
// world's Citra.py (GPL-2.0) on 25 Aug 2026, all fields little-endian u32:
//
//   request  = version(1) | id | type | payload_size | [payload]
//   reply    = the same 16-byte header | [data]
//   TYPE_NONE  (0): handshake — empty payload, empty reply.
//   TYPE_READ  (1): payload = address, size.  Reply data = the bytes.
//                   At most 32 bytes per request; larger reads are chunked.
//   TYPE_WRITE (2): payload = address, size, data. At most 24 data bytes.
//
// Addresses are the 3DS process's own virtual addresses — absolute, no
// regions. The `region` argument ISnesMemory carries is therefore ignored
// here: a 3DS Lua module passes plain addresses and whatever domain string
// it likes.
//
// ⚠ UDP DELIVERS WHEN IT FEELS LIKE IT. A dropped packet must not kill a
// session, so each request retries a few times on timeout. And a LATE reply
// to a request we gave up on would otherwise be read as the answer to the
// NEXT request — same header, and with equal sizes even the length check
// passes, handing the caller stale bytes as fresh. So the socket is drained
// of leftovers before every send.
public sealed class AzaharMemory : ISnesMemory, IDisposable
{
    public const int DefaultPort = 45987;

    private const uint PacketVersion = 1;
    private const uint TypeNone      = 0;
    private const uint TypeRead      = 1;
    private const uint TypeWrite     = 2;
    private const int  HeaderSize    = 16;
    private const int  MaxReadSize   = 32;
    private const int  MaxWriteSize  = 24;
    private const int  TimeoutMs     = 1000;
    private const int  Attempts      = 4;

    private readonly Socket _socket;
    private readonly object _wire = new();   // one request/response in flight
    private bool _disposed;

    public AzaharMemory(int port = DefaultPort)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram,
                             ProtocolType.Udp);
        _socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
        _socket.ReceiveTimeout = TimeoutMs;
    }

    /// One TYPE_NONE round-trip. True when the emulator's scripting server
    /// answered — the caller retries this until the emulator has booted.
    public Task<bool> HandshakeAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                lock (_wire)
                {
                    Drain();
                    _socket.Send(Header(TypeNone, 0));
                    var buf = new byte[HeaderSize + MaxReadSize];
                    return _socket.Receive(buf) >= HeaderSize;
                }
            }
            catch { return false; }
        }, ct);

    public Task<byte[]> ReadAsync(string region, int address, int length,
                                  CancellationToken ct = default)
        => Task.Run(() =>
        {
            var result = new byte[length];
            int done = 0;
            while (done < length)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(length - done, MaxReadSize);
                byte[] part = ReadSingle((uint)(address + done), n);
                Buffer.BlockCopy(part, 0, result, done, n);
                done += n;
            }
            return result;
        }, ct);

    public Task WriteAsync(string region, int address, byte[] data,
                           CancellationToken ct = default)
        => Task.Run(() =>
        {
            int done = 0;
            while (done < data.Length)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(data.Length - done, MaxWriteSize);
                WriteSingle((uint)(address + done), data, done, n);
                done += n;
            }
        }, ct);

    // --- one request, with the drain + retry story ---

    private byte[] ReadSingle(uint address, int size)
    {
        lock (_wire)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Drain();
                    byte[] packet = Header(TypeRead, 8);
                    PutU32(packet, HeaderSize + 0, address);
                    PutU32(packet, HeaderSize + 4, (uint)size);
                    _socket.Send(packet);

                    var buf = new byte[HeaderSize + MaxReadSize];
                    int got = _socket.Receive(buf);
                    if (got != HeaderSize + size)
                        throw new SocketException((int)SocketError.MessageSize);

                    var data = new byte[size];
                    Buffer.BlockCopy(buf, HeaderSize, data, 0, size);
                    return data;
                }
                catch (SocketException) when (attempt < Attempts)
                {
                    // Timeout or a mangled datagram — ask again. Reads are
                    // idempotent, so a retry can only cost time.
                }
            }
        }
    }

    private void WriteSingle(uint address, byte[] data, int offset, int count)
    {
        lock (_wire)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Drain();
                    byte[] packet = Header(TypeWrite, (uint)(8 + count),
                                           extra: count);
                    PutU32(packet, HeaderSize + 0, address);
                    PutU32(packet, HeaderSize + 4, (uint)count);
                    Buffer.BlockCopy(data, offset, packet, HeaderSize + 8, count);
                    _socket.Send(packet);

                    var buf = new byte[HeaderSize + MaxReadSize];
                    _socket.Receive(buf);   // ack header
                    return;
                }
                catch (SocketException) when (attempt < Attempts)
                {
                    // ⚠ A write retry is NOT idempotent in general — but every
                    // write this bridge makes is "set this word to this value",
                    // which is. If a non-idempotent write ever appears here,
                    // this retry has to learn sequence numbers first.
                }
            }
        }
    }

    /// Header + payload buffer. `extra` = payload bytes beyond the two u32s.
    private static byte[] Header(uint type, uint payloadSize, int extra = 0)
    {
        var p = new byte[HeaderSize + (payloadSize > 0 ? 8 + extra : 0)];
        PutU32(p, 0,  PacketVersion);
        PutU32(p, 4,  0);            // request id — the protocol ignores it
        PutU32(p, 8,  type);
        PutU32(p, 12, payloadSize);
        return p;
    }

    private static void PutU32(byte[] b, int at, uint v)
    {
        b[at + 0] = (byte)v;
        b[at + 1] = (byte)(v >> 8);
        b[at + 2] = (byte)(v >> 16);
        b[at + 3] = (byte)(v >> 24);
    }

    /// Throw away any datagram already queued — a late reply to an abandoned
    /// request, which the next Receive would mistake for its own answer.
    private void Drain()
    {
        var bin = new byte[HeaderSize + MaxReadSize];
        while (_socket.Available > 0)
        {
            try { _socket.Receive(bin); }
            catch { break; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _socket.Dispose(); } catch { }
    }
}
