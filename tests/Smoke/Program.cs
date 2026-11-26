using Switchboard;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.IO;
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
        var scroll=(ScrollViewer)window.Content;
        if(scrollToBottom)scroll.ScrollToBottom();else scroll.ScrollToTop();
        window.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder=new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=File.Create(Path.Combine(directory,name));
        encoder.Save(output);
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
            using var manager=new Manager(settings);
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
                }

                app.ThemeMode=ThemeMode.Light;
                window.ThemeMode=ThemeMode.Light;
                Capture(window,directory,"settings-token-entry.png",600,scrollToBottom:true);
                store.Save(syntheticToken);
                window.ShowAppUpdateResult(null);
                window.ShowServerUpdateResult(settings.Channel,settings.Pin,manager.RuntimeVersion,manager.RuntimeVersion);
                window.UpdateLayout();
                Require(Descendants(window).OfType<TextBlock>().Count(t=>t.Text=="Up to date")==2,"Confirmed update statuses missing");
                Require(HasButton(window,"Remove Token") && !HasButton(window,"Save Token") && !HasButton(window,"Create a GitHub Token…")
                    && !Descendants(window).OfType<PasswordBox>().Any(),"Stored token controls are incorrect");
                Capture(window,directory,"settings-token-stored.png",600,scrollToBottom:true);
            } finally { window.Hide(); store.Remove(); }
            Require(!window.IsVisible,"Closing settings failed");
            if(args.Length>1) {
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
                    Require(HasText(setupWindow,"Running on this PC") && HasButton(setupWindow,"Continue Account Setup…"),"Running setup controls missing");
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
                    Require(!HasButton(readyWindow,"Continue Account Setup…") && HasButton(readyWindow,"Recover Password…"),
                        "Ready account controls are incorrect");
                } finally { readyWindow.Hide(); }
                Wait(local.Stop());Require(!local.Running,"Owned server remained running");
                Wait(local.Start());Wait(Task.Delay(1500));Wait(local.Stop());
                Wait(local.Start());Wait(Task.Delay(1500));
                var supervisor=local.OwnedSupervisorId;Require(supervisor!=null,"No owned supervisor");System.Diagnostics.Process.GetProcessById(supervisor!.Value).Kill();
                Wait(Task.Run(async()=>{var until=DateTime.UtcNow.AddSeconds(20);while(local.Status!="Not running"){if(DateTime.UtcNow>until)throw new Exception("Supervisor crash cleanup timed out");await Task.Delay(100);}}));
                Wait(Task.Run(async()=>{using var http=new System.Net.Http.HttpClient();var until=DateTime.UtcNow.AddSeconds(20);while(true){try{await http.GetAsync(owned.BrowserUrl+"/readyz");}catch(System.Net.Http.HttpRequestException){break;}if(DateTime.UtcNow>until)throw new Exception("Crash left an owned server running");await Task.Delay(100);}}));
            }
            Console.WriteLine("PASS: settings validation, exclusive lock recovery, verified relocation, WPF layout and screenshot render.");
            return 0;
        }finally{
            for(var attempt=0;;attempt++){try{Directory.Delete(root,true);break;}catch(IOException)when(attempt<20){Thread.Sleep(250);}}
        }
    }
}
