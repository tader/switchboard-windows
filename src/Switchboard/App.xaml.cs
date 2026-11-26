using System.Windows;
using System.Diagnostics;
using Forms = System.Windows.Forms;
namespace Switchboard;
public partial class App : System.Windows.Application {
    private readonly CancellationTokenSource activationStop=new();
    private Mutex? mutex;
    private TrayPresentation? tray;
    private Manager? manager;
    private MainWindow? settings;
    private readonly System.Windows.Threading.DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromDays(1) };
    protected override void OnStartup(StartupEventArgs e) {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        ThemeMode=System.Windows.ThemeMode.System;
        base.OnStartup(e);
        mutex = new Mutex(true, "Local\\Switchboard-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, out var first);
        var pipeName="Switchboard-activate-"+System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        if (!first) {try{using var pipe=new System.IO.Pipes.NamedPipeClientStream(".",pipeName,System.IO.Pipes.PipeDirection.Out);pipe.Connect(2000);pipe.Write(System.Text.Encoding.UTF8.GetBytes("show-settings"));}catch(System.IO.IOException){}catch(TimeoutException){}Shutdown();return;}

        try { manager = new Manager(); } catch(Exception error) {System.Windows.MessageBox.Show(error.Message,"Switchboard needs attention");Shutdown();return;}
        async Task CheckUpdates() { if (manager.Settings.AutomaticAppChecks) { settings ??= new MainWindow(manager); await AppUpdates.Check(settings, manager, true); } }
        updateTimer.Tick += async (_, _) => await CheckUpdates(); updateTimer.Start();
        _ = Dispatcher.InvokeAsync(async () => { await Task.Delay(10000); await CheckUpdates(); });
        _=Task.Run(async()=>{while(!activationStop.IsCancellationRequested){try{using var pipe=new System.IO.Pipes.NamedPipeServerStream(pipeName,System.IO.Pipes.PipeDirection.In,1,System.IO.Pipes.PipeTransmissionMode.Byte,System.IO.Pipes.PipeOptions.Asynchronous|System.IO.Pipes.PipeOptions.CurrentUserOnly);await pipe.WaitForConnectionAsync(activationStop.Token);var bytes=new byte[32];var read=await pipe.ReadAsync(bytes,activationStop.Token);if(System.Text.Encoding.UTF8.GetString(bytes,0,read)=="show-settings")_ = Dispatcher.InvokeAsync(ShowSettings);}catch(OperationCanceledException){break;}catch(System.IO.IOException){}}});
        var icon = new System.Drawing.Icon(GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico"))!.Stream);
        tray = new TrayPresentation(icon, () => Dispatcher.Invoke(manager.OpenConnections));
        var menu = tray.Menu;
        Forms.ToolStripItem Add(string title, Action action) => menu.Items.Add(title, null, (_, _) => Dispatcher.Invoke(action));
        Add("Settings…", ShowSettings);
        var openItem=Add("Open Switchboard", manager.Open);
        var startItem=Add("Start", async () => await StartFromTray());
        var stopItem=Add("Stop", async () => await manager.Stop());
        var restartItem=Add("Restart", async () => { if(await manager.Stop()) await StartFromTray(); });
        Add("Show Logs", manager.ShowLogs);
        Add("Quit Switchboard", async () => { if (await manager.Shutdown()) Shutdown(); });
        menu.Opening+=(_,_)=>{
            openItem.Enabled=manager.ServerStarted;
            startItem.Enabled=!manager.Running&&!manager.Busy;
            stopItem.Enabled=manager.Running&&!manager.Busy;
            restartItem.Enabled=stopItem.Enabled;
        };
        tray.OnPrimaryClick(() => Dispatcher.Invoke(() => {
            if (manager.ServerStarted) manager.Open(); else ShowSettings();
        }));
        manager.Changed += () => Dispatcher.Invoke(() => {
            tray.Update(manager.ServerStatus,manager.Attention);
            settings?.Refresh();
        });
        tray.Update(manager.ServerStatus,manager.Attention);
        if (!manager.Settings.Configured || e.Args.Contains("--show-settings")) ShowSettings();
        if (manager.Settings.Configured && manager.Settings.StartServer) _ = manager.Start();
    }
    private Task StartFromTray() => settings?.StartServer() ?? manager!.Start();
    private void ShowSettings() { settings ??= new MainWindow(manager!); settings.Refresh(); settings.Show(); settings.WindowState = WindowState.Normal; settings.Activate(); }
    protected override void OnExit(ExitEventArgs e) { activationStop.Cancel(); tray?.Dispose(); manager?.Dispose(); mutex?.Dispose(); base.OnExit(e); }
}
