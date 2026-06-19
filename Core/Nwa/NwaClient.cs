using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Nwa;

// ═══════════════════════════════════════════════════════════════════════════════
// NwaClient — a TCP client for the Emulator Network Access (NWA) protocol.
//
// snes9x-emunwa (the §14 SNES backend) exposes its emulated memory over NWA —
// there is NO in-emulator script, so the launcher is the CLIENT and runs the
// SNES game logic itself (poll WRAM for checks, write WRAM to deliver items),
// reusing the addresses our alttp.lua / super_metroid.lua modules document.
//
// Wire protocol (usb2snes/emulator-networkaccess, Protocol-1.0 Draft-v4):
//   • Server listens on TCP 48879 (0xBEEF), +1 per already-bound port.
//   • Command line: "KEYWORD arg1;arg2;...\n" — KEYWORD and the first arg are
//     separated by a single SPACE, remaining args by ';'. Binary commands are
//     prefixed with a lowercase 'b' (e.g. bCORE_WRITE).
//   • ASCII reply: leading byte '\n', then "key:value\n" lines, terminated by an
//     empty line (a bare '\n'). Errors are ASCII with error:/reason: keys.
//   • Binary reply: leading byte '\0', then a 4-byte BIG-ENDIAN length, then
//     exactly that many data bytes.
//   • CORE_READ <mem>;<off>;<size>[;<off2>;<size2>...] → binary reply.
//   • bCORE_WRITE <mem>;<off>;<size> followed by a binary message
//     "\0<4-byte BE size><data>" → ASCII reply.
//   • SNES memory names: WRAM (0-based, offset 0xF000 == bus $7EF000), SRAM,
//     CARTROM, VRAM, OAM, CGRAM, APURAM, CPUBUS, APUBUS.
//
// Verified against a mock NWA server (Tools/NwaSelfTest). The LIVE confirmation
// (a real snes9x-emunwa attaching and reporting a real check) is the same
// in-emulator gate BizHawk+Emerald cleared — that one is the owner's to run.
// ═══════════════════════════════════════════════════════════════════════════════

/// One NWA reply: an ASCII key/value map OR a binary blob (never both).
public sealed class NwaReply
{
    public bool IsBinary { get; }
    public IReadOnlyDictionary<string, string> Map { get; }
    public byte[] Data { get; }

    private NwaReply(bool binary, IReadOnlyDictionary<string, string> map, byte[] data)
    { IsBinary = binary; Map = map; Data = data; }

    public static NwaReply Ascii(IReadOnlyDictionary<string, string> map)
        => new(false, map, Array.Empty<byte>());
    public static NwaReply Binary(byte[] data)
        => new(true, EmptyMap, data);

    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>();

    /// True when the emulator answered with an error hashmap.
    public bool IsError => !IsBinary && Map.ContainsKey("error");
    public string? Error  => Map.TryGetValue("error",  out var e) ? e : null;
    public string? Reason => Map.TryGetValue("reason", out var r) ? r : null;
}

public sealed class NwaClient : IDisposable
{
    public const int DefaultPort = 48879;   // 0xBEEF

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly byte[] _one = new byte[1];

    public int Port { get; }

    private NwaClient(TcpClient tcp, int port)
    {
        _tcp    = tcp;
        _stream = tcp.GetStream();
        Port    = port;
    }

    /// Connect to the first NWA server in the port range [basePort, basePort+span).
    /// snes9x-emunwa increments the port when 0xBEEF is busy, so a small scan
    /// finds it. Returns null when nothing answers in range.
    public static async Task<NwaClient?> ConnectAsync(
        string host = "127.0.0.1", int basePort = DefaultPort, int span = 8,
        CancellationToken ct = default)
    {
        for (int port = basePort; port < basePort + span; port++)
        {
            var tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync(host, port, ct);
                tcp.NoDelay = true;   // tiny latency-sensitive command/reply frames
                return new NwaClient(tcp, port);
            }
            catch (OperationCanceledException) { tcp.Dispose(); throw; }
            catch { tcp.Dispose(); /* try next port */ }
        }
        return null;
    }

    /// Send "EMULATOR_INFO" and return its hashmap (name/version/nwa_version/...).
    public Task<NwaReply> EmulatorInfoAsync(CancellationToken ct = default)
        => ExchangeAsync("EMULATOR_INFO", null, ct);

    /// CORE_READ a single contiguous range. Returns exactly <paramref name="size"/>
    /// bytes, or throws on an NWA error / short read.
    public async Task<byte[]> ReadMemoryAsync(
        string memory, long offset, int size, CancellationToken ct = default)
    {
        var reply = await ExchangeAsync($"CORE_READ {memory};{offset};{size}", null, ct);
        if (reply.IsError)
            throw new NwaException($"CORE_READ {memory};{offset};{size}", reply);
        if (!reply.IsBinary)
            throw new NwaException("CORE_READ returned a non-binary reply", reply);
        if (reply.Data.Length != size)
            throw new NwaException(
                $"CORE_READ returned {reply.Data.Length} bytes, expected {size}", reply);
        return reply.Data;
    }

    /// bCORE_WRITE a single contiguous range. Throws on an NWA error.
    public async Task WriteMemoryAsync(
        string memory, long offset, byte[] data, CancellationToken ct = default)
    {
        var reply = await ExchangeAsync(
            $"bCORE_WRITE {memory};{offset};{data.Length}", data, ct);
        if (reply.IsError)
            throw new NwaException($"bCORE_WRITE {memory};{offset};{data.Length}", reply);
    }

    // ── Core exchange ─────────────────────────────────────────────────────────

    /// Write a command line (+ optional binary payload for b-prefixed commands)
    /// and read exactly one reply.
    public async Task<NwaReply> ExchangeAsync(
        string commandLine, byte[]? binaryPayload, CancellationToken ct)
    {
        byte[] line = Encoding.ASCII.GetBytes(commandLine + "\n");
        await _stream.WriteAsync(line, ct);

        if (binaryPayload != null)
        {
            // Binary message frame: '\0' + 4-byte big-endian size + data.
            int len = binaryPayload.Length;
            byte[] hdr = { 0,
                           (byte)(len >> 24), (byte)(len >> 16),
                           (byte)(len >> 8),  (byte)len };
            await _stream.WriteAsync(hdr, ct);
            await _stream.WriteAsync(binaryPayload, ct);
        }
        await _stream.FlushAsync(ct);
        return await ReadReplyAsync(ct);
    }

    private async Task<NwaReply> ReadReplyAsync(CancellationToken ct)
    {
        int first = await ReadByteAsync(ct);
        if (first == '\n')   // 0x0A — ASCII hashmap reply
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            while (true)
            {
                string lineText = await ReadAsciiLineAsync(ct);
                if (lineText.Length == 0) break;          // blank line ends the reply
                int colon = lineText.IndexOf(':');
                if (colon >= 0) map[lineText[..colon]] = lineText[(colon + 1)..];
                else            map[lineText] = "";
            }
            return NwaReply.Ascii(map);
        }
        if (first == 0)      // 0x00 — binary reply
        {
            byte[] lenBuf = new byte[4];
            await ReadExactAsync(lenBuf, 4, ct);
            int size = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
            if (size < 0 || size > 64 * 1024 * 1024)
                throw new NwaException($"binary reply announced implausible size {size}",
                                       NwaReply.Ascii(new Dictionary<string, string>()));
            byte[] data = new byte[size];
            await ReadExactAsync(data, size, ct);
            return NwaReply.Binary(data);
        }
        if (first < 0)
            throw new NwaException("connection closed before a reply arrived",
                                   NwaReply.Ascii(new Dictionary<string, string>()));
        throw new NwaException($"unexpected reply marker byte 0x{first:X2}",
                               NwaReply.Ascii(new Dictionary<string, string>()));
    }

    // ── Byte-level helpers ────────────────────────────────────────────────────

    /// Read one byte, or -1 on clean EOF.
    private async Task<int> ReadByteAsync(CancellationToken ct)
    {
        int n = await _stream.ReadAsync(_one.AsMemory(0, 1), ct);
        return n == 0 ? -1 : _one[0];
    }

    /// Read an ASCII line (up to, not including, the next '\n').
    private async Task<string> ReadAsciiLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            int b = await ReadByteAsync(ct);
            if (b < 0 || b == '\n') break;
            if (b != '\r') sb.Append((char)b);
        }
        return sb.ToString();
    }

    /// Fill <paramref name="count"/> bytes of <paramref name="buf"/> or throw on EOF.
    private async Task ReadExactAsync(byte[] buf, int count, CancellationToken ct)
    {
        int got = 0;
        while (got < count)
        {
            int n = await _stream.ReadAsync(buf.AsMemory(got, count - got), ct);
            if (n == 0)
                throw new NwaException(
                    $"connection closed mid-payload ({got}/{count} bytes)",
                    NwaReply.Ascii(new Dictionary<string, string>()));
            got += n;
        }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { }
        try { _tcp.Dispose(); }    catch { }
    }
}

/// Raised when NWA answers with an error hashmap or breaks framing.
public sealed class NwaException : Exception
{
    public NwaReply Reply { get; }
    public NwaException(string context, NwaReply reply)
        : base(reply.IsError
            ? $"{context}: NWA error '{reply.Error}' — {reply.Reason}"
            : context)
        => Reply = reply;
}
