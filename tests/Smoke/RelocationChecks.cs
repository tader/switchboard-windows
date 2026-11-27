using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Switchboard;

static class RelocationChecks {
    static void Require(bool ok,string message) { if(!ok)throw new Exception(message); }
    static string Epoch(Settings s) => JsonDocument.Parse(File.ReadAllText(Path.Combine(s.StatePath,"activity-preview-control.json"))).RootElement.GetProperty("epoch").GetString()!;
    static IEnumerable<DependencyObject> Descendants(DependencyObject parent) {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
            var child=VisualTreeHelper.GetChild(parent,i);yield return child;
            foreach(var nested in Descendants(child))yield return nested;
        }
    }
    static Settings Clone(Settings s) => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s))!;
    static string Feedback(Window window) => string.Join(" | ",Descendants(window).OfType<TextBlock>().Select(t=>t.Text));
    static Settings Fixture(string folder,string runtime) {
        var s=new Settings{DataPath=Path.Combine(folder,"Data"),StatePath=Path.Combine(folder,"State"),RuntimePath=runtime,Configured=true};
        PreviewChecks.Seed(s,runtime);File.WriteAllText(Path.Combine(s.DataPath,"synthetic.txt"),"preserved");return s;
    }
    static void GuiSave(MainWindow window,Settings next,bool copy) {
        Descendants(window).OfType<RadioButton>().Single(b=>Equals(b.Content,"Advanced")).IsChecked=true;window.UpdateLayout();
        foreach(var field in new[]{("Data folder",next.DataPath),("Updater folder",next.StatePath)})
            ((TextBox)Descendants(window).OfType<Label>().Single(l=>Equals(l.Content,field.Item1)).Target).Text=field.Item2;
        Descendants(window).OfType<CheckBox>().Single(c=>c.Content is TextBlock text&&text.Text.StartsWith("Copy and verify")).IsChecked=copy;
        Descendants(window).OfType<Button>().Single(b=>Equals(b.Content,"Save Changes")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
    }
    public static void Run(string root,string runtime) {
        foreach(var move in new[]{"data","state","both"}) {
            var folder=Path.Combine(root,"gui-"+move);var source=Fixture(folder,runtime);var before=Epoch(source);
            var file=Path.Combine(folder,"settings.json");Settings.Atomic(file,JsonSerializer.Serialize(source));
            using var manager=new Manager(source,s=>Settings.Atomic(file,JsonSerializer.Serialize(s)));
            var next=Clone(source);if(move!="state")next.DataPath=Path.Combine(folder,"NewData");if(move!="data")next.StatePath=Path.Combine(folder,"NewState");
            var window=new MainWindow(manager,new GithubTokenStore("Switchboard/Test/"+Guid.NewGuid()),false);window.Show();
            try {
                GuiSave(window,next,false);
                Require(Maintenance.Same(manager.Settings.DataPath,source.DataPath)&&Maintenance.Same(manager.Settings.StatePath,source.StatePath),"Unchecked GUI folder switch bypassed the guard");
                Require(Epoch(source)==before,"Rejected unchecked switch retired previews");
                Require(Descendants(window).OfType<TextBlock>().Any(t=>t.Text.Contains("Choose Copy and verify")),"Unchecked switch did not explain recovery");
                GuiSave(window,next,true);
                Require(Maintenance.Same(manager.Settings.DataPath,next.DataPath)&&Maintenance.Same(manager.Settings.StatePath,next.StatePath),"Checked GUI folder move was not saved: "+Feedback(window));
                Require(Epoch(source)!=before,"GUI move did not advance source epoch");
                Require(!Directory.EnumerateFiles(Settings.Canonical(source.StatePath)+".activity-previews","*.enc").Any(),"GUI move left old previews");
                Require(!File.Exists(Path.Combine(source.StatePath,Maintenance.RelocationFile)),"Committed GUI move left a pending journal");
            } finally { window.Hide(); }
        }
        foreach(var boundary in new[]{"partial-copy","verified","data-published","before-settings","settings-write"}) {
            var folder=Path.Combine(root,boundary);var source=Fixture(folder,runtime);var before=Epoch(source);
            var next=Clone(source);next.DataPath=Path.Combine(folder,"NewData");next.StatePath=Path.Combine(folder,"NewState");
            var file=Path.Combine(folder,"settings.json");Settings.Atomic(file,JsonSerializer.Serialize(source));
            var copy=Maintenance.CopyRelocation;string? partial=null;
            try {
                if(boundary=="partial-copy")Maintenance.CopyRelocation=(from,to)=>{Settings.PrivateFolder(to);partial=to;File.WriteAllText(Path.Combine(to,"sentinel"),"unrelated");throw new IOException("Synthetic interruption");};
                else Maintenance.RelocationCheckpoint=phase=>{if(phase==boundary)throw new IOException("Synthetic interruption");};
                using var manager=new Manager(source,s=>{if(boundary=="settings-write")throw new IOException("Synthetic interruption");Settings.Atomic(file,JsonSerializer.Serialize(s));});
                try { manager.ApplySettings(next,true);throw new Exception("Interruption fixture was ignored"); }
                catch(IOException e) { Require(e.Message=="Synthetic interruption","Unexpected copy failure"); }
            } finally { Maintenance.CopyRelocation=copy;Maintenance.RelocationCheckpoint=null; }
            var retiredEpoch=Epoch(source);Require(retiredEpoch!=before,"Interrupted move restored the source epoch");
            Require(File.Exists(Path.Combine(source.StatePath,Maintenance.RelocationFile)),"Interrupted move lost its receipt");
            var saved=JsonSerializer.Deserialize<Settings>(File.ReadAllText(file))!;
            using var resumed=new Manager(saved,s=>Settings.Atomic(file,JsonSerializer.Serialize(s)));
            resumed.Start().GetAwaiter().GetResult();Require(!resumed.Running&&resumed.Error?.Contains("pending")==true,"Pending source installation was allowed to restart");
            if(boundary=="before-settings") {
                File.WriteAllText(Path.Combine(next.DataPath,"synthetic.txt"),"changed destination");
                try { resumed.ApplySettings(Clone(next),true);throw new Exception("Modified retained destination adopted"); }
                catch(Exception e) { Require(e.Message.Contains("retained relocation"),"Unexpected destination validation failure"); }
                File.WriteAllText(Path.Combine(next.DataPath,"synthetic.txt"),"preserved");
            }
            var resumedWindow=new MainWindow(resumed,new GithubTokenStore("Switchboard/Test/"+Guid.NewGuid()),false);resumedWindow.Show();
            try {
                foreach(var field in new[]{("Data folder",next.DataPath),("Updater folder",next.StatePath)})
                    Require(((TextBox)Descendants(resumedWindow).OfType<Label>().Single(l=>Equals(l.Content,field.Item1)).Target).Text==field.Item2,"Restart did not restore the pending destination draft");
                Descendants(resumedWindow).OfType<Button>().Single(b=>Equals(b.Content,"Save Changes")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(Maintenance.Same(resumed.Settings.DataPath,next.DataPath)&&Maintenance.Same(resumed.Settings.StatePath,next.StatePath),"Restarted GUI Save did not resume the move: "+Feedback(resumedWindow));
            } finally { resumedWindow.Hide(); }
            Require(Epoch(source)==retiredEpoch,"Retry changed or restored the retired source epoch");
            Require(File.ReadAllText(Path.Combine(next.DataPath,"synthetic.txt"))=="preserved","Resumed move lost data");
            Require(!File.Exists(Path.Combine(source.StatePath,Maintenance.RelocationFile)),"Resumed settings commit left a pending receipt");
            if(partial!=null)Require(File.ReadAllText(Path.Combine(partial,"sentinel"))=="unrelated","Retry removed an unrelated staged entry");
        }
        Console.WriteLine("PASS: actual Advanced Save guards unchecked/checked data/state/combined moves; restart resumes partial copy, verified stages, partial publication and failed settings commit without restoring source epoch");
    }
}
