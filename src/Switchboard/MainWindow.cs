using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
namespace Switchboard;
public sealed class MainWindow : Window {
    readonly Manager manager; readonly StackPanel body=new(){Margin=new Thickness(24)};
    bool advanced;
    int releasePage=1;
    readonly List<string> versionTags=new();
    public MainWindow(Manager model) {manager=model;Title="Switchboard";Width=600;Height=760;MinWidth=460;MinHeight=500;Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Microsoft.Win32.SystemEvents.UserPreferenceChanged+=(_,_)=>Dispatcher.Invoke(Refresh);SizeChanged+=(_,_)=>Refresh();Closing+=(_,e)=>{e.Cancel=true;Hide();};Refresh();}
    void Text(string value,bool heading=false) {body.Children.Add(new TextBlock{Foreground=Foreground,Text=value,TextWrapping=TextWrapping.Wrap,FontSize=heading?20:14,FontWeight=heading?FontWeights.SemiBold:FontWeights.Normal,Margin=new Thickness(0,heading?20:6,0,6)});}
    void Button(string label,Func<Task> action,bool enabled=true) {var b=new System.Windows.Controls.Button{Content=label,IsEnabled=enabled,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,5,0,5),Padding=new Thickness(12,6,12,6)};b.Click+=async(_,_)=>{try{await action();}catch(Exception e){System.Windows.MessageBox.Show(this,e.Message,"Switchboard");}Refresh();};body.Children.Add(b);}
    void Toggle(string label,bool value,Action<bool> change) {var c=new CheckBox{Content=label,IsChecked=value,Margin=new Thickness(0,8,0,8)};c.Click+=(_,_)=>{change(c.IsChecked==true);manager.Settings.Save();};body.Children.Add(c);}
    public void Refresh() {
        Dispatcher.Invoke(()=>{
            if (System.Windows.Input.Keyboard.FocusedElement is TextBox) return;
            var theme=System.Windows.Application.Current.ThemeMode;
            var dark=theme==System.Windows.ThemeMode.Dark || theme==System.Windows.ThemeMode.System && (int?)Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1)==0;
            Background=SystemParameters.HighContrast?SystemColors.WindowBrush:new SolidColorBrush(dark?Color.FromRgb(32,32,32):Color.FromRgb(250,250,250));
            Foreground=SystemParameters.HighContrast?SystemColors.WindowTextBrush:new SolidColorBrush(dark?Color.FromRgb(245,245,245):Color.FromRgb(26,26,26));
            ((ScrollViewer)Content).Background=Background;
            body.Children.Clear();Text(advanced?"Advanced Settings":"Switchboard",true);Text(manager.Status);
            if(manager.Error!=null) Text(manager.Error);
            if(manager.Installation!=null && manager.CanCancelInstallation) Button("Cancel Installation",()=>{manager.Installation.Cancel();return Task.CompletedTask;});
            if(!advanced){
                Button(manager.Running?"Open Switchboard":manager.Settings.Configured?"Start Switchboard":"Set Up Switchboard",async()=>{if(manager.Running)manager.Open();else await manager.Start();},!manager.Busy);
                if(manager.Running)Button("Stop",async()=>{await manager.Stop();},!manager.Busy);
                if(!manager.Settings.Configured)Text("Set up Switchboard to download the server and create your account in your browser. The recommended settings are ready.");
                Text("Account",true);
                if(manager.Running){
                    Text(manager.AccountReady?"Your account is ready.":"Finish creating your account in your browser.");
                    if(!manager.AccountReady)Button("Continue Account Setup",async()=>{var r=await manager.Account("account-setup");if(r.TryGetProperty("setupUrl",out var u)&&u.ValueKind==System.Text.Json.JsonValueKind.String)Manager.OpenUrl(u.GetString()!);});
                    Button("Recover Password…",Recover,!manager.Busy);
                }else{
                    Text("Start Switchboard to access your account.");
                    if(manager.Settings.Configured)Button("Start & Continue to Recovery…",async()=>{await manager.Start();if(manager.Running)await Recover();},!manager.Busy);
                }
                if(manager.Attention>0)Button($"Sign in to connections ({manager.Attention})",()=>{Manager.OpenUrl(manager.Settings.BrowserUrl.TrimEnd('/')+"/connections");return Task.CompletedTask;});
                Text("Startup",true);Toggle("Start Switchboard when the app opens",manager.Settings.StartServer,v=>manager.Settings.StartServer=v);
                using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                Toggle("Open app when I sign in to Windows",key?.GetValue("Switchboard")!=null,v=>{using var run=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");if(v)run.SetValue("Switchboard","\""+Environment.ProcessPath+"\"");else run.DeleteValue("Switchboard",false);});
                Button("Open Windows Startup Apps",()=>{Manager.OpenUrl("ms-settings:startupapps");return Task.CompletedTask;});
                Text("Updates",true);
                var panels=new Grid();if(ActualWidth<560){panels.RowDefinitions.Add(new());panels.RowDefinitions.Add(new());}else{panels.ColumnDefinitions.Add(new());panels.ColumnDefinitions.Add(new());}body.Children.Add(panels);
                var app=new StackPanel{Margin=new Thickness(0,0,16,0)};app.Children.Add(new TextBlock{Text="Windows app",FontWeight=FontWeights.SemiBold});app.Children.Add(new TextBlock{Text="The tray app on this PC.",TextWrapping=TextWrapping.Wrap});app.Children.Add(new TextBlock{Text="Version: "+System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)});
                var automatic=new CheckBox{Content="Check automatically",IsChecked=manager.Settings.AutomaticAppChecks,Margin=new Thickness(0,10,0,0)};System.Windows.Automation.AutomationProperties.SetName(automatic,"Check for app updates automatically");automatic.Click+=(_,_)=>{manager.Settings.AutomaticAppChecks=automatic.IsChecked==true;manager.Settings.Save();};app.Children.Add(automatic);
                var check=new System.Windows.Controls.Button{Content="Check for app updates…",Margin=new Thickness(0,10,0,0)};check.Click+=async(_,_)=>await AppUpdates.Check(this,manager);app.Children.Add(check);panels.Children.Add(app);
                var server=new StackPanel();if(ActualWidth<560){Grid.SetRow(server,1);server.Margin=new Thickness(0,20,0,0);}else Grid.SetColumn(server,1);server.Children.Add(new TextBlock{Text="Server",FontWeight=FontWeights.SemiBold});server.Children.Add(new TextBlock{Text="Runs Switchboard in your browser.",TextWrapping=TextWrapping.Wrap});server.Children.Add(new TextBlock{Text="Version: "+manager.RuntimeVersion});
                var channel=new ComboBox{ItemsSource=new[]{"Stable (recommended)","Development"},SelectedIndex=manager.Settings.Channel=="development"?1:0,Margin=new Thickness(0,10,0,0),IsEnabled=manager.Settings.Pin==null};channel.SelectionChanged+=(_,_)=>{manager.Settings.Channel=channel.SelectedIndex==1?"development":"stable";manager.Settings.Save();};server.Children.Add(channel);
                var install=new System.Windows.Controls.Button{Content=manager.Running?"Stop & Update…":"Check / Install update…",Margin=new Thickness(0,10,0,0),IsEnabled=!manager.Busy};install.Click+=async(_,_)=>{if(System.Windows.MessageBox.Show(this,"Install the selected server release? Your data is kept and the server will be left stopped.","Server update",MessageBoxButton.OKCancel)==MessageBoxResult.OK)await manager.Work(manager.InstallCore);};server.Children.Add(install);panels.Children.Add(server);
            }else{
                Text("Folders and network",true);Text("Stop Switchboard before changing these settings.");
                var draft=System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(manager.Settings))!;
                var fields=new List<(TextBox Box,Action<string> Save)>();
                void Field(string title,string value,Action<string> save){Text(title);var box=new TextBox{Text=value,IsEnabled=!manager.Running&&!manager.Busy};System.Windows.Automation.AutomationProperties.SetName(box,title);body.Children.Add(box);fields.Add((box,save));}
                Field("Data folder",manager.Settings.DataPath,v=>draft.DataPath=v);Field("Updater folder",manager.Settings.StatePath,v=>draft.StatePath=v);Field("Listen address",manager.Settings.Host,v=>draft.Host=v);Field("Port",manager.Settings.Port.ToString(),v=>draft.Port=int.Parse(v));Field("Browser URL",manager.Settings.BrowserUrl,v=>draft.BrowserUrl=v);
                var copy=new CheckBox{Content="Copy and verify folders when moving data",Margin=new Thickness(0,10,0,10)};body.Children.Add(copy);
                Button("Save Changes",()=>{foreach(var f in fields)f.Save(f.Box.Text);draft.Validate();if(copy.IsChecked==true)Maintenance.Relocate(manager.Settings,draft);manager.ApplySettings(draft);return Task.CompletedTask;},!manager.Running&&!manager.Busy);
                Text("Version pinning",true);Toggle("Pin a specific version",manager.Settings.Pin!=null,v=>{manager.Settings.Pin=v?"choose-version":null;Refresh();});
                if(manager.Settings.Pin!=null){var choices=new ComboBox{MinWidth=200};body.Children.Add(choices);releasePage=1;versionTags.Clear();_=LoadVersions(choices);Button("Load Older Versions",async()=>{releasePage++;await LoadVersions(choices);});}
                Button("Open Windows Startup Apps",()=>{Manager.OpenUrl("ms-settings:startupapps");return Task.CompletedTask;});
                Text("Maintenance",true);Button("Recover Server Lock…",()=>{if(System.Windows.MessageBox.Show(this,"Archive leftover lock metadata after checking exclusive ownership? Data stays in place.","Recover server lock",MessageBoxButton.OKCancel)==MessageBoxResult.OK)Maintenance.RecoverLock(manager.Settings);return Task.CompletedTask;},!manager.Running&&!manager.Busy);Button("Show Logs and Backups",()=>{manager.ShowLogs();return Task.CompletedTask;});Button("Reinstall Selected Release",()=>manager.Work(manager.InstallCore),!manager.Busy);
            }
            Button(advanced?"Back to Settings":"Advanced…",()=>{advanced=!advanced;Refresh();return Task.CompletedTask;});Text("Closing this window keeps Switchboard running.");
        });
    }
    async Task LoadVersions(ComboBox box){try{
        box.IsEnabled=false;
        var rows=await RuntimeInstaller.Releases(releasePage);
        versionTags.AddRange(rows.Where(r=>!r.GetProperty("draft").GetBoolean()&&r.GetProperty("assets").EnumerateArray().Any(a=>a.GetProperty("name").GetString()==$"runtime-win32-{RuntimeInstaller.Arch}.json")).Select(r=>r.GetProperty("tag_name").GetString()!));
        box.ItemsSource=versionTags.Distinct().ToArray();box.SelectedItem=manager.Settings.Pin;
        box.SelectionChanged+=(_,_)=>{if(box.SelectedItem is string tag){manager.Settings.Pin=tag;manager.Settings.Save();}};
        if(box.SelectedIndex<0&&versionTags.Count>0)box.SelectedIndex=0;box.IsEnabled=true;
    }catch{box.IsEnabled=false;}}

    async Task Recover(){
        var result=await manager.Account("account-users");var users=result.GetProperty("users").EnumerateArray().ToArray();
        var window=new Window{Title="Recover a password",Width=420,Height=240,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner};var panel=new StackPanel{Margin=new Thickness(24)};window.Content=panel;
        panel.Children.Add(new TextBlock{Text="Choose an account. Windows will ask you to verify your identity.",TextWrapping=TextWrapping.Wrap});
        var pick=new ComboBox{ItemsSource=users.Select(u=>u.GetProperty("username").GetString()).ToArray(),SelectedIndex=0,Margin=new Thickness(0,16,0,16)};panel.Children.Add(pick);
        var button=new System.Windows.Controls.Button{Content="Authenticate & Recover",IsEnabled=users.Length>0};panel.Children.Add(button);button.Click+=async(_,_)=>{button.IsEnabled=false;try{var reply=await manager.RecoverAccount(users[pick.SelectedIndex].GetProperty("id").GetString()!,new WindowInteropHelper(window).Handle,()=>window.IsVisible);Manager.OpenUrl(reply.GetProperty("setupUrl").GetString()!);window.Close();}catch(Exception e){System.Windows.MessageBox.Show(window,e.Message,"Password recovery");button.IsEnabled=true;}};window.ShowDialog();
    }
}
