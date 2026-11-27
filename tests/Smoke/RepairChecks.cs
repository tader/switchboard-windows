using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Switchboard;

static class RepairChecks {
    static void Require(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }

    static async Task Node(string runtime, string data, params string[] arguments) {
        var start = new ProcessStartInfo(Path.Combine(runtime, "bin", "node.exe")) {
            WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true
        };
        start.Environment["SWITCHBOARD_DATA_DIR"] = data;
        start.Environment.Remove("NODE_OPTIONS");
        start.Environment.Remove("NODE_PATH");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not run the repair fixture.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try {
            await child.WaitForExitAsync(timeout.Token);
        } catch (OperationCanceledException) {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            throw;
        }
        Require(child.ExitCode == 0, "Repair fixture failed. See its preceding output.");
    }

    internal static void Run(string fixedRuntime, string browserScript, string screenshots, Action<Task> wait,
        Func<DependencyObject, IEnumerable<DependencyObject>> descendants) {
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary {
            Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml")
        });
        var root = Directory.CreateTempSubdirectory("switchboard-repair-").FullName;
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var settings = new Settings {
            DataPath = Path.Combine(root, "data"), StatePath = Path.Combine(root, "state"),
            Port = port, BrowserUrl = $"http://127.0.0.1:{port}", Pin = "v0.16.0"
        };
        Settings.PrivateFolder(settings.DataPath);
        Settings.PrivateFolder(settings.StatePath);
        try {
            var initial = RuntimeInstaller.Install(settings, phase => Console.WriteLine("v0.16.0: " + phase));
            wait(initial);
            settings.RuntimePath = initial.Result;
            settings.Configured = true;
            var sessionFile = Path.Combine(root, "session");
            wait(Node(initial.Result, settings.DataPath, "--input-type=module", "-e", """
                import { initDb, db } from './server/db.ts';
                import { initKey } from './server/crypto.ts';
                import { createUser, setPassword, createSession } from './server/users.ts';
                import { writeFileSync } from 'node:fs';
                initDb();
                initKey();
                const owner = createUser('synthetic-repair-owner', 'admin');
                setPassword(owner.id, 'synthetic-repair-password');
                writeFileSync(process.argv[1], createSession(owner.id));
                db.close();
                """, sessionFile));
            File.Delete(Path.Combine(settings.StatePath, "runtime-install.json"));
            using var manager = new Manager(settings, value => Settings.Atomic(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(value)));
            using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri(settings.ControlUrl), Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Add("Cookie", "switchboard_session=" + File.ReadAllText(sessionFile));
            http.DefaultRequestHeaders.Add("Origin", settings.ControlUrl);

            async Task Ready(string? previousRuntime = null) {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                while (!manager.ServerStarted || manager.Settings.RuntimePath == previousRuntime) {
                    Require(manager.Error == null, manager.Error ?? "Desktop launch failed.");
                    await Task.Delay(100, timeout.Token);
                }
                using var response = await http.GetAsync("/readyz", timeout.Token);
                Require(response.IsSuccessStatusCode, "The selected runtime did not reach /readyz.");
            }
            string Version() => JsonDocument.Parse(File.ReadAllText(Path.Combine(manager.Settings.RuntimePath!, "runtime.json")))
                .RootElement.GetProperty("version").GetString()!;

            wait(manager.Start());
            wait(Ready());
            Require(Version() == "0.16.0", "Expected the released v0.16.0 runtime.");
            var window = new MainWindow(manager, new GithubTokenStore("Switchboard/Test/" + Guid.NewGuid()), false);
            window.Show();
            try {
                descendants(window).OfType<RadioButton>().Single(button => Equals(button.Content, "Advanced")).IsChecked = true;
                void Reinstall() {
                    window.Refresh();
                    window.UpdateLayout();
                    var button = descendants(window).OfType<Button>().Single(control => Equals(control.Content, "Reinstall Selected Release"));
                    Require(button.IsEnabled, "The WPF install action is unavailable.");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }

                var previous = manager.Settings.RuntimePath;
                manager.Settings.Pin = "v0.17.0";
                Reinstall();
                wait(Ready(previous));
                Require(Version() == "0.17.0", "The WPF install did not select released v0.17.0.");
                async Task<JsonElement> Policy() =>
                    JsonDocument.Parse(await http.GetStringAsync("/api/admin/activity-retention")).RootElement.Clone();
                var policy = Policy();
                wait(policy);
                var oldCapture = policy.Result.GetProperty("capture");
                Require(!oldCapture.GetProperty("available").GetBoolean() && oldCapture.GetProperty("reason").GetString() == "restore-unsupported",
                    "Expected the v0.17.0 preview warning.");
                async Task SaveRetention() {
                    using var response = await http.PutAsync("/api/admin/activity-retention", System.Net.Http.Json.JsonContent.Create(
                        new { hours = 9, revision = policy.Result.GetProperty("revision") }));
                    Require(response.IsSuccessStatusCode, "Could not save retention on v0.17.0.");
                }
                wait(SaveRetention());
                Console.WriteLine("PASS: WPF install downloaded v0.16.0 → v0.17.0 and started automatically with preview capture unavailable.");

                manager.RuntimeInstallation = (value, _, _) => {
                    using var controller = RuntimeInstaller.Lease(Path.Combine(value.StatePath, "controller-lease.db"));
                    using var server = RuntimeInstaller.Lease(Path.Combine(value.DataPath, ".server-lease.db"));
                    var pending = new Settings { DataPath = value.DataPath, StatePath = value.StatePath, RuntimePath = fixedRuntime };
                    PreviewLifecycle.RecoverSelection(value, pending);
                    return Task.FromResult(fixedRuntime);
                };
                previous = manager.Settings.RuntimePath;
                Reinstall();
                wait(Ready(previous));
                Require(manager.Settings.RuntimePath == fixedRuntime, "The repair did not select the fixed package.");
                var chosen = PreviewLifecycle.SelectedRuntime(manager.Settings);
                Require(chosen == fixedRuntime, "The supervisor pointer did not select the fixed package.");
                void Reveal(string phase) => wait(Node(fixedRuntime, settings.DataPath, browserScript, phase, settings.ControlUrl,
                    settings.StatePath, sessionFile, screenshots));
                Reveal("after-repair");

                wait(manager.Stop());
                Require(!manager.Running, "The repaired server failed to stop for restart.");
                wait(manager.Start());
                wait(Ready());
                Reveal("after-restart");
            } finally {
                window.Hide();
                wait(manager.Shutdown());
            }
        } finally {
            wait(Settings.RemoveFolder(root));
        }
    }
}
