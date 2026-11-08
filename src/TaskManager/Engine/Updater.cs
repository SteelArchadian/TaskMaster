using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace TaskManager.Engine;

/// <summary>
/// Updates from GitHub Releases. Each release carries TaskMaster-&lt;v&gt;.zip and latest.json
/// {version, notes, url, sig}, where sig is an ECDSA P-256 signature of the zip made with the release key
/// (tools\release.py). Nothing installs unless the signature checks out against the key built in here.
/// Checks on launch and every 30 minutes; installing waits for the user's press of Update, so it never lands mid-work.
/// Install: the new copy, unpacked to the data folder, waits for this one to exit, keeps the current files as
/// "previous", copies itself over the app folder, starts it on the same page and rolls back if it doesn't come up.
/// </summary>
public static class Updater
{
    // TASKMASTER_UPDATE_FEED points tests at a local server; TASKMASTER_UPDATE_AUTO=1 installs without asking (tests only).
    private static readonly string LatestUrl = Environment.GetEnvironmentVariable("TASKMASTER_UPDATE_FEED")
        ?? "https://github.com/SteelArchadian/TaskMaster/releases/latest/download/latest.json";
    public static bool TestAutoInstall { get; } = Environment.GetEnvironmentVariable("TASKMASTER_UPDATE_AUTO") == "1";
    private const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE+nDgylEGNVArlrV/RCq+B//niqv+KK9fuDamTGmWoqyC93hnG/TJUzLvWGipRV+qAsq5rHzebX7rhQow6itw0g==";
    private static readonly TimeSpan s_every = TimeSpan.FromMinutes(30);

    public sealed record Release(Version Version, string Notes, string Url, string Sig);

    public static Version Current { get; } = typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);

    /// <summary>A newer signed release, once a check found one.</summary>
    public static Release? Available { get; private set; }
    public static DateTime? LastChecked { get; private set; }
    public static string? LastError { get; private set; }
    public static bool Busy { get; private set; }

    /// <summary>Raised on the thread that finished a check or install step; listeners marshal to the UI themselves.</summary>
    public static event Action? Changed;

    private static string UpdatesDir => Path.Combine(Settings.Folder, "updates");
    private static readonly HttpClient s_http = MakeClient();

    private static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"TaskMaster/{Current}");
        return c;
    }

    private static Timer? s_timer;

    /// <summary>First check shortly after launch (so start-up stays quick), then every 30 minutes.</summary>
    public static void Start() => s_timer ??= new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(10), s_every);

    public static async Task CheckAsync()
    {
        if (Busy) return;
        Busy = true;
        Changed?.Invoke();
        try
        {
            using var doc = JsonDocument.Parse(await FetchAsync(LatestUrl));
            var r = doc.RootElement;
            var v = Version.Parse(r.GetProperty("version").GetString()!.TrimStart('v', 'V'));
            Available = v > Current && v.ToString() != App.Settings.FailedUpdateVersion
                ? new Release(v, r.GetProperty("notes").GetString() ?? "", r.GetProperty("url").GetString()!, r.GetProperty("sig").GetString()!)
                : null;
            LastError = null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            LastError = null; // no release published yet
        }
        catch (Exception ex)
        {
            LastError = "Couldn't check for updates (offline?)";
            Log.Info("update check: " + ex.Message);
        }
        finally
        {
            LastChecked = DateTime.Now;
            Busy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Downloads and verifies the release, then starts the new copy to swap files once we exit.
    /// Returns an error message, or null when the caller should now close the app.
    /// </summary>
    public static async Task<string?> PrepareAsync(Release rel, string page)
    {
        Busy = true;
        Changed?.Invoke();
        try
        {
            string dir = Path.Combine(UpdatesDir, rel.Version.ToString());
            string zip = dir + ".zip";
            Directory.CreateDirectory(UpdatesDir);
            byte[] data = await FetchAsync(rel.Url);
            if (!Verify(data, rel.Sig)) return "The download's signature doesn't match, so it wasn't installed.";
            await File.WriteAllBytesAsync(zip, data);
            ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
            string exe = Path.Combine(dir, "TaskMaster.exe");
            if (!File.Exists(exe)) return "The update is missing TaskMaster.exe, so it wasn't installed.";

            // The new version shows these once ("What's new").
            App.Settings.PendingNotesVersion = rel.Version.ToString();
            App.Settings.PendingNotes = rel.Notes;
            App.Settings.Save();

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "--apply-update", AppContext.BaseDirectory.TrimEnd('\\'), Environment.ProcessId.ToString(), page })
                psi.ArgumentList.Add(a);
            using var applier = Process.Start(psi)!;
            // The installer waits for us to close; if it died instead, stay open rather than leave nothing running.
            if (applier.WaitForExit(2000)) return "The update couldn't start its installer. Details are in the log.";
            Log.Info($"update {Current} -> {rel.Version}: handed over");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("update", ex);
            return "The update couldn't be downloaded: " + ex.Message;
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    // file:/// feeds are for local tests.
    private static Task<byte[]> FetchAsync(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsFile ? File.ReadAllBytesAsync(u.LocalPath) : s_http.GetByteArrayAsync(url);

    public static bool Verify(byte[] data, string sigBase64)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
            return ec.VerifyData(data, Convert.FromBase64String(sigBase64), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) { Log.Error("update verify", ex); return false; }
    }

    // ---------- the new copy's side (--apply-update <app folder> <old pid> <page>) ----------

    private static string MarkerFor(Version v) => Path.Combine(UpdatesDir, $"started-{v}");

    /// <summary>Called by the newly started version once its window is up, so the installer knows it worked.</summary>
    public static void ConfirmStarted()
    {
        try { Directory.CreateDirectory(UpdatesDir); File.WriteAllText(MarkerFor(Current), DateTime.Now.ToString("o")); }
        catch (Exception ex) { Log.Error("update confirm", ex); }
    }

    public static int Apply(string target, int oldPid, string page)
    {
        string previous = Path.Combine(UpdatesDir, "previous");
        string source = AppContext.BaseDirectory.TrimEnd('\\');
        try
        {
            try { using var old = Process.GetProcessById(oldPid); old.WaitForExit(30000); }
            catch (ArgumentException) { /* already gone */ }

            CopyTree(target, previous); // replaces the last one file by file; nothing is deleted
            CopyTree(source, target);
            Log.Info($"update applied to {target}");

            if (StartAndWait(target, page, MarkerFor(Current))) return 0;

            Log.Info("update: new version didn't start; rolling back");
            CopyTree(previous, target);
            // Don't offer the same broken version again; a later one is offered as usual.
            var s = Settings.Load();
            s.FailedUpdateVersion = Current.ToString();
            s.Save();
            StartAndWait(target, page, null);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Error("update apply", ex);
            if (Directory.Exists(previous)) { try { CopyTree(previous, target); } catch (Exception e2) { Log.Error("update rollback", e2); } }
            StartAndWait(target, page, null);
            return 1;
        }
    }

    /// <summary>Starts the installed app on the given page; with a marker, waits up to 45 s for it to say it's up.</summary>
    private static bool StartAndWait(string target, string page, string? marker)
    {
        try
        {
            var since = DateTime.Now.AddSeconds(-1);
            var psi = new ProcessStartInfo(Path.Combine(target, "TaskMaster.exe")) { UseShellExecute = false };
            psi.ArgumentList.Add("--after-update");
            psi.ArgumentList.Add(page);
            if (!Elevation.IsElevated) psi.ArgumentList.Add("--no-elevate");
            using var p = Process.Start(psi)!;
            if (marker == null) return true;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(45))
            {
                if (File.Exists(marker) && File.GetLastWriteTime(marker) >= since) return true;
                if (p.HasExited) return false;
                Thread.Sleep(250);
            }
            p.Kill();
            return false;
        }
        catch (Exception ex) { Log.Error("update start", ex); return false; }
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var d in Directory.GetDirectories(from, "*", System.IO.SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", System.IO.SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), overwrite: true);
    }
}
