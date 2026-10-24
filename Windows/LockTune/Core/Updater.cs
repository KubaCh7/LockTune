using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LockTune.Core;

/// In-app updates from the rolling "windows-latest" GitHub release: compares
/// build numbers, downloads LockTune.exe next to the running one and swaps it.
public sealed class Updater
{
    private const string ReleaseApi = "https://api.github.com/repos/KubaCh7/LockTune/releases/tags/windows-latest";
    public const string ReleasePage = "https://github.com/KubaCh7/LockTune/releases/tag/windows-latest";

    private static readonly HttpClient Http = CreateClient();
    private readonly AppSettings settings;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromHours(6) };
    private bool busy;
    private string? exeUrl;

    public string StatusText { get; private set; } = "Not checked yet";
    public int? AvailableBuild { get; private set; }
    public bool UpdateAvailable => AvailableBuild > AppCoordinator.Build;

    /// (build, userInitiated) — the app asks the user whether to install.
    public event Action<int, bool>? Found;
    public event Action? StatusChanged;

    public Updater(AppSettings settings)
    {
        this.settings = settings;
        timer.Tick += (_, _) => { if (settings.AutoCheckUpdates) CheckNow(false); };
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LockTune-Windows-Updater");
        return client;
    }

    public void Start()
    {
        CleanUpOldVersion();
        timer.Start();
        if (!settings.AutoCheckUpdates) return;
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        first.Tick += (_, _) => { first.Stop(); CheckNow(false); };
        first.Start();
    }

    public async void CheckNow(bool userInitiated)
    {
        if (busy) return;
        busy = true;
        SetStatus("Checking…");
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(ReleaseApi));
            string? versionUrl = null;
            exeUrl = null;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();
                if (name == "version-windows.json") versionUrl = url;
                if (name == "LockTune.exe") exeUrl = url;
            }
            if (versionUrl == null || exeUrl == null) throw new InvalidDataException("release has no Windows build");
            using var version = JsonDocument.Parse(await Http.GetStringAsync(versionUrl));
            AvailableBuild = version.RootElement.GetProperty("build").GetInt32();
            if (UpdateAvailable)
            {
                SetStatus($"Build {AvailableBuild} is available");
                Found?.Invoke(AvailableBuild.Value, userInitiated);
            }
            else SetStatus($"Up to date (checked {DateTime.Now:t})");
        }
        catch (Exception e)
        {
            SetStatus("Couldn't check: " + e.Message);
        }
        finally
        {
            busy = false;
        }
    }

    /// Downloads and installs the new build, then restarts. Returns false
    /// (and opens the download page) when the exe can't be replaced.
    public async Task<bool> InstallAsync()
    {
        var current = Environment.ProcessPath;
        if (exeUrl == null || current == null) return false;
        var folder = Path.GetDirectoryName(current)!;
        var downloaded = Path.Combine(folder, "LockTune.new.exe");
        var old = Path.Combine(folder, "LockTune.old.exe");
        try
        {
            SetStatus("Downloading update…");
            var bytes = await Http.GetByteArrayAsync(exeUrl);
            // Sanity check: a Windows executable of a plausible size.
            if (bytes.Length < 1_000_000 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                throw new InvalidDataException("download is not a valid LockTune.exe");
            await File.WriteAllBytesAsync(downloaded, bytes);
            if (File.Exists(old)) File.Delete(old);
            File.Move(current, old);         // a running exe can be renamed…
            File.Move(downloaded, current);  // …and the new one put in its place.
            Process.Start(new ProcessStartInfo(current, "--updated") { UseShellExecute = false });
            Application.Current.Shutdown();
            return true;
        }
        catch (Exception e)
        {
            SetStatus("Update failed: " + e.Message);
            try { if (File.Exists(downloaded)) File.Delete(downloaded); } catch { }
            try { if (!File.Exists(current) && File.Exists(old)) File.Move(old, current); } catch { }
            AppCoordinator.OpenUrl(ReleasePage);
            return false;
        }
    }

    private static void CleanUpOldVersion()
    {
        var current = Environment.ProcessPath;
        if (current == null) return;
        var old = Path.Combine(Path.GetDirectoryName(current)!, "LockTune.old.exe");
        Task.Run(async () =>
        {
            for (int i = 0; i < 10 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch { await Task.Delay(1000); }
            }
        });
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke();
    }
}
