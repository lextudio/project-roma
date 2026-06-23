using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

using Xunit;

namespace Roma.IntegrationTests;

// Launches the Roma desktop app once for a test collection, waits for the DevFlow agent to come up,
// and exposes helpers to invoke "roma.probe.*" actions. Disposing kills the app. Tests that need a
// clean process (e.g. persistence-across-restart) can WipeSettings()+RestartAsync().
public sealed class RomaAppFixture : IAsyncLifetime
{
    const int Port = 9223; // App.xaml.cs hardwires the DevFlow agent to 9223.
    static readonly string BaseUrl = $"http://localhost:{Port}";

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    Process? _app;

    public string SettingsFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LeXtudio", "Roma", "Roma.ILSpy.xml");

    public string RomaHostProjectPath { get; } = LocateRomaHostProject();

    public async Task InitializeAsync()
    {
        // Clear any stale instance from a prior aborted run so the new one can bind the DevFlow port.
        StopApp();
        await WaitForPortFreeAsync(TimeSpan.FromSeconds(30));
        await StartAsync();
    }

    public async Task DisposeAsync()
    {
        StopApp();
        _http.Dispose();
        await Task.CompletedTask;
    }

    public void WipeSettings()
    {
        try { if (File.Exists(SettingsFilePath)) File.Delete(SettingsFilePath); }
        catch { /* best effort */ }
    }

    public async Task RestartAsync()
    {
        StopApp();
        await WaitForPortFreeAsync(TimeSpan.FromSeconds(30));
        await StartAsync();
    }

    // After killing the app, the OS can hold the DevFlow port briefly; binding a new instance before
    // it frees leaves the agent unreachable and cascades failures into later tests. Wait it out.
    async Task WaitForPortFreeAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            bool romaGone = Process.GetProcessesByName("Roma.Host").Length == 0;
            if (romaGone && !IsPortInUse(Port))
                return;
            await Task.Delay(500);
        }
    }

    static bool IsPortInUse(int port)
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(ep => ep.Port == port);
        }
        catch { return false; }
    }

    async Task StartAsync()
    {
        // Run pre-built to avoid the exe-lock/rebuild cost; the suite assumes Roma.Host is already
        // built in a probe-enabled configuration. `dotnet run --no-build` launches that build.
        // Redirect stdout/stderr AND drain them asynchronously. Uno logs heavily; under `dotnet test`
        // the inherited console is a captured pipe the test host does not drain, so once its OS buffer
        // fills the app blocks and dies. Redirecting + reading continuously keeps the pipe empty.
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(RomaHostProjectPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "run", "--project", RomaHostProjectPath, "-f", "net10.0-desktop", "--no-build" })
            psi.ArgumentList.Add(a);

        _app = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Roma.Host");
        _app.OutputDataReceived += (_, _) => { };   // drain so the pipe never fills
        _app.ErrorDataReceived += (_, _) => { };
        _app.BeginOutputReadLine();
        _app.BeginErrorReadLine();
        await WaitForAgentAsync(TimeSpan.FromSeconds(90));
        await WarmUpAsync(TimeSpan.FromSeconds(60));
    }

    // The DevFlow agent answers /status before the UI thread finishes the initial decompile, during
    // which dispatcher-marshalled probes time out. Poll a read-only probe until it responds cleanly,
    // so tests start against a settled UI.
    async Task WarmUpAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var s = await InvokeAsync("roma.probe.state");
                if (s.TryGetProperty("rows", out _)) return;
            }
            catch { /* UI not responsive yet */ }
            await Task.Delay(500);
        }
        throw new TimeoutException("Roma UI did not become responsive to probes within " + timeout);
    }

    void StopApp()
    {
        // Kill the whole tree: `dotnet run` spawns the actual Roma.Host child process.
        try { if (_app is { HasExited: false }) _app.Kill(entireProcessTree: true); } catch { }
        try { foreach (var p in Process.GetProcessesByName("Roma.Host")) { try { p.Kill(true); } catch { } } } catch { }
        _app = null;
    }

    async Task WaitForAgentAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var resp = await _http.GetAsync($"{BaseUrl}/api/v1/agent/status");
                if (resp.IsSuccessStatusCode) return;
            }
            catch { /* not up yet */ }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"DevFlow agent did not respond on {BaseUrl} within {timeout}.");
    }

    // Invokes a probe action and returns the parsed JSON state ('returnValue' is the probe's string).
    public async Task<JsonElement> InvokeAsync(string action, params object[] args)
    {
        var body = JsonSerializer.Serialize(new { args });
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{BaseUrl}/api/v1/invoke/actions/{action}", content);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Probe '{action}' failed ({(int)resp.StatusCode}). Request body: {body}. Response: {err}");
        }
        var envelope = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var raw = envelope.TryGetProperty("returnValue", out var rv) ? rv.GetString() : null;
        if (string.IsNullOrEmpty(raw))
            throw new InvalidOperationException($"Probe '{action}' returned no value: {envelope}");
        var state = JsonDocument.Parse(raw).RootElement.Clone();
        if (state.TryGetProperty("error", out var probeErr))
            throw new InvalidOperationException($"Probe '{action}' reported error: {probeErr.GetString()} (raw: {raw})");
        return state;
    }

    // Polls a probe until 'predicate' holds or it times out (for async effects like decompile or the
    // reset-to-New-Tab marshal). Returns the last snapshot.
    public async Task<JsonElement> PollAsync(string action, Func<JsonElement, bool> predicate, int timeoutMs = 8000, params object[] args)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            last = await InvokeAsync(action, args);
            if (predicate(last)) return last;
            await Task.Delay(250);
        }
        return last;
    }

    static string LocateRomaHostProject()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "src", "Roma.Host", "Roma.Host.csproj");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("Could not locate src/Roma.Host/Roma.Host.csproj by walking up from " + AppContext.BaseDirectory);
    }
}

[CollectionDefinition("Roma app")]
public sealed class RomaAppCollection : ICollectionFixture<RomaAppFixture> { }
