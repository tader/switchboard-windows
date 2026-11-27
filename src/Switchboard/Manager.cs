using System.Diagnostics;
using System.IO;
using System.Text.Json;
namespace Switchboard;
public sealed class Manager : IDisposable {
    public Settings Settings { get; private set; }
    readonly Action<Settings> saveSettings;
    public string Status { get; private set; } = "Not running";
    public string? Error { get; private set; }
    public int? OwnedSupervisorId => Running ? child!.Id : null;
    public bool Running => child is { HasExited: false };
    public Guid Session { get; private set; } = Guid.NewGuid();
    public bool Busy { get; private set; }
    public bool CanCancelInstallation {get;private set;}
    public string UpdateState {get;private set;}="";
    public string RuntimeVersion {get {try {var pointer=Path.Combine(Settings.StatePath,"current.json");var runtime=File.Exists(pointer)?JsonDocument.Parse(File.ReadAllText(pointer)).RootElement.GetProperty("release").GetString():Settings.RuntimePath;return JsonDocument.Parse(File.ReadAllText(Path.Combine(runtime!,"runtime.json"))).RootElement.GetProperty("version").GetString()!;}catch{return "Not installed";}}}
    public int Attention { get; private set; }
    private long lastStatus;
    internal string ServerStatus => Error != null ? "Needs attention" :
        !Running && !Busy && !Settings.Configured ? "Setup required" : Status;
    internal string? AccountStatus => AccountReady ? "Account ready" :
        NeedsAccountSetup ? "Finish account setup" : null;
    public bool AccountReady { get; private set; }
    internal bool NeedsAccountSetup { get; private set; }
    internal bool ServerStarted => Running && Status == "Running on this PC" && !stopping;
    public event Action? Changed;
    public void ApplySettings(Settings value) { saveSettings(value); Settings = value; Notify(); }
    public CancellationTokenSource? Installation { get; private set; }
    internal Func<Settings, Action<string>, CancellationToken, Task<string>> RuntimeInstallation { get; set; } = RuntimeInstaller.Install;
    private Process? child;
    private Task? reader;
    private readonly SemaphoreSlim commands = new(1);
    private readonly System.Windows.Threading.DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool stopping;
    private TaskCompletionSource<JsonElement>? account;
    private string? accountId;
    private Job job = new();
    private bool disposed;
    public Action<string> AccountBrowserOpener { get; set; } = OpenUrl;
    public Manager(Settings? settings=null) : this(settings,value=>value.Save()) {}
    internal Manager(Settings? settings, Action<Settings> saveSettings) {
        Settings=settings??global::Switchboard.Settings.Load();
        this.saveSettings=saveSettings;
        var journal=Path.Combine(Settings.StatePath,"runtime-install.json");
        if(File.Exists(journal)) {
            using var controller=RuntimeInstaller.Lease(Path.Combine(Settings.StatePath,"controller-lease.db"));
            using var server=RuntimeInstaller.Lease(Path.Combine(Settings.DataPath,".server-lease.db"));
            var pending=JsonSerializer.Deserialize<Settings>(File.ReadAllText(journal))!; pending.Validate();
            PreviewLifecycle.RecoverSelection(Settings, pending);
            pending.Save();Settings=pending;File.Delete(journal);
        }
        poll.Tick += async (_, _) => {
            if (!Running || Status == "Stopping…") return;
            if (lastStatus != 0 && Environment.TickCount64 - lastStatus > 8000) {
                ClearAccountStatus();
                Notify();
            }
            try { await Send(new { action = "status" }); }
            catch (IOException) { ClearAccountStatus(); Notify(); }
        };
    }
    private void ClearAccountStatus() {
        lastStatus=0;
        Attention=0;
        AccountReady=false;
        NeedsAccountSetup=false;
    }
    public void Notify() => Changed?.Invoke();
    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    public void ShowLogs() { global::Switchboard.Settings.PrivateFolder(Settings.StatePath);OpenUrl(Settings.StatePath); }
    public void Open() { if (ServerStarted) OpenUrl(Settings.BrowserUrl); }
    internal Action<string> ConnectionsBrowserOpener { get; set; } = OpenUrl;
    internal void OpenConnections() {
        if (ServerStarted && Attention > 0) ConnectionsBrowserOpener(Settings.BrowserUrl.TrimEnd('/')+"/connections");
    }
    public async Task Start() {
        if (Busy || Running) return;
        await Work(async () => {
            if (Settings.RuntimePath == null) await InstallCore();
            await Launch();
        });
    }
    internal Task InstallAndStart() => Work(async () => {
        if (Running && !await Stop()) return;
        await InstallCore();
        await Launch();
    });
    private async Task Launch() {
        Settings.Validate(); global::Switchboard.Settings.PrivateFolder(Settings.DataPath); global::Switchboard.Settings.PrivateFolder(Settings.StatePath);
        var serverEnvironment = GithubApi.ServerEnvironment(Settings, new GithubTokenStore().Read());
        var pointer = Path.Combine(Settings.StatePath, "current.json");
        var runtime = Settings.RuntimePath ?? throw new InvalidOperationException("Install a runtime first.");
        if (File.Exists(pointer)) {
            using var current = JsonDocument.Parse(File.ReadAllText(pointer));
            runtime = current.RootElement.GetProperty("release").GetString() ?? throw new InvalidOperationException("Selected runtime is missing.");
        }
        var start = new ProcessStartInfo(Path.Combine(runtime, "bin", "node.exe")) { WorkingDirectory = runtime, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--disable-warning=ExperimentalWarning"); start.ArgumentList.Add("updater/desktop.ts");
        start.Environment.Remove("SWITCHBOARD_PLUGIN_INSTALL_GITHUB_TOKEN");
        start.Environment.Remove("NODE_OPTIONS"); start.Environment.Remove("NODE_PATH");
        Session = Guid.NewGuid();
        ClearAccountStatus();
        Status = "Starting…";
        child = Process.Start(start) ?? throw new Exception("Could not start Switchboard.");
        job.Attach(child);
        // Never forward raw stderr, IPC payloads or setup links into logs.
        _ = child.StandardError.ReadToEndAsync();
        reader = Read(child);
        await Send(new { action = "start", settings = new { instance = "local-manager", mode = "runtime", runtimePreviewLifecycle = true, dataDir = Settings.DataPath, stateDir = Settings.StatePath, initialRelease = runtime, appUrl = Settings.ControlUrl, env = serverEnvironment } });
        poll.Start();
    }
    private async Task Read(Process process) {
        try { while (await process.StandardOutput.ReadLineAsync() is { } line) {
            if (line.Length > 1024 * 1024) throw new Exception("Invalid manager response.");
            var before=(Status,AccountReady,NeedsAccountSetup,Attention,Error,UpdateState);
            var m = JsonDocument.Parse(line).RootElement;
            switch (m.GetProperty("event").GetString()) {
                case "started":
                    if (stopping || Status=="Stopping…") break;
                    Status = "Running on this PC";
                    NeedsAccountSetup = m.TryGetProperty("setupUrl", out var setup) && setup.ValueKind == JsonValueKind.String;
                    if (NeedsAccountSetup) AccountBrowserOpener(setup.GetString()!);
                    await Send(new { action = "status" });
                    break;
                case "status":
                    UpdateState=m.TryGetProperty("job",out var jobState)&&jobState.ValueKind==JsonValueKind.Object&&jobState.TryGetProperty("state",out var jobPhase)?jobPhase.GetString()??"":"";
                    if (!ServerStarted) break;
                    if (m.TryGetProperty("ready", out var ready) && ready.ValueKind is JsonValueKind.True or JsonValueKind.False) {
                        lastStatus=Environment.TickCount64;
                        AccountReady = ready.GetBoolean();
                        NeedsAccountSetup = !AccountReady;
                        Attention = AccountReady && m.TryGetProperty("attention", out var attention) &&
                            attention.ValueKind == JsonValueKind.Number && attention.TryGetInt32(out var count) ? Math.Max(0,count) : 0;
                    } else ClearAccountStatus();
                    break;
                case "server-failed":
                    ClearAccountStatus();
                    Status = "Not running";
                    Error = "The server stopped repeatedly. Check the logs before starting it again.";
                    break;
                case "stopped": ClearAccountStatus(); Status = "Not running"; break;
                case "account-result": if (m.GetProperty("requestId").GetString() == accountId) account?.TrySetResult(m.Clone()); break;
                case "error":
                    ClearAccountStatus();
                    Error = "The action could not complete. Check folders, port and the recovery journal.";
                    if (Status == "Starting…") {
                        Status = "Stopping…";
                        await Send(new { action = "quit" });
                    }
                    break;
            }
            if(before!=(Status,AccountReady,NeedsAccountSetup,Attention,Error,UpdateState))Notify();
        } } catch { Error = "The local manager disconnected. Stop and inspect the recovery journal."; }
        finally { ClearAccountStatus(); poll.Stop(); if(ReferenceEquals(child,process)){job.Dispose();if(!disposed)job=new Job();}Status = "Not running"; Notify(); }
    }
    private async Task Send(object message) {
        await commands.WaitAsync(); try { if (child is { HasExited: false }) { await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)); await child.StandardInput.FlushAsync(); } } finally { commands.Release(); }
    }
    public async Task<bool> Stop() {
        if (!Running) return true;
        if (stopping) return false;
        stopping=true;
        Session = Guid.NewGuid();
        Status = "Stopping…";
        ClearAccountStatus();
        Notify();
        try { await Send(new { action = "quit" }); await child!.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));if(reader!=null)await reader; poll.Stop(); child.Dispose(); child = null; Status = "Not running"; Notify(); return true; }
        catch { Error = "Switchboard is still finishing an operation. Wait and retry."; Notify(); return false; }
        finally { stopping=false; }
    }
    public Task CancelServerUpdate()=>Send(new {action="cancel"});
    public Task<JsonElement> Account(string action) {
        if(action is not ("account-users" or "account-setup"))throw new Exception("This account action requires fresh Windows verification.");
        return AccountAction(action);
    }
    public async Task<JsonElement> RecoverAccount(string userId,IntPtr owner,Func<bool> visible) {
        var session=Session;JsonElement result=default;
        await RecoveryAuthorization.Run(async()=>{
            var available=await Windows.Security.Credentials.UI.UserConsentVerifier.CheckAvailabilityAsync();
            if(available!=Windows.Security.Credentials.UI.UserConsentVerifierAvailability.Available)throw new Exception("Windows verification is unavailable. Set up Windows Hello/PIN in Windows Settings, then retry.");
            return await Windows.Security.Credentials.UI.UserConsentVerifierInterop.RequestVerificationForWindowAsync(owner,"Recover a Switchboard password on this PC.")==Windows.Security.Credentials.UI.UserConsentVerificationResult.Verified;
        },()=>visible()&&session==Session&&ServerStarted&&!Busy,async()=>result=await AccountAction("account-recovery",userId));
        return result;
    }
    private async Task<JsonElement> AccountAction(string action, string? userId = null) {
        if (account != null) throw new Exception("An account action is already in progress.");
        var pending = account = new(TaskCreationOptions.RunContinuationsAsynchronously); accountId = Guid.NewGuid().ToString();
        try { await Send(new { action, requestId = accountId, userId }); var result = await pending.Task.WaitAsync(TimeSpan.FromSeconds(35)); if (result.TryGetProperty("error", out _)) throw new Exception("Update Switchboard or retry when it is idle."); return result; }
        finally { account = null; accountId = null; }
    }
    private Task currentWork=Task.CompletedTask;
    public Task Work(Func<Task> action) { if(Busy)return Task.CompletedTask;return currentWork=RunWork(action); }
    public async Task<bool> Shutdown() { if(Busy){Installation?.Cancel();await currentWork;}return await Stop(); }
    private async Task RunWork(Func<Task> action) { if (Busy) return; Busy = true; Error = null; Notify(); try { await action(); } catch (Exception e) { Error = e.Message; if(!Running)Status="Not running"; } finally { Busy = false; Notify(); } }
    private async Task InstallCore() {
        Installation = new CancellationTokenSource();CanCancelInstallation=true;
        string path;
        try { path = await Task.Run(() => RuntimeInstallation(Settings, text => { Status = text;if(text=="Finishing installation…")CanCancelInstallation=false; Notify(); }, Installation.Token)); }
        catch { Status = "Not running"; throw; }
        finally { Installation.Dispose(); Installation = null;CanCancelInstallation=false; }
        Settings.RuntimePath = path; Settings.Configured = true; Settings.Save(); File.Delete(Path.Combine(Settings.StatePath,"runtime-install.json")); Status = "Not running";
    }
    public void Dispose() { disposed=true;poll.Stop(); job.Dispose(); child?.Dispose(); }
}
