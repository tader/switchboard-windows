using Switchboard;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.IO;
class Program {
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    [STAThread] static int Main(string[] args) {
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
            var app=new System.Windows.Application();app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml")});
            using var manager=new Manager();var window=new MainWindow(manager);window.Show();window.UpdateLayout();
            var directory=args.Length>0?args[0]:root;Directory.CreateDirectory(directory);
            foreach(var theme in new[]{ThemeMode.Light,ThemeMode.Dark}) foreach(var width in new[]{600,460}) {
                app.ThemeMode=theme;
                window.Width=width;window.Height=800;window.UpdateLayout();
                var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(directory,"settings-"+theme+"-"+width+".png"));encoder.Save(output);
            }
            window.Hide();Require(!window.IsVisible,"Closing settings failed");
            Console.WriteLine("PASS: settings validation, exclusive lock recovery, verified relocation, WPF layout and screenshot render.");
            return 0;
        }finally{Directory.Delete(root,true);}
    }
}
