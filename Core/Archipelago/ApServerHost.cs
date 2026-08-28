using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core.Archipelago;

// ApServerHost — an Archipelago server as a thing London starts, watches and
// stops, instead of a console window the player has to babysit.
//
// Every rule in here was measured, and each one guards a real failure:
//
//   * "Hosting game at ..." is printed BEFORE the port is bound. The only
//     proof of a live server is the "server listening" line -- a port clash
//     surfaces a full six seconds into startup, after that hopeful message.
//   * The exit code of a failed start is -1 whether the port was taken or the
//     file was missing. The code says nothing; stderr's winerror 10048 does.
//   * The .apsave is written on graceful exit or dirty autosave -- a hard kill
//     of an idle room leaves nothing. So the ONLY correct stop is "/exit" on
//     stdin, and kill is the fallback for a server that stopped answering.
//   * A CRASHED startup also writes an .apsave on its way down. The file's
//     existence does not mean the last run was healthy, so London keeps its
//     own marker and only offers "Resume" when both agree.
//   * The default port (38281) is a default: on this machine it has already
//     been found occupied by strays. Every start probes for a free port.
public sealed class ApServerHost
{
    private const string MarkerName = "london_host.json";

    private static readonly List<ApServerHost> _running = new();
    public static IReadOnlyList<ApServerHost> Running { get { lock (_running) return _running.ToList(); } }

    public static ApServerHost? For(SeedInfo seed)
    {
        lock (_running)
            return _running.FirstOrDefault(h =>
                string.Equals(h.SeedFolder, seed.Folder, StringComparison.OrdinalIgnoreCase));
    }

    public string SeedFolder { get; }
    public int Port { get; }
    public int Pid => _proc.Id;

    private readonly Process _proc;
    private readonly StringBuilder _log = new();
    public event Action<string>? Output;

    private ApServerHost(Process proc, string seedFolder, int port)
    {
        _proc = proc;
        SeedFolder = seedFolder;
        Port = port;
    }

    public bool IsRunning
    {
        get { try { return !_proc.HasExited; } catch { return false; } }
    }

    public string Log { get { lock (_log) return _log.ToString(); } }

    // ------------------------------------------------------------------ start

    public sealed record StartResult(ApServerHost? Host, string Message);

    /// Starts the server for a seed on the first free port at or above 38281.
    /// Waits for proof of life, not for hope.
    /// One start at a time, globally. Two concurrent joins of the same seed
    /// both looked, both saw nothing running, and both started a server --
    /// the caught-in-proof version of the double-click problem. Server starts
    /// are rare and take ~6 s; serialising them costs nothing.
    private static readonly System.Threading.SemaphoreSlim _startGate = new(1, 1);

    public static async Task<StartResult> StartAsync(
        ApEngine.Report engine, SeedInfo seed, CancellationToken ct = default)
    {
        if (!engine.Usable)
            return new StartResult(null, engine.Summary());
        if (!File.Exists(seed.MultidataPath))
            return new StartResult(null, "The seed's server file is missing from " + seed.Folder);

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await StartLockedAsync(engine, seed, ct).ConfigureAwait(false);
        }
        finally { _startGate.Release(); }
    }

    private static async Task<StartResult> StartLockedAsync(
        ApEngine.Report engine, SeedInfo seed, CancellationToken ct)
    {
        // Re-checked INSIDE the gate: the whole point is that the second
        // caller waits here until the first caller's server is visible.
        if (For(seed) is { IsRunning: true } already)
            return new StartResult(already, $"Already hosting on port {already.Port}.");

        var tried = new HashSet<int>();
        string lastError = "";

        for (int attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            int port = FindFreePort(tried);
            tried.Add(port);

            var psi = new ProcessStartInfo
            {
                FileName               = Path.Combine(engine.Root, ApEngine.ServerExeName),
                WorkingDirectory       = engine.Root,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add(seed.MultidataPath);
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(port.ToString());

            Process proc;
            try { proc = Process.Start(psi)!; }
            catch (Exception e)
            {
                return new StartResult(null, "The server could not be started: " + e.Message);
            }

            var host = new ApServerHost(proc, seed.Folder, port);
            bool listening = false, portTaken = false;

            void OnLine(string? line)
            {
                if (line == null) return;
                lock (host._log) host._log.AppendLine(line);
                host.Output?.Invoke(line);
                if (line.Contains("server listening", StringComparison.OrdinalIgnoreCase))
                    listening = true;
                // Localised Windows text -- match the code, never the words.
                if (line.Contains("10048", StringComparison.Ordinal))
                    portTaken = true;
            }
            proc.OutputDataReceived += (_, e) => OnLine(e.Data);
            proc.ErrorDataReceived  += (_, e) => OnLine(e.Data);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Boot is ~6 s; the margin covers a slow disk, not a hung server.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (!listening && !portTaken && !proc.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(200, ct).ConfigureAwait(false);

            if (listening)
            {
                WriteMarker(seed.Folder, port, healthy: false);
                lock (_running) _running.Add(host);
                return new StartResult(host, $"Hosting on port {port}.");
            }

            // Not alive. Whatever it was doing, it must not keep doing it.
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }

            if (portTaken) { lastError = $"Port {port} was taken after all."; continue; }

            lastError = LastRealLine(host.Log);
            break;
        }

        return new StartResult(null,
            lastError.Length > 0 ? lastError : "The server did not come up.");
    }

    // ------------------------------------------------------------------- stop

    /// Asks nicely first: "/exit" is the ONLY stop that reliably writes the
    /// save. Kill is what happens to a server that no longer answers.
    public async Task<bool> StopAsync()
    {
        bool graceful = false;
        try
        {
            if (!_proc.HasExited)
            {
                try
                {
                    await _proc.StandardInput.WriteLineAsync("/exit").ConfigureAwait(false);
                    await _proc.StandardInput.FlushAsync().ConfigureAwait(false);
                }
                catch { /* stdin gone means it is already dying */ }

                graceful = await Task.Run(() => _proc.WaitForExit(10_000)).ConfigureAwait(false);
                if (!graceful)
                    try { _proc.Kill(entireProcessTree: true); } catch { }
            }
            else graceful = true;
        }
        finally
        {
            WriteMarker(SeedFolder, Port, healthy: graceful);
            lock (_running) _running.Remove(this);
        }
        return graceful;
    }

    /// Every server London started, stopped the polite way. Called when the
    /// launcher closes, so no orphan keeps a port warm for nobody -- the two
    /// strays found on this machine were exactly that.
    public static async Task StopAllAsync()
    {
        foreach (var host in Running)
            try { await host.StopAsync().ConfigureAwait(false); } catch { }
    }

    // ----------------------------------------------------------------- resume

    /// True only when there is a save AND London's own marker says the last
    /// shutdown was healthy. An .apsave alone can be the debris of a crash.
    public static bool CanResume(SeedInfo seed)
        => seed.HasSave && ReadMarker(seed.Folder) is { Healthy: true };

    // ---------------------------------------------------------------- innards

    private sealed record Marker(
        [property: System.Text.Json.Serialization.JsonPropertyName("port")]    int Port,
        [property: System.Text.Json.Serialization.JsonPropertyName("healthy")] bool Healthy);

    private static void WriteMarker(string folder, int port, bool healthy)
    {
        try
        {
            File.WriteAllText(Path.Combine(folder, MarkerName),
                JsonSerializer.Serialize(new Marker(port, healthy)));
        }
        catch { /* a missing marker only costs the Resume label */ }
    }

    private static Marker? ReadMarker(string folder)
    {
        try
        {
            string path = Path.Combine(folder, MarkerName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Marker>(File.ReadAllText(path))
                : null;
        }
        catch { return null; }
    }

    /// First bindable port from 38281 up. The probe binds and releases, which
    /// cannot promise the port stays free -- that is what the 10048 retry in
    /// StartAsync is for. This just avoids the ports that are obviously gone.
    private static int FindFreePort(ISet<int> skip)
    {
        for (int port = 38281; port < 38400; port++)
        {
            if (skip.Contains(port)) continue;
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException) { /* taken, try the next */ }
        }
        // Nothing free in the whole range: let the OS-assigned failure happen
        // loudly rather than inventing a port outside the family's range.
        return 38281;
    }

    /// The last line that says something. Their stderr ends every failure with
    /// the same unrelated EOFError, and warnings about other people's worlds
    /// are noise even on a healthy run.
    private static string LastRealLine(string log)
        => log.Split('\n')
              .Select(l => l.Trim())
              .Where(l => l.Length > 0
                          && !l.Contains("EOFError", StringComparison.Ordinal)
                          && !l.Contains("Did not load", StringComparison.Ordinal)
                          && !l.Contains("manifest file", StringComparison.Ordinal)
                          && !l.Contains("pkg_resources", StringComparison.Ordinal))
              .LastOrDefault() ?? "";
}
