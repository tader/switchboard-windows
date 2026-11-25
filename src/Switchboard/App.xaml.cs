using System.Windows;
using System.Diagnostics;
using Forms = System.Windows.Forms;
namespace Switchboard;
public partial class App : System.Windows.Application {
    private Mutex? mutex;
    private Forms.NotifyIcon? tray;
    private Manager? manager;
    private MainWindow? settings;
    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        mutex = new Mutex(true, "Local\\Switchboard-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, out var first);
        if (!first) { Shutdown(); return; }
        manager = new Manager();
        tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico"))!.Stream), Text = "Switchboard", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        void Add(string title, Action action) { menu.Items.Add(title, null, (_, _) => Dispatcher.Invoke(action)); }
        Add("Open Switchboard", () => manager.Open());
        Add("Start", async () => await manager.Start());
        Add("Stop", async () => await manager.Stop());
        Add("Restart", async () => { await manager.Stop(); await manager.Start(); });
        Add("Settings…", ShowSettings);
        Add("Show Logs", () => Manager.OpenUrl(manager.Settings.StatePath));
        Add("Quit Switchboard", async () => { if (await manager.Stop()) Shutdown(); });
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(() => { if (manager.Running) manager.Open(); else ShowSettings(); }); };
        manager.Changed += () => Dispatcher.Invoke(() => { tray.Text = ("Switchboard: " + manager.Status + (manager.Attention > 0 ? $" · {manager.Attention} need sign-in" : ""))[..Math.Min(63, ("Switchboard: " + manager.Status + (manager.Attention > 0 ? $" · {manager.Attention} need sign-in" : "")).Length)]; settings?.Refresh(); });
        if (!manager.Settings.Configured || e.Args.Contains("--show-settings")) ShowSettings();
        if (manager.Settings.Configured && manager.Settings.StartServer) _ = manager.Start();
    }
    private void ShowSettings() { settings ??= new MainWindow(manager!); settings.Show(); settings.WindowState = WindowState.Normal; settings.Activate(); }
    protected override void OnExit(ExitEventArgs e) { tray?.Dispose(); manager?.Dispose(); mutex?.Dispose(); base.OnExit(e); }
}
