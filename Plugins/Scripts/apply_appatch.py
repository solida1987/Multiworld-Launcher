"""apply_appatch.py - apply an Archipelago APProcedurePatch container to a ROM.

Usage:
    python apply_appatch.py <patch_file> <base_rom> <out_rom> [ap_lib_dir]

The container (.apemerald / .apfirered / ...) is a ZIP holding:
    archipelago.json     manifest: base_checksum (MD5 of the vanilla ROM) and
                         the procedure step list
    base_patch.bsdiff4   vanilla -> AP base patch        (apply_bsdiff4)
    token_data.bin       per-seed token writes           (apply_tokens)

Both step implementations mirror worlds/Files.py of Archipelago 0.6.6
(APPatchExtension.apply_bsdiff4 line 475, apply_tokens line 480, APTokenTypes
line 382 - recovered from the frozen install's Files.pyc).

bsdiff4 resolution order:
    1. a regular `import bsdiff4` (pip install)
    2. the AP install's own bundled C extension
       (<ap_lib_dir>\bsdiff4.core.cp3XX-win_amd64.pyd + library.zip package)
    3. a pure-python BSDIFF40 fallback (stdlib bz2; slower but dependency-free)

Output: a single JSON line on stdout. {"ok": true, ...} and exit 0 on
success; {"ok": false, "error": ...} and exit 1..4 on failure. The base ROM
is only ever read; the output is written via a temp file + atomic replace.
"""

import glob
import hashlib
import json
import os
import sys
import zipfile


def fail(code, message, **extra):
    print(json.dumps({"ok": False, "error": message, **extra}))
    sys.exit(code)


# ---------------------------------------------------------------------------
# bsdiff4
# ---------------------------------------------------------------------------

def _load_ap_bundled_bsdiff4(lib_dir):
    """Import the AP install's own bsdiff4: the package lives in library.zip,
    its C extension sits loose as bsdiff4.core.cp3XX-win_amd64.pyd (a .pyd
    cannot be imported from inside a zip, so pre-register it)."""
    import importlib.util
    pyds = glob.glob(os.path.join(lib_dir, "bsdiff4.core.*.pyd"))
    if not pyds:
        return None
    spec = importlib.util.spec_from_file_location("bsdiff4.core", pyds[0])
    core = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(core)          # raises on python-version mismatch
    sys.modules["bsdiff4.core"] = core
    sys.path.insert(0, os.path.join(lib_dir, "library.zip"))
    sys.path.insert(0, lib_dir)
    import bsdiff4
    return bsdiff4


def _bsdiff4_module(lib_dir):
    try:
        import bsdiff4
        return bsdiff4, "pip"
    except ImportError:
        pass
    if lib_dir and os.path.isdir(lib_dir):
        try:
            mod = _load_ap_bundled_bsdiff4(lib_dir)
            if mod is not None:
                return mod, "ap-bundled"
        except Exception:
            pass                            # version mismatch etc. -> fallback
    return None, "pure-python"


def _read_int64(buf, off):
    """bsdiff sign-magnitude little-endian int64 (NOT two's complement)."""
    magnitude = int.from_bytes(buf[off:off + 8], "little") & 0x7FFFFFFFFFFFFFFF
    return -magnitude if buf[off + 7] & 0x80 else magnitude


def _bsdiff4_patch_pure(src, patch):
    """Pure-python BSDIFF40 apply (stdlib only)."""
    import bz2
    if patch[:8] != b"BSDIFF40":
        raise ValueError("not a BSDIFF40 stream")
    len_control = _read_int64(patch, 8)
    len_diff    = _read_int64(patch, 16)
    len_dst     = _read_int64(patch, 24)
    control = bz2.decompress(patch[32:32 + len_control])
    diff    = bz2.decompress(patch[32 + len_control:32 + len_control + len_diff])
    extra   = bz2.decompress(patch[32 + len_control + len_diff:])

    dst = bytearray(len_dst)
    cp = dp = ep = pos_src = pos_dst = 0
    while pos_dst < len_dst:
        x = _read_int64(control, cp)
        y = _read_int64(control, cp + 8)
        z = _read_int64(control, cp + 16)
        cp += 24
        chunk_src = src[pos_src:pos_src + x]
        chunk_dif = diff[dp:dp + x]
        dst[pos_dst:pos_dst + x] = bytes((a + b) & 0xFF
                                         for a, b in zip(chunk_src, chunk_dif))
        pos_src += x
        pos_dst += x
        dp += x
        dst[pos_dst:pos_dst + y] = extra[ep:ep + y]
        pos_dst += y
        ep += y
        pos_src += z                        # z may be negative
    return bytes(dst)


def apply_bsdiff4(rom, patch_bytes, lib_dir):
    mod, source = _bsdiff4_module(lib_dir)
    if mod is not None:
        return mod.patch(rom, patch_bytes), source
    return _bsdiff4_patch_pure(rom, patch_bytes), source


# ---------------------------------------------------------------------------
# apply_ips — classic IPS (used by the Super Metroid APProcedurePatch, whose
# base patches are .ips). Format: 'PATCH' header, then records of
#   3-byte offset, 2-byte length, length bytes of data
# with a length-0 record meaning RLE (2-byte run length, 1-byte value), ending
# at the 'EOF' marker. An optional trailing 3-byte value truncates the file.
# stdlib only; mirrors Archipelago's worlds/Files.py:APPatchExtension.apply_ips.
# ---------------------------------------------------------------------------

def apply_ips(rom, patch_bytes):
    if patch_bytes[:5] != b"PATCH":
        raise ValueError("not an IPS stream (missing PATCH header)")
    out = bytearray(rom)
    pos = 5
    n = len(patch_bytes)
    while pos < n:
        if patch_bytes[pos:pos + 3] == b"EOF":
            pos += 3
            # optional 3-byte truncate length after EOF
            if pos + 3 <= n:
                truncate = int.from_bytes(patch_bytes[pos:pos + 3], "big")
                if 0 < truncate < len(out):
                    del out[truncate:]
            break
        offset = int.from_bytes(patch_bytes[pos:pos + 3], "big")
        length = int.from_bytes(patch_bytes[pos + 3:pos + 5], "big")
        pos += 5
        if length == 0:                                   # RLE record
            run_len = int.from_bytes(patch_bytes[pos:pos + 2], "big")
            value = patch_bytes[pos + 2]
            pos += 3
            if offset + run_len > len(out):
                out.extend(b"\x00" * (offset + run_len - len(out)))
            out[offset:offset + run_len] = bytes([value]) * run_len
        else:                                             # plain copy
            data = patch_bytes[pos:pos + length]
            pos += length
            if offset + length > len(out):
                out.extend(b"\x00" * (offset + length - len(out)))
            out[offset:offset + length] = data
    return bytes(out)


# ---------------------------------------------------------------------------
# apply_tokens — worlds/Files.py:480
# token_data.bin: u32le count, then per token:
#   u8 type, u32le offset, u32le size, u8[size] data
# types (Files.py:382): WRITE=0 COPY=1 RLE=2 AND_8=3 OR_8=4 XOR_8=5
# ---------------------------------------------------------------------------

def apply_tokens(rom, token_data):
    out = bytearray(rom)
    count = int.from_bytes(token_data[0:4], "little")
    bpr = 4
    for _ in range(count):
        token_type = token_data[bpr]
        offset = int.from_bytes(token_data[bpr + 1:bpr + 5], "little")
        size   = int.from_bytes(token_data[bpr + 5:bpr + 9], "little")
        data   = token_data[bpr + 9:bpr + 9 + size]
        bpr += 9 + size
        if token_type in (3, 4, 5):                       # AND_8 / OR_8 / XOR_8
            arg = data[0]
            if token_type == 3:
                out[offset] &= arg
            elif token_type == 4:
                out[offset] |= arg
            else:
                out[offset] ^= arg
        elif token_type in (1, 2):                        # COPY / RLE
            length = int.from_bytes(data[0:4], "little")
            value  = int.from_bytes(data[4:8], "little")
            if token_type == 1:
                out[offset:offset + length] = out[value:value + length]
            else:
                out[offset:offset + length] = bytes([value & 0xFF]) * length
        else:                                             # WRITE
            out[offset:offset + size] = data
    return bytes(out)


# ---------------------------------------------------------------------------

def main():
    if len(sys.argv) < 4:
        fail(2, "usage: apply_appatch.py <patch_file> <base_rom> <out_rom> [ap_lib_dir]")
    patch_path, base_path, out_path = sys.argv[1:4]
    lib_dir = sys.argv[4] if len(sys.argv) > 4 else r"C:\ProgramData\Archipelago\lib"

    if not os.path.isfile(patch_path):
        fail(2, f"patch file not found: {patch_path}")
    if not os.path.isfile(base_path):
        fail(2, f"base ROM not found: {base_path}")

    try:
        with zipfile.ZipFile(patch_path) as zf:
            manifest = json.loads(zf.read("archipelago.json"))
            files = {n: zf.read(n) for n in zf.namelist() if n != "archipelago.json"}
    except Exception as exc:
        fail(2, f"cannot read patch container: {exc}")

    rom = open(base_path, "rb").read()

    expected = (manifest.get("base_checksum") or "").lower()
    actual = hashlib.md5(rom).hexdigest()
    if expected and actual != expected:
        fail(3, "base ROM checksum mismatch - this is not the vanilla ROM the "
                "patch was made for", expected_md5=expected, actual_md5=actual)

    bsdiff_source = None
    try:
        for step, args in manifest.get("procedure", []):
            if step == "apply_bsdiff4":
                rom, bsdiff_source = apply_bsdiff4(rom, files[args[0]], lib_dir)
            elif step == "apply_ips":
                rom = apply_ips(rom, files[args[0]])
            elif step == "apply_tokens":
                rom = apply_tokens(rom, files[args[0]])
            else:
                fail(4, f"unsupported procedure step: {step}")
    except SystemExit:
        raise
    except Exception as exc:
        fail(4, f"patch step failed: {exc}")

    tmp_path = out_path + ".tmp"
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    with open(tmp_path, "wb") as f:
        f.write(rom)
    os.replace(tmp_path, out_path)

    print(json.dumps({
        "ok": True,
        "out": out_path,
        "size": len(rom),
        "md5": hashlib.md5(rom).hexdigest(),
        "bsdiff4": bsdiff_source,
        "game": manifest.get("game"),
        "player_name": manifest.get("player_name"),
    }))


if __name__ == "__main__":
    main()
