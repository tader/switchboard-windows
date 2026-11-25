using System.Windows;
using System.Diagnostics;
using Forms = System.Windows.Forms;
namespace Switchboard;
public partial class App : System.Windows.Application {
    private Mutex? mutex;
    private Forms.NotifyIcon? tray;
    private Manager? manager;
    private MainWindow? settings;
    private readonly System.Windows.Threading.DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromDays(1) };
    protected override void OnStartup(StartupEventArgs e) {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        ThemeMode=System.Windows.ThemeMode.System;
        base.OnStartup(e);
        mutex = new Mutex(true, "Local\\Switchboard-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, out var first);
        if (!first) { Shutdown(); return; }
        try { manager = new Manager(); } catch(Exception error) {System.Windows.MessageBox.Show(error.Message,"Switchboard needs attention");Shutdown();return;}
        async Task CheckUpdates() { if (manager.Settings.AutomaticAppChecks) { settings ??= new MainWindow(manager); await AppUpdates.Check(settings, manager, true); } }
        updateTimer.Tick += async (_, _) => await CheckUpdates(); updateTimer.Start();
        _ = Dispatcher.InvokeAsync(async () => { await Task.Delay(10000); await CheckUpdates(); });
        tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico"))!.Stream), Text = "Switchboard", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        Forms.ToolStripItem Add(string title, Action action) => menu.Items.Add(title, null, (_, _) => Dispatcher.Invoke(action));
        var openItem=Add("Open Switchboard", () => manager.Open());
        var startItem=Add("Start", async () => await manager.Start());
        var stopItem=Add("Stop", async () => await manager.Stop());
        var restartItem=Add("Restart", async () => { await manager.Stop(); await manager.Start(); });
        Add("Settings…", ShowSettings);
        Add("Show Logs", () => Manager.OpenUrl(manager.Settings.StatePath));
        Add("Quit Switchboard", async () => { if (await manager.Shutdown()) Shutdown(); });
        var signIn=Add("Sign in to connections",()=>Manager.OpenUrl(manager.Settings.BrowserUrl.TrimEnd('/')+"/connections"));
        menu.Opening+=(_,_)=>{openItem.Enabled=manager.Running;startItem.Enabled=!manager.Running&&!manager.Busy;stopItem.Enabled=manager.Running&&!manager.Busy;restartItem.Enabled=manager.Running&&!manager.Busy;signIn.Visible=manager.Attention>0;signIn.Text=$"Sign in to connections ({manager.Attention})";};
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(() => { if (manager.Running) manager.Open(); else ShowSettings(); }); };
        manager.Changed += () => Dispatcher.Invoke(() => { tray.Text = ("Switchboard: " + manager.Status + (manager.Attention > 0 ? $" · {manager.Attention} need sign-in" : ""))[..Math.Min(63, ("Switchboard: " + manager.Status + (manager.Attention > 0 ? $" · {manager.Attention} need sign-in" : "")).Length)]; settings?.Refresh(); });
        if (!manager.Settings.Configured || e.Args.Contains("--show-settings")) ShowSettings();
        if (manager.Settings.Configured && manager.Settings.StartServer) _ = manager.Start();
    }
    private void ShowSettings() { settings ??= new MainWindow(manager!); settings.Show(); settings.WindowState = WindowState.Normal; settings.Activate(); }
    protected override void OnExit(ExitEventArgs e) { tray?.Dispose(); manager?.Dispose(); mutex?.Dispose(); base.OnExit(e); }
}
