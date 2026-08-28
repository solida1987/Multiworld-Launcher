using System;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Nwa;

// One SNES memory, two transports.
//
// Snes9xLuaBridge runs the per-game AP Lua modules launcher-side and only ever
// needs "read/write this region at this offset". How those bytes move — NWA's
// TCP protocol into snes9x-emunwa, or SNI's usb2snes WebSocket into whatever
// SNI has attached — is the transport's business, so the bridge takes this
// interface and the two adapters below carry the difference.
//
// Regions are the BizHawk domain names the modules speak: "WRAM", "SRAM"
// (battery RAM; the modules also say "CARTRAM") and "CARTROM" (the cartridge
// ROM; the modules also say "ROM"). The bridge normalises the aliases before
// calling here, so an adapter only sees these three.
public interface ISnesMemory
{
    Task<byte[]> ReadAsync(string region, int address, int length,
                           CancellationToken ct = default);
    Task WriteAsync(string region, int address, byte[] data,
                    CancellationToken ct = default);
}

/// NWA: the regions ARE the protocol's core memory names, pass them through.
public sealed class NwaMemory : ISnesMemory
{
    private readonly NwaClient _nwa;
    public NwaMemory(NwaClient nwa) => _nwa = nwa;

    public Task<byte[]> ReadAsync(string region, int address, int length,
                                  CancellationToken ct = default)
        => _nwa.ReadMemoryAsync(region, address, length, ct);

    public Task WriteAsync(string region, int address, byte[] data,
                           CancellationToken ct = default)
        => _nwa.WriteMemoryAsync(region, address, data, ct);
}

/// SNI: one flat usb2snes address space, so a region is a base offset.
///
/// The bases are usb2snes convention, the same mapping alttp.lua documents in
/// its header: ROM from 0x000000, battery SRAM from 0xE00000, WRAM from
/// 0xF50000. The transport underneath is whatever IEmulatorBridge the sni
/// extension provides — this class never opens a socket of its own.
public sealed class SniMemory : ISnesMemory
{
    private const long RomBase  = 0x000000;
    private const long SramBase = 0xE00000;
    private const long WramBase = 0xF50000;

    private readonly Extensions.IEmulatorBridge _bridge;
    public SniMemory(Extensions.IEmulatorBridge bridge) => _bridge = bridge;

    private static long Base(string region) => region switch
    {
        "WRAM"    => WramBase,
        "SRAM"    => SramBase,
        "CARTROM" => RomBase,
        _ => throw new ArgumentException($"Unknown SNES region '{region}'."),
    };

    public Task<byte[]> ReadAsync(string region, int address, int length,
                                  CancellationToken ct = default)
        => _bridge.ReadAsync(Base(region) + address, length, ct);

    public Task WriteAsync(string region, int address, byte[] data,
                           CancellationToken ct = default)
        => _bridge.WriteAsync(Base(region) + address, data, ct);
}
