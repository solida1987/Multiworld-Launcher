using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LauncherV2.Core.Plugins;

// Approval is bound to the SHA-256 of the installed folder — a changed
// plugin is an unapproved plugin again.

public sealed class PluginTrustRecord
{
    [JsonPropertyName("sha256")]   public string Sha256   { get; set; } = "";
    [JsonPropertyName("approved")] public string Approved { get; set; } = "";
    [JsonPropertyName("version")]  public string Version  { get; set; } = "";
    [JsonPropertyName("author")]   public string Author   { get; set; } = "";
}

public static class PluginTrustStore
{
    private static readonly object Gate = new();

    public static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "plugins.json");

    private static Dictionary<string, PluginTrustRecord> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new(StringComparer.OrdinalIgnoreCase);
            var d = JsonSerializer.Deserialize<Dictionary<string, PluginTrustRecord>>(
                        File.ReadAllText(FilePath));
            return d == null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(d, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // A corrupt trust file must not brick the launcher. Losing it means
            // the player is asked again — annoying, never dangerous.
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save(Dictionary<string, PluginTrustRecord> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath,
            JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Record that the player accepted exactly these bytes.</summary>
    public static void Approve(string gameId, string sha256, string version, string author)
    {
        lock (Gate)
        {
            var map = Load();
            map[gameId] = new PluginTrustRecord
            {
                Sha256   = sha256,
                Approved = DateTime.UtcNow.ToString("O"),
                Version  = version,
                Author   = author,
            };
            Save(map);
        }
    }

    /// <summary>Forget a plugin — on removal, or when the player revokes it.</summary>
    public static void Revoke(string gameId)
    {
        lock (Gate)
        {
            var map = Load();
            if (map.Remove(gameId)) Save(map);
        }
    }

    public static PluginTrustRecord? Get(string gameId)
    {
        lock (Gate) { return Load().TryGetValue(gameId, out var r) ? r : null; }
    }

    /// <summary>Why a plugin may not load, or null when it may.</summary>
    public enum Verdict
    {
        /// <summary>Approved, and the bytes still match.</summary>
        Trusted,
        /// <summary>Never approved on this machine.</summary>
        Unknown,
        /// <summary>Approved once, but the files have changed since.</summary>
        Changed,
    }

    /// <summary>
    /// Check an installed plugin folder against what was approved.
    /// Hashes the folder, so an edit anywhere inside it counts.
    /// </summary>
    public static Verdict Check(string gameId, string directory)
    {
        var rec = Get(gameId);
        if (rec == null || string.IsNullOrEmpty(rec.Sha256)) return Verdict.Unknown;

        string now;
        try { now = PluginPackage.HashDirectory(directory); }
        catch { return Verdict.Unknown; }

        return string.Equals(now, rec.Sha256, StringComparison.OrdinalIgnoreCase)
            ? Verdict.Trusted
            : Verdict.Changed;
    }

    /// <summary>Wording for the player when a check comes back not-Trusted.</summary>
    public static string Explain(Verdict v, string displayName) => v switch
    {
        Verdict.Changed =>
            $"{displayName} has changed since you approved it. That is normal after an "
          + "update, but it is new code either way — approve it again to use it.",
        Verdict.Unknown =>
            $"{displayName} has not been approved on this computer yet.",
        _ => "",
    };
}
