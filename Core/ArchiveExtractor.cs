using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// ArchiveExtractor — extracts emulator distribution archives.
//
// BizHawk ships .zip (BCL ZipFile handles it). snes9x-emunwa ships .7z, which
// ZipFile CANNOT read. Rather than bundle a 7-Zip binary or add a NuGet
// dependency (both raise the launcher's AV-heuristic surface — the owner's
// standing constraint, see the AV notes in LauncherV2.csproj), we extract .7z
// with the Windows-bundled **bsdtar** (%SystemRoot%\System32\tar.exe, backed by
// libarchive with liblzma — verified to read 7-Zip/LZMA archives). bsdtar ships
// in Windows 10 1803+ and every Windows 11, so on any supported target there is
// nothing to install. If it is somehow absent we throw an actionable error
// rather than silently failing.
// ═══════════════════════════════════════════════════════════════════════════════

public static class ArchiveExtractor
{
    /// Absolute path to the OS-bundled bsdtar (libarchive). Resolved from the
    /// real system directory so we never pick up an MSYS/Git GNU tar on PATH
    /// (GNU tar cannot read .7z).
    private static string SystemTar =>
        Path.Combine(Environment.SystemDirectory, "tar.exe");

    /// Extract <paramref name="archivePath"/> into <paramref name="destDir"/>
    /// (created if missing), choosing the method from the file extension.
    /// Throws on any failure. Existing files are overwritten.
    public static void Extract(string archivePath, string destDir)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive not found.", archivePath);
        Directory.CreateDirectory(destDir);

        string ext = Path.GetExtension(archivePath).ToLowerInvariant();
        switch (ext)
        {
            case ".zip":
                ZipFile.ExtractToDirectory(archivePath, destDir, overwriteFiles: true);
                break;
            case ".7z":
                ExtractWithBsdTar(archivePath, destDir);
                break;
            default:
                throw new NotSupportedException(
                    $"Unsupported archive type '{ext}'. " +
                    "Supported: .zip (built-in), .7z (Windows bsdtar).");
        }
    }

    /// Extract a .7z (or any libarchive-supported format) with the OS bsdtar.
    /// bsdtar auto-detects the format, so the same call also handles .tar.gz etc.
    private static void ExtractWithBsdTar(string archivePath, string destDir)
    {
        if (!File.Exists(SystemTar))
            throw new InvalidOperationException(
                "Windows' built-in archive tool (bsdtar) was not found at " +
                $"{SystemTar}. It ships with Windows 10 1803+ and Windows 11 — " +
                "update Windows, or install 7-Zip and extract the download " +
                "manually, then retry.");

        // -x extract, -f file, -C change-to-dir. bsdtar reads .7z natively via
        // libarchive+liblzma. Quote both paths; capture stderr for diagnostics.
        var psi = new ProcessStartInfo
        {
            FileName               = SystemTar,
            WorkingDirectory       = destDir,
            UseShellExecute        = false,
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-x");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(archivePath);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(destDir);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start bsdtar.");
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"Extracting '{Path.GetFileName(archivePath)}' failed " +
                $"(bsdtar exit {proc.ExitCode}). {stderr.Trim()}");
    }

    /// Many emulator archives unpack into a single top-level folder
    /// (e.g. snes9x-1.63-nwa-win32-x64\snes9x-x64.exe). If <paramref name="destDir"/>
    /// ended up with exactly one sub-directory and the <paramref name="sentinelExe"/>
    /// is NOT already at the top level, lift the inner folder's contents up one
    /// level so callers find the exe directly under destDir. No-op when the
    /// layout is already flat. Returns true if a flatten happened.
    public static bool FlattenSingleSubdir(string destDir, string sentinelExe)
    {
        if (File.Exists(Path.Combine(destDir, sentinelExe))) return false;  // already flat

        var dirs  = Directory.GetDirectories(destDir);
        var files = Directory.GetFiles(destDir);
        if (dirs.Length != 1 || files.Length != 0) return false;  // not a single-subdir layout

        string inner = dirs[0];
        foreach (string f in Directory.GetFiles(inner))
            File.Move(f, Path.Combine(destDir, Path.GetFileName(f)), overwrite: true);
        foreach (string d in Directory.GetDirectories(inner))
        {
            string dest = Path.Combine(destDir, Path.GetFileName(d));
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.Move(d, dest);
        }
        try { Directory.Delete(inner, recursive: true); } catch { /* emptied already */ }
        return true;
    }

    /// Find the first release asset whose name matches a backend's archive
    /// pattern. <paramref name="systemTag"/> is the platform token in the asset
    /// name ("win-x64" for BizHawk, "win32-x64" for snes9x-emunwa) and
    /// <paramref name="ext"/> is the extension (".zip"/".7z"). Returns the asset
    /// name or null. Kept here so the per-backend asset rule lives next to the
    /// extractor that consumes it.
    public static string? MatchAssetName(System.Collections.Generic.IEnumerable<string> assetNames,
                                         string systemTag, string ext)
        => assetNames.FirstOrDefault(n =>
               n.Contains(systemTag, StringComparison.OrdinalIgnoreCase) &&
               n.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
}
