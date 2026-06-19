using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// SteamLocator — shared helper that locates a Steam game's install directory
// from the Windows registry + libraryfolders.vdf, given a Steam AppID.
//
// Used by multiple plugins so the VDF-parsing logic lives in one place.
// All methods are safe to call from any thread and never throw.
// ═══════════════════════════════════════════════════════════════════════════════

public static class SteamLocator
{
    /// <summary>
    /// Return the install directory for the Steam game with <paramref name="appId"/>
    /// (e.g. 405640 for Pony Island), or <c>null</c> when the game is not found.
    /// Searches every Steam library folder on this PC.
    /// </summary>
    public static string? FindGameDir(int appId)
    {
        try
        {
            foreach (string steamRoot in SteamRoots())
            {
                if (string.IsNullOrWhiteSpace(steamRoot)) continue;
                foreach (string lib in LibraryRoots(steamRoot))
                {
                    try
                    {
                        string steamapps = Path.Combine(lib, "steamapps");
                        string manifest  = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
                        if (!File.Exists(manifest)) continue;

                        string common     = Path.Combine(steamapps, "common");
                        string? installDir = ReadAcfInstallDir(manifest);
                        if (installDir != null)
                        {
                            string candidate = Path.Combine(common, installDir);
                            if (Directory.Exists(candidate)) return candidate;
                        }
                    }
                    catch { /* try next library */ }
                }
            }
        }
        catch { /* never throw */ }
        return null;
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadReg(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu))
            yield return NormPath(hkcu);

        string? hklm = ReadReg(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm))
            yield return NormPath(hklm);

        string? hklm64 = ReadReg(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64))
            yield return NormPath(hklm64);

        string? pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf86))
            yield return Path.Combine(pf86, "Steam");
    }

    private static IEnumerable<string> LibraryRoots(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        const string key = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) break;
            string raw  = text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
            string norm = NormPath(raw);
            if (norm.Length > 0 && seen.Add(norm))
                yield return norm;
            i = close + 1;
        }
    }

    private static string? ReadAcfInstallDir(string acfPath)
    {
        try
        {
            string text = File.ReadAllText(acfPath);
            const string key = "\"installdir\"";
            int i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            int open  = text.IndexOf('"', i);
            if (open < 0) return null;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) return null;
            return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
        }
        catch { return null; }
    }

    private static string? ReadReg(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    private static string NormPath(string p) => p.Replace('/', '\\').TrimEnd('\\');
}
