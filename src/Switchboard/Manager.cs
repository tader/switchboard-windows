using System.Diagnostics;
using System.IO;
using System.Text.Json;
namespace Switchboard;
public sealed class Manager : IDisposable {
    public Settings Settings { get; private set; } = global::Switchboard.Settings.Load();
    public string Status { get; private set; } = "Not running";
    public string? Error { get; private set; }
    public bool Running => child is { HasExited: false };
    public Guid Session { get; private set; } = Guid.NewGuid();
    public bool Busy { get; private set; }
    public bool CanCancelInstallation {get;private set;}
    public string RuntimeVersion {get {try {return JsonDocument.Parse(File.ReadAllText(Path.Combine(Settings.RuntimePath!,"runtime.json"))).RootElement.GetProperty("version").GetString()!;}catch{return "Not installed";}}}
    public int Attention { get; private set; }
    public bool AccountReady { get; private set; }
    public event Action? Changed;
    public void ApplySettings(Settings value) { value.Save(); Settings = value; Notify(); }
    public CancellationTokenSource? Installation { get; private set; }
    private Process? child;
    private Task? reader;
    private readonly SemaphoreSlim commands = new(1);
    private readonly System.Windows.Threading.DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private TaskCompletionSource<bool>? stopped;
    private TaskCompletionSource<JsonElement>? account;
    private string? accountId;
    private readonly Job job = new();
    public Action<string> AccountBrowserOpener { get; set; } = OpenUrl;
    public Manager(Settings? settings=null) {
        if(settings!=null)Settings=settings;
        var journal=Path.Combine(Settings.StatePath,"runtime-install.json");
        if(File.Exists(journal)) {
            using var controller=RuntimeInstaller.Lease(Path.Combine(Settings.StatePath,"controller-lease.db"));
            using var server=RuntimeInstaller.Lease(Path.Combine(Settings.DataPath,".server-lease.db"));
            var pending=JsonSerializer.Deserialize<Settings>(File.ReadAllText(journal))!; pending.Validate();
            if(pending.RuntimePath==null || !File.Exists(Path.Combine(pending.RuntimePath,"runtime.json")))throw new Exception("Installation recovery requires inspection. Preserve both folders.");
            File.WriteAllText(Path.Combine(Settings.StatePath,"current.json.tmp"),JsonSerializer.Serialize(new {release=pending.RuntimePath}));File.Move(Path.Combine(Settings.StatePath,"current.json.tmp"),Path.Combine(Settings.StatePath,"current.json"),true);
            pending.Save();Settings=pending;File.Delete(journal);
        }
 poll.Tick += async (_, _) => { if (Running) await Send(new { action = "status" }); }; }
    public void Notify() => Changed?.Invoke();
    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    public void Open() { if (Running) OpenUrl(Settings.BrowserUrl); }
    public async Task Start() {
        if (Busy || Running) return;
        await Work(async () => {
            if (Settings.RuntimePath == null) await InstallCore();
            Settings.Validate(); global::Switchboard.Settings.PrivateFolder(Settings.DataPath); global::Switchboard.Settings.PrivateFolder(Settings.StatePath);
            var start = new ProcessStartInfo(Path.Combine(Settings.RuntimePath!, "bin", "node.exe")) { WorkingDirectory = Settings.RuntimePath!, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--disable-warning=ExperimentalWarning"); start.ArgumentList.Add("updater/desktop.ts");
            start.Environment.Remove("NODE_OPTIONS"); start.Environment.Remove("NODE_PATH");
            Session = Guid.NewGuid();
            child = Process.Start(start) ?? throw new Exception("Could not start Switchboard.");
            job.Attach(child);
            // Never forward raw stderr, IPC payloads or setup links into logs.
            _ = child.StandardError.ReadToEndAsync();
            reader = Read(child);
            await Send(new { action = "start", settings = new { instance = "local-manager", mode = "runtime", dataDir = Settings.DataPath, stateDir = Settings.StatePath, initialRelease = Settings.RuntimePath, appUrl = Settings.ControlUrl, env = new Dictionary<string,string> { ["SWITCHBOARD_HOST"] = Settings.Host, ["SWITCHBOARD_PORT"] = Settings.Port.ToString(), ["SWITCHBOARD_PUBLIC_URL"] = Settings.ControlUrl, ["SWITCHBOARD_LOCAL_ACCOUNT_RECOVERY"] = "1" } } });
            Status = "Starting…"; poll.Start();
        });
    }
    private async Task Read(Process process) {
        try { while (await process.StandardOutput.ReadLineAsync() is { } line) {
            if (line.Length > 1024 * 1024) throw new Exception("Invalid manager response.");
            var m = JsonDocument.Parse(line).RootElement;
            switch (m.GetProperty("event").GetString()) {
                case "started": Status = "Running on this PC"; if (m.TryGetProperty("setupUrl", out var setup) && setup.ValueKind == JsonValueKind.String) AccountBrowserOpener(setup.GetString()!); break;
                case "status": AccountReady = m.TryGetProperty("ready", out var ready) && ready.GetBoolean(); Attention = m.TryGetProperty("attention", out var attention) ? attention.GetInt32() : 0; break;
                case "stopped": Status = "Not running"; stopped?.TrySetResult(true); break;
                case "account-result": if (m.GetProperty("requestId").GetString() == accountId) account?.TrySetResult(m.Clone()); break;
                case "error": Error = "The action could not complete. Check folders, port and the recovery journal."; break;
            }
            Notify();
        } } catch { Error = "The local manager disconnected. Stop and inspect the recovery journal."; }
        finally { Status = "Not running"; Notify(); }
    }
    private async Task Send(object message) {
        await commands.WaitAsync(); try { if (child is { HasExited: false }) { await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)); await child.StandardInput.FlushAsync(); } } finally { commands.Release(); }
    }
    public async Task<bool> Stop() {
        if (!Running) return true;
        Session = Guid.NewGuid();
        stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Send(new { action = "quit" });
        try { await child!.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5)); poll.Stop(); child.Dispose(); child = null; Status = "Not running"; Notify(); return true; }
        catch { Error = "Switchboard is still finishing an operation. Wait and retry."; Notify(); return false; }
    }
    public async Task<JsonElement> Account(string action, string? userId = null) {
        if (account != null) throw new Exception("An account action is already in progress.");
        var pending = account = new(TaskCreationOptions.RunContinuationsAsynchronously); accountId = Guid.NewGuid().ToString();
        try { await Send(new { action, requestId = accountId, userId }); var result = await pending.Task.WaitAsync(TimeSpan.FromSeconds(35)); if (result.TryGetProperty("error", out _)) throw new Exception("Update Switchboard or retry when it is idle."); return result; }
        finally { account = null; accountId = null; }
    }
    private Task currentWork=Task.CompletedTask;
    public Task Work(Func<Task> action) { if(Busy)return Task.CompletedTask;return currentWork=RunWork(action); }
    public async Task<bool> Shutdown() { if(Busy){Installation?.Cancel();await currentWork;}return await Stop(); }
    private async Task RunWork(Func<Task> action) { if (Busy) return; Busy = true; Error = null; Notify(); try { await action(); } catch (Exception e) { Error = e.Message; } finally { Busy = false; Notify(); } }
    public async Task InstallCore() {
        if (Running && !await Stop()) return;
        Installation = new CancellationTokenSource();CanCancelInstallation=true;
        string path;
        try { path = await Task.Run(() => RuntimeInstaller.Install(Settings, text => { Status = text;if(text=="Finishing installation…")CanCancelInstallation=false; Notify(); }, Installation.Token)); }
        finally { Installation.Dispose(); Installation = null;CanCancelInstallation=false; }
        Settings.RuntimePath = path; Settings.Configured = true; Settings.Save(); File.Delete(Path.Combine(Settings.StatePath,"runtime-install.json")); Status = "Not running";
    }
    public void Dispose() { poll.Stop(); job.Dispose(); child?.Dispose(); }
}
