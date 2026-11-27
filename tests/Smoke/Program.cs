using Switchboard;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Documents;
using System.IO;
using Forms=System.Windows.Forms;
class Program {
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static void Wait(Task task) {var frame=new System.Windows.Threading.DispatcherFrame();var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};timer.Tick+=(_,_)=>{if(task.IsCompleted)frame.Continue=false;};timer.Start();System.Windows.Threading.Dispatcher.PushFrame(frame);timer.Stop();task.GetAwaiter().GetResult();}
    static IEnumerable<DependencyObject> Descendants(DependencyObject parent) {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){
            var child=VisualTreeHelper.GetChild(parent,i);
            yield return child;
            foreach(var nested in Descendants(child))yield return nested;
        }
    }
    static bool HasText(Window window,string text) => Descendants(window).OfType<TextBlock>().Any(t=>t.Text==text);
    static bool HasButton(Window window,string label) => Descendants(window).OfType<Button>().Any(b=>Equals(b.Content,label));
    static void Capture(MainWindow window,string directory,string name,int width,bool scrollToBottom=false) {
        window.Width=width;
        window.Height=760;
        window.Refresh();
        window.UpdateLayout();
        var scroll=Descendants(window).OfType<ScrollViewer>().First();
        if(scrollToBottom)scroll.ScrollToBottom();else scroll.ScrollToTop();
        window.UpdateLayout();
        Render(window,directory,name);
    }
    static void Render(Window window,string directory,string name) {
        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder=new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=File.Create(Path.Combine(directory,name));
        encoder.Save(output);
    }
    static void ConnectionStatusChecks(string root,string core) {
        var runtime=Path.Combine(root,"AttentionRuntime");
        Directory.CreateDirectory(Path.Combine(runtime,"bin"));
        Directory.CreateDirectory(Path.Combine(runtime,"updater"));
        File.Copy(Path.Combine(core,"bin","node.exe"),Path.Combine(runtime,"bin","node.exe"));
        var scenario=Path.Combine(runtime,"attention.txt");
        File.WriteAllText(scenario,"2");
        File.WriteAllText(Path.Combine(runtime,"updater","desktop.ts"),"""
            import { readFileSync } from 'node:fs';
            import * as readline from 'node:readline';
            const lines = readline.createInterface({ input: process.stdin });
            const emit = value => console.log(JSON.stringify(value));
            lines.on('line', line => {
              switch (JSON.parse(line).action) {
                case 'start': emit({ event: 'started' }); break;
                case 'status': {
                  const value = readFileSync('attention.txt', 'utf8');
                  if (value === 'silent') break;
                  if (value === 'unknown') emit({ event: 'status' });
                  else emit({ event: 'status', ready: true, attention: Number(value) });
                  break;
                }
                case 'quit': emit({ event: 'stopped' }); process.exit(0);
              }
            });
            """);
        using var manager=new Manager(new Settings {
            RuntimePath=runtime, Configured=true,
            DataPath=Path.Combine(root,"AttentionData"), StatePath=Path.Combine(root,"AttentionState")
        });
        string? connectionUrl=null;
        manager.ConnectionsBrowserOpener=value=>connectionUrl=value;
        void Await(Func<bool> condition) => Wait(Task.Run(async()=>{
            var deadline=DateTime.UtcNow.AddSeconds(15);
            while(!condition()) {
                if(DateTime.UtcNow>deadline)throw new Exception("Connection status did not refresh");
                await Task.Delay(100);
            }
        }));
        Wait(manager.Start());
        Await(()=>manager.Attention==2);
        manager.OpenConnections();
        Require(connectionUrl=="http://127.0.0.1:8770/connections","Tray Connections action target is wrong");
        File.WriteAllText(scenario,"0");
        Await(()=>manager.Attention==0 && manager.AccountReady);
        File.WriteAllText(scenario,"1");
        Await(()=>manager.Attention==1);
        File.WriteAllText(scenario,"unknown");
        Await(()=>manager.Attention==0 && !manager.AccountReady);
        File.WriteAllText(scenario,"2");
        Await(()=>manager.Attention==2);
        File.WriteAllText(scenario,"silent");
        Await(()=>manager.Attention==0 && !manager.AccountReady);
        File.WriteAllText(scenario,"2");
        Await(()=>manager.Attention==2);
        var stop=manager.Stop();
        Require(manager.Attention==0,"Connection attention survived stop");
        Wait(stop);
    }

    static void TrayChecks(string directory) {
        using var source=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Switchboard;component/Assets/AppIcon.ico"))!.Stream;
        var invoked=0;
        using var tray=new TrayPresentation(new System.Drawing.Icon(source),()=>invoked++);
        var item=tray.Menu.Items[0];
        var normal=tray.CurrentIcon;
        Require(!item.Available,"Sign-in action visible without a ready count");
        tray.Update("Running on this PC",1);
        Require(item.Available && item.Text=="Sign in to connections (1)" &&
            tray.Tooltip=="Switchboard: 1 connection needs sign-in" && tray.CurrentIcon!=normal,"Singular tray badge state is incorrect");
        item.PerformClick();
        Require(invoked==1,"Tray action did not invoke its handler");
        tray.Update("Running on this PC",2);
        Require(item.Text=="Sign in to connections (2)" && tray.Tooltip=="Switchboard: 2 connections need sign-in",
            "Plural tray state is incorrect");
        tray.Menu.Items.Add("Settings…");
        tray.Menu.Items.Add("Open Switchboard");
        tray.Menu.Show(System.Drawing.Point.Empty);
        try {
            using var menu=new System.Drawing.Bitmap(tray.Menu.Width,tray.Menu.Height);
            tray.Menu.DrawToBitmap(menu,tray.Menu.ClientRectangle);
            menu.Save(Path.Combine(directory,"tray-sign-in-menu.png"));
            using var badge=tray.CurrentIcon.ToBitmap();
            badge.Save(Path.Combine(directory,"tray-sign-in-icon.png"));
        } finally { tray.Menu.Close(); }
        tray.Update("Not running",0);
        Require(!item.Available && tray.CurrentIcon==normal && tray.Tooltip=="Switchboard: Not running","Tray badge did not clear");
    }
    [STAThread] static int Main(string[] args) {
        using var deadline=new System.Threading.Timer(_=>Environment.FailFast("Smoke test deadline exceeded"),null,TimeSpan.FromMinutes(3),Timeout.InfiniteTimeSpan);
        Console.WriteLine("Starting ownership smoke checks");
        var root=Path.Combine(Path.GetTempPath(),"switchboard-windows-smoke-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try {
            var settings=new Settings{DataPath=Path.Combine(root,"Data"),StatePath=Path.Combine(root,"Updater")};settings.Validate();
            var original=settings.StatePath;settings.StatePath=settings.DataPath.ToUpperInvariant();try{settings.Validate();throw new Exception("Overlap accepted");}catch(Exception e){Require(e.Message.Contains("separate"),"Wrong overlap error");}settings.StatePath=original;
            settings.Port=80;try{settings.Validate();throw new Exception("Privileged port accepted");}catch(Exception e){Require(e.Message.Contains("port"),"Wrong port error");}settings.Port=8770;
            Settings.PrivateFolder(settings.DataPath);Settings.PrivateFolder(settings.StatePath);
            File.WriteAllText(Path.Combine(settings.DataPath,".server-lock"),"synthetic stale metadata");
            using(var lease=RuntimeInstaller.Lease(Path.Combine(settings.DataPath,".server-lease.db"))) {try{Maintenance.RecoverLock(settings);throw new Exception("Active lease ignored");}catch(Microsoft.Data.Sqlite.SqliteException){}}
            Maintenance.RecoverLock(settings);Require(!File.Exists(Path.Combine(settings.DataPath,".server-lock")),"Lock not archived");Require(Directory.GetFiles(Path.Combine(settings.StatePath,"recovered-locks")).Length==1,"Lock bytes not preserved");
            File.WriteAllText(Path.Combine(settings.DataPath,"synthetic.txt"),"synthetic data");
            var next=new Settings{DataPath=Path.Combine(root,"DataCopy"),StatePath=Path.Combine(root,"StateCopy")};Maintenance.Relocate(settings,next);Require(File.ReadAllText(Path.Combine(next.DataPath,"synthetic.txt"))=="synthetic data","Relocation content changed");Require(File.Exists(Path.Combine(settings.DataPath,"synthetic.txt")),"Original removed");
            Maintenance.CompleteRelocation(settings);
            var requests=0;
            foreach(var scenario in new[]{(Verified:false,Current:true),(Verified:true,Current:false)}) {
                try{RecoveryAuthorization.Run(()=>Task.FromResult(scenario.Verified),()=>scenario.Current,()=>{requests++;return Task.CompletedTask;}).GetAwaiter().GetResult();throw new Exception("Unsafe recovery accepted");}catch(Exception e){Require(e.Message.Contains("not authorized")||e.Message.Contains("changed"),"Wrong recovery result");}
            }
            Require(requests==0,"Recovery requested before fresh verification");
            using(var key=System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256)) {
                var feed=System.Text.Encoding.UTF8.GetBytes("synthetic update feed");var signature=key.SignData(feed,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);
                Require(AppUpdates.VerifyFeed(feed,signature,key.ExportSubjectPublicKeyInfoPem()),"Valid feed signature rejected");feed[0]^=1;Require(!AppUpdates.VerifyFeed(feed,signature,key.ExportSubjectPublicKeyInfoPem()),"Tampered feed accepted");
            }
            Console.WriteLine("Starting GitHub token checks");
            const string syntheticToken="synthetic-token-value";
            Require(GithubTokenStore.Validate("  "+syntheticToken+"\r\n")==syntheticToken,"Token trimming failed");
            foreach(var invalid in new[]{"", "two tokens", "injected\r\nheader",new string('a',2561)}) {
                try{GithubTokenStore.Validate(invalid);throw new Exception("Invalid token accepted");}catch(Exception e){Require(e.Message.StartsWith("Enter a GitHub token"),"Wrong token validation result");}
            }
            var store=new GithubTokenStore("Switchboard/Test/"+Guid.NewGuid());
            try {
                Require(store.Read()==null,"Isolated test credential already exists");store.Save(syntheticToken);
                Require(store.Read()==syntheticToken,"Credential reload failed");store.Save("replacement-token");
                Require(store.Read()=="replacement-token","Credential replacement failed");store.Remove();store.Remove();
                Require(store.Read()==null,"Credential removal failed");
            } finally {store.Remove();}
            using(var authenticated=GithubApi.Request(new Uri("https://api.github.com/repos/sample/public/releases"),syntheticToken))
                Require(authenticated.Headers.Authorization?.ToString()=="Bearer "+syntheticToken,"GitHub authorization missing");
            using(var anonymous=GithubApi.Request(new Uri("https://api.github.com/repos/sample/public/releases"),null))
                Require(anonymous.Headers.Authorization==null,"Anonymous request acquired credentials");
            foreach(var url in new[]{"http://api.github.com/x","https://github.com/x","https://api.github.com.example/x","https://api.github.com:8443/x","https://user@api.github.com/x"}) {
                try{using var invalid=GithubApi.Request(new Uri(url),syntheticToken);throw new Exception("Untrusted origin accepted");}catch(Exception e){Require(e.Message=="Untrusted GitHub API origin.","Wrong origin error");}
            }
            using(var unauthorized=new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized))
                Require(GithubApi.Error(unauthorized).Contains("Remove it under GitHub token"),"Missing token recovery advice");
            using(var limited=new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)) {
                limited.Headers.Add("x-ratelimit-remaining","0");limited.Headers.Add("retry-after","60");
                Require(GithubApi.Error(limited).Contains("60 seconds"),"Rate-limit retry timing missing");
                Require(GithubApi.Error(limited).Contains("below Updates to GitHub token"),"Rate-limit settings guidance missing");
            }
            using(var missing=new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound))
                Require(GithubApi.Error(missing).Contains("not found"),"Missing release error wrong");
            var tokenEnv=GithubApi.ServerEnvironment(settings,syntheticToken);
            Require(tokenEnv["SWITCHBOARD_PLUGIN_INSTALL_GITHUB_TOKEN"]==syntheticToken && !tokenEnv.ContainsKey("SWITCHBOARD_GITHUB_TOKEN"),"Server credential scope wrong");
            Require(!GithubApi.ServerEnvironment(settings,null).ContainsKey("SWITCHBOARD_PLUGIN_INSTALL_GITHUB_TOKEN"),"Removed token forwarded");
            Require(!System.Text.Json.JsonSerializer.Serialize(settings).Contains(syntheticToken),"Token leaked into settings");
            Console.WriteLine("Starting WPF render smoke checks");
            var app=new System.Windows.Application();app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml")});
            var directory=args.Length>0?args[0]:root;Directory.CreateDirectory(directory);
            settings.RuntimePath=Path.Combine(root,"VersionFixture");
            Directory.CreateDirectory(settings.RuntimePath);
            File.WriteAllText(Path.Combine(settings.RuntimePath,"runtime.json"),"""{"version":"2.3.0"}""");
            using var manager=new Manager(settings,_=>throw new IOException("Synthetic settings write failure."));
            var window=new MainWindow(manager,store,true);
            try {
                window.Show();
                window.UpdateLayout();
                Require(Descendants(window).OfType<PasswordBox>().Any(),"Masked token entry missing");
                var entry=Descendants(window).OfType<PasswordBox>().Single();
                entry.Password="synthetic-unsaved-draft";
                System.Windows.Input.Keyboard.ClearFocus();window.Refresh();window.UpdateLayout();
                Require(Descendants(window).OfType<PasswordBox>().Single().Password=="synthetic-unsaved-draft","Token draft lost during refresh");
                Descendants(window).OfType<PasswordBox>().Single().Clear();
                Require(HasText(window,"Server"),"Server update panel is missing");
                Require(HasText(window,"Windows app"),"App update panel is missing");

                window.ShowAppUpdateResult("2.4.0");
                window.ShowServerUpdateResult(settings.Channel,settings.Pin,manager.RuntimeVersion,"2.5.0");
                Require(HasText(window,"Available 2.4.0") && HasText(window,"Selected release 2.5.0"),"Candidate versions missing");
                foreach(var theme in new[]{ThemeMode.Light,ThemeMode.Dark}) foreach(var width in new[]{600,460}) {
                    app.ThemeMode=theme;
                    window.ThemeMode=theme;
                    Capture(window,directory,$"settings-updates-{theme}-{width}.png",width);
                    var blocks=Descendants(window).OfType<TextBlock>().ToArray();
                    var appLabel=blocks.Single(t=>t.Text=="Windows app").TranslatePoint(new Point(),window);
                    var serverLabel=blocks.Single(t=>t.Text=="Server").TranslatePoint(new Point(),window);
                    Require(width==600 ? serverLabel.X>appLabel.X && serverLabel.Y==appLabel.Y :
                        serverLabel.X==appLabel.X && serverLabel.Y>appLabel.Y,"Update panels are arranged incorrectly");
                    var channelLabel=Descendants(window).OfType<Label>().Single(l=>Equals(l.Content,"Channel"));
                    Require(channelLabel.Target is ComboBox combo &&
                        Math.Abs(channelLabel.TranslatePoint(new Point(),window).X -
                            combo.TranslatePoint(new Point(),window).X)<1,"Channel picker is misaligned");
                }

                app.ThemeMode=ThemeMode.Light;
                window.ThemeMode=ThemeMode.Light;
                foreach (var width in new[]{600,460}) {
                    Capture(window,directory,$"settings-token-entry-{width}.png",width,scrollToBottom:true);
                    var controls=Descendants(window).ToArray();
                    var field=controls.OfType<PasswordBox>().Single();
                    var save=controls.OfType<Button>().Single(button=>Equals(button.Content,"Save Token"));
                    var row=(Grid)field.Parent;
                    Require(save.Parent==row && Grid.GetColumn(save)==1 &&
                        field.TranslatePoint(new Point(field.ActualWidth,0),row).X < save.TranslatePoint(new Point(),row).X &&
                        save.TranslatePoint(new Point(save.ActualWidth,0),row).X <= row.ActualWidth &&
                        save.ActualWidth >= save.DesiredSize.Width,"Token save button does not fit beside the field");
                    var link=controls.OfType<TextBlock>().SelectMany(block=>block.Inlines).OfType<Hyperlink>().Single();
                    Require(link.Focusable && System.Windows.Input.KeyboardNavigation.GetIsTabStop(link) &&
                        link.NavigateUri.AbsoluteUri=="https://github.com/settings/personal-access-tokens/new","Token creation link is inaccessible");
                }
                using(var unconfigured=new Manager(new Settings { DataPath=settings.DataPath, StatePath=settings.StatePath })) {
                    var firstRun=new MainWindow(unconfigured,store,false);
                    firstRun.Show();
                    try {
                        Capture(firstRun,directory,"settings-first-setup.png",600);
                        Require(HasText(firstRun,"Server · Setup required") && HasText(firstRun,"Not installed") &&
                            !HasButton(firstRun,"Open Windows Startup Apps"),"First setup status or startup recovery visibility is incorrect");
                    } finally { firstRun.Hide(); window.Activate(); }
                }
                void Select(string label) {
                    Descendants(window).OfType<RadioButton>().Single(tab=>Equals(tab.Content,label)).IsChecked=true;
                    window.UpdateLayout();
                }
                TextBox Port() => (TextBox)Descendants(window).OfType<Label>().Single(label=>Equals(label.Content,"Port")).Target;
                void Save() => Descendants(window).OfType<Button>().Single(button=>Equals(button.Content,"Save Changes"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Select("Advanced");
                Port().Text="80";
                Select("General");
                window.Hide();
                window.Show();
                Select("Advanced");
                Require(Port().Text=="80","Advanced draft was lost after navigation and hide");
                Save();
                Capture(window,directory,"settings-advanced-validation.png",460);
                Require(HasText(window,"Enter a port from 1024 to 65535.") && Port().Text=="80","Port validation lost the draft or explanation");
                Port().Text="8771";
                Save();
                Capture(window,directory,"settings-advanced-save-error.png",600);
                Require(HasText(window,"Synthetic settings write failure.") && Port().Text=="8771" && manager.Settings.Port==8770,
                    "Failed save lost the draft or changed committed settings");
                Select("General");
                store.Save(syntheticToken);
                window.ShowAppUpdateResult(null);
                window.ShowServerUpdateResult(settings.Channel,settings.Pin,manager.RuntimeVersion,manager.RuntimeVersion);
                window.UpdateLayout();
                Require(Descendants(window).OfType<TextBlock>().Count(t=>t.Text=="Up to date")==2,"Confirmed update statuses missing");
                Require(HasButton(window,"Remove Token") && !HasButton(window,"Save Token") && !Descendants(window).OfType<TextBlock>().SelectMany(block=>block.Inlines).OfType<Hyperlink>().Any()
                    && !Descendants(window).OfType<PasswordBox>().Any(),"Stored token controls are incorrect");
                Capture(window,directory,"settings-token-stored.png",600,scrollToBottom:true);
            } finally { window.Hide(); store.Remove(); }
            Require(!window.IsVisible,"Closing settings failed");
            if(args.Length>1) {
                RelocationChecks.Run(Path.Combine(root,"RelocationRecovery"),Path.GetFullPath(args[1]));
                PreviewChecks.Run(Path.Combine(root,"PreviewLifecycle"),Path.GetFullPath(args[1]));
                Console.WriteLine("Starting native manager/core lifecycle checks");
                var socket=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);socket.Start();var port=((System.Net.IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
                var owned=new Settings{DataPath=Path.Combine(root,"OwnedData"),StatePath=Path.Combine(root,"OwnedState"),RuntimePath=Path.GetFullPath(args[1]),Configured=true,Port=port,BrowserUrl=$"http://127.0.0.1:{port}"};
                using var local=new Manager(owned);string? setup=null;local.AccountBrowserOpener=value=>setup=value;
                Wait(local.Start());
                Wait(Task.Run(async()=>{var until=DateTime.UtcNow.AddSeconds(90);while(!local.Status.StartsWith("Running")){if(local.Error!=null)throw new Exception(local.Error);if(DateTime.UtcNow>until)throw new Exception("Native startup timed out");await Task.Delay(100);}}));
                Require(setup?.Contains("/invite#")==true,"Initial setup link not received over private pipes");
                Require(local.NeedsAccountSetup,"The initial setup state was not detected");
                var setupWindow=new MainWindow(local,store,true);
                setupWindow.Show();
                try {
                    Capture(setupWindow,directory,"settings-running-first-admin.png",600);
                    Require(HasText(setupWindow,"Server · Running on this PC") && HasButton(setupWindow,"Continue Account Setup…"),"Running setup controls missing");
                    Require(!HasButton(setupWindow,"Recover Password…"),"Recovery appeared before account setup");
                } finally { setupWindow.Hide(); }
                using(var http=new System.Net.Http.HttpClient())
                using(var request=new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post,owned.BrowserUrl+"/api/auth/invite")) {
                    request.Headers.Add("Origin",owned.BrowserUrl);
                    request.Content=System.Net.Http.Json.JsonContent.Create(new {
                        token=new Uri(setup!).Fragment[1..],
                        username="synthetic-owner",
                        password="synthetic-original-password"
                    });
                    var response=http.SendAsync(request);
                    Wait(response);
                    using var accepted=response.Result;
                    Require(accepted.IsSuccessStatusCode,"Synthetic administrator creation failed");
                }
                Wait(Task.Run(async()=>{
                    var until=DateTime.UtcNow.AddSeconds(15);
                    while(!local.AccountReady){
                        if(DateTime.UtcNow>until)throw new Exception("Account status did not refresh");
                        await Task.Delay(100);
                    }
                }));
                var readyWindow=new MainWindow(local,store,true);
                readyWindow.Show();
                try {
                    Capture(readyWindow,directory,"settings-account-recovery.png",600,scrollToBottom:true);
                    Require(HasText(readyWindow,"Account ready") && !HasButton(readyWindow,"Continue Account Setup…") && HasButton(readyWindow,"Recover Password…"),
                        "Ready account controls are incorrect");
                    var captureDialog=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };
                    var dialogDeadline=DateTime.UtcNow.AddSeconds(10);
                    var captured=false;
                    captureDialog.Tick+=(_,_)=>{
                        var dialog=readyWindow.OwnedWindows.OfType<Window>().SingleOrDefault(w=>w.IsVisible);
                        if(dialog==null)return;
                        var accounts=Descendants(dialog).OfType<ComboBox>().SingleOrDefault();
                        if(accounts?.IsEnabled==true) {
                            Render(dialog,directory,"settings-password-recovery-dialog.png");
                            captured=true;
                        }
                        if(captured || DateTime.UtcNow>dialogDeadline)dialog.Close();
                    };
                    captureDialog.Start();
                    try {
                        Descendants(readyWindow).OfType<Button>().Single(b=>Equals(b.Content,"Recover Password…"))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    } finally { captureDialog.Stop(); }
                    Require(captured,"Password recovery dialog did not show the synthetic account");

                } finally { readyWindow.Hide(); }
                Wait(local.Stop());Require(!local.Running,"Owned server remained running");
                // Supply the prepared fixture at the download boundary; exercise the real
                // desktop installation completion and launch paths without a release upload.
                local.RuntimeInstallation = (s, progress, cancellation) => Task.FromResult(Path.GetFullPath(args[1]));
                Wait(local.InstallAndStart());
                Wait(Task.Run(async()=>{var until=DateTime.UtcNow.AddSeconds(30);while(!local.ServerStarted){if(local.Error!=null)throw new Exception(local.Error);if(DateTime.UtcNow>until)throw new Exception("Reinstallation did not start the server automatically");await Task.Delay(100);}}));
                Wait(local.Stop());
                Wait(local.Start());Wait(Task.Delay(1500));Wait(local.Stop());
                Wait(local.Start());Wait(Task.Delay(1500));
                var supervisor=local.OwnedSupervisorId;Require(supervisor!=null,"No owned supervisor");System.Diagnostics.Process.GetProcessById(supervisor!.Value).Kill();
                Wait(Task.Run(async()=>{var until=DateTime.UtcNow.AddSeconds(20);while(local.Status!="Not running"){if(DateTime.UtcNow>until)throw new Exception("Supervisor crash cleanup timed out");await Task.Delay(100);}}));
                Wait(Task.Run(async()=>{using var http=new System.Net.Http.HttpClient();var until=DateTime.UtcNow.AddSeconds(20);while(true){try{await http.GetAsync(owned.BrowserUrl+"/readyz");}catch(System.Net.Http.HttpRequestException){break;}if(DateTime.UtcNow>until)throw new Exception("Crash left an owned server running");await Task.Delay(100);}}));
            }
            TrayChecks(directory);
            if(args.Length>1) ConnectionStatusChecks(root,Path.GetFullPath(args[1]));
            Console.WriteLine("PASS: settings validation, exclusive lock recovery, verified relocation, WPF layout and screenshot render.");
            return 0;
        }finally{
            for(var attempt=0;;attempt++){try{Directory.Delete(root,true);break;}catch(IOException)when(attempt<20){Thread.Sleep(250);}}
        }
    }
}
