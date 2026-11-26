using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Windows.Media;
using Microsoft.Win32;
namespace Switchboard;
public sealed class MainWindow : Window {
    readonly Manager manager; readonly StackPanel body=new(){Margin=new Thickness(24)};
    bool advanced;
    readonly GithubTokenStore tokenStore;
    readonly bool appUpdatesAvailable;
    string? appUpdateResult;
    (string Channel, string? Pin, string Installed, string Selected)? serverRelease;
    bool checkingServerRelease;
    string tokenDraft="";
    bool tokenSaving;
    int releasePage=1;
    readonly List<string> versionTags=new();
    public MainWindow(Manager model) : this(model, new GithubTokenStore(), AppUpdates.CanCheck) {}
    internal MainWindow(Manager model, GithubTokenStore tokenStore, bool appUpdatesAvailable) {
        manager=model; this.tokenStore=tokenStore; this.appUpdatesAvailable=appUpdatesAvailable;Title="Switchboard";Width=600;Height=760;MinWidth=460;MinHeight=500;Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Microsoft.Win32.SystemEvents.UserPreferenceChanged+=(_,_)=>Dispatcher.Invoke(Refresh);Closing+=(_,e)=>{e.Cancel=true;Hide();};Refresh();}
    void Text(string value,bool heading=false) {body.Children.Add(new TextBlock{Foreground=Foreground,Text=value,TextWrapping=TextWrapping.Wrap,FontSize=heading?20:14,FontWeight=heading?FontWeights.SemiBold:FontWeights.Normal,Margin=new Thickness(0,heading?20:6,0,6)});}
    System.Windows.Controls.Button Button(string label,Func<Task> action,bool enabled=true,Panel? parent=null) {var b=new System.Windows.Controls.Button{Content=label,IsEnabled=enabled,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,5,0,5),Padding=new Thickness(12,6,12,6)};b.Click+=async(_,_)=>{try{await action();}catch(Exception e){System.Windows.MessageBox.Show(this,e.Message,"Switchboard");}Refresh();};(parent??body).Children.Add(b);return b;}
    void Toggle(string label,bool value,Action<bool> change,Panel? parent=null) {var c=new CheckBox{Content=label,IsChecked=value,Margin=new Thickness(0,8,0,8)};c.Click+=(_,_)=>{change(c.IsChecked==true);manager.Settings.Save();};(parent??body).Children.Add(c);}
    public void Refresh() {
        Dispatcher.Invoke(()=>{
            if (System.Windows.Input.Keyboard.FocusedElement is FrameworkElement focused && IsAncestorOf(focused) && focused is TextBox or PasswordBox) return;
            var theme=System.Windows.Application.Current.ThemeMode;
            var dark=theme==System.Windows.ThemeMode.Dark || theme==System.Windows.ThemeMode.System && (int?)Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1)==0;
            Background=SystemParameters.HighContrast?SystemColors.WindowBrush:new SolidColorBrush(dark?Color.FromRgb(32,32,32):Color.FromRgb(250,250,250));
            Foreground=SystemParameters.HighContrast?SystemColors.WindowTextBrush:new SolidColorBrush(dark?Color.FromRgb(245,245,245):Color.FromRgb(26,26,26));
            ((ScrollViewer)Content).Background=Background;
            body.Children.Clear();
            Text(advanced?"Advanced Settings":"Switchboard",true);
            ShowServerStatus(dark);
            if(manager.Error!=null) Text(manager.Error);
            if(manager.Installation!=null && manager.CanCancelInstallation) Button("Cancel Installation",()=>{manager.Installation.Cancel();return Task.CompletedTask;});
            if(!advanced){
                Button(manager.Running?"Open Switchboard":manager.Settings.Configured?"Start Switchboard":"Set Up Switchboard",async()=>{if(manager.Running)manager.Open();else await manager.Start();},!manager.Busy);
                if(manager.Running)Button("Stop",async()=>{await manager.Stop();},!manager.Busy);
                if(!manager.Settings.Configured)Text("Set up Switchboard to download the server and create your account in your browser. The recommended settings are ready.");
                if (manager.ServerStarted && manager.NeedsAccountSetup) {
                    Text("Create your administrator account",true);
                    Button("Continue Account Setup…",async()=>{
                        var result=await manager.Account("account-setup");
                        if(result.TryGetProperty("setupUrl",out var url) && url.ValueKind==System.Text.Json.JsonValueKind.String)
                            Manager.OpenUrl(url.GetString()!);
                    },!manager.Busy);
                }
                if(manager.Attention>0)Button($"Sign in to connections ({manager.Attention})",()=>{Manager.OpenUrl(manager.Settings.BrowserUrl.TrimEnd('/')+"/connections");return Task.CompletedTask;});
                Text("Startup",true);Toggle("Start Switchboard when the app opens",manager.Settings.StartServer,v=>manager.Settings.StartServer=v);
                using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                Toggle("Open app when I sign in to Windows",key?.GetValue("Switchboard")!=null,v=>{using var run=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");if(v)run.SetValue("Switchboard","\""+Environment.ProcessPath+"\"");else run.DeleteValue("Switchboard",false);});
                Button("Open Windows Startup Apps",()=>{Manager.OpenUrl("ms-settings:startupapps");return Task.CompletedTask;});
                UpdateSettings();
                TokenSettings(dark);
                if (manager.ServerStarted && manager.AccountReady)
                    Button("Recover Password…",Recover,!manager.Busy);
                else if (!manager.Running && manager.Settings.Configured)
                    Button("Start & Continue to Recovery…",async()=>{
                        await manager.Start();
                        if(manager.Running)await Recover();
                    },!manager.Busy);
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
                Text("Maintenance",true);if(manager.UpdateState is "queued" or "preparing")Button("Cancel Server Update",manager.CancelServerUpdate,manager.Running);if(manager.UpdateState.Length>0)Text(manager.UpdateState);Button("Recover Server Lock…",()=>{if(System.Windows.MessageBox.Show(this,"Archive leftover lock metadata after checking exclusive ownership? Data stays in place.","Recover server lock",MessageBoxButton.OKCancel)==MessageBoxResult.OK)Maintenance.RecoverLock(manager.Settings);return Task.CompletedTask;},!manager.Running&&!manager.Busy);Button("Show Logs and Backups",()=>{manager.ShowLogs();return Task.CompletedTask;});Button("Reinstall Selected Release",()=>manager.Work(manager.InstallCore),!manager.Busy);
            }
            Button(advanced?"Back to Settings":"Advanced…",()=>{advanced=!advanced;Refresh();return Task.CompletedTask;});Text("Closing this window keeps Switchboard running.");
        });
    }
    void ShowServerStatus(bool dark) {
        var row = new DockPanel { Margin = new Thickness(0,6,0,6) };
        var dot = new Ellipse {
            Width=9, Height=9, Margin=new Thickness(0,0,8,0),
            VerticalAlignment=VerticalAlignment.Center,
            Fill=SystemParameters.HighContrast ? SystemColors.WindowTextBrush :
                manager.ServerStarted ? new SolidColorBrush(dark ? Color.FromRgb(100,210,125) : Color.FromRgb(16,125,59)) :
                new SolidColorBrush(dark ? Color.FromRgb(160,160,160) : Color.FromRgb(105,105,105))
        };
        DockPanel.SetDock(dot,Dock.Left);
        row.Children.Add(dot);
        row.Children.Add(new TextBlock { Text=manager.Status, Foreground=Foreground, TextWrapping=TextWrapping.Wrap, FontSize=14 });
        body.Children.Add(row);
    }

    internal void ShowAppUpdateResult(string? candidate) {
        appUpdateResult = candidate == null ? "Up to date" : $"Available {candidate}";
        Refresh();
    }

    internal void ShowServerUpdateResult(string channel, string? pin, string installed, string selected) {
        serverRelease = (channel,pin,installed,selected);
        Refresh();
    }

    void UpdateSettings() {
        Text("Updates",true);
        var updates = new Grid();
        updates.ColumnDefinitions.Add(new ColumnDefinition());
        updates.ColumnDefinitions.Add(new ColumnDefinition());
        updates.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        updates.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        var app = new StackPanel();
        var server = new StackPanel();
        updates.Children.Add(app);
        updates.Children.Add(server);
        void ArrangeUpdatePanels(double width) {
            var sideBySide = width >= 500;
            updates.ColumnDefinitions[1].Width = sideBySide ? new GridLength(1,GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(server, sideBySide ? 1 : 0);
            Grid.SetRow(server, sideBySide ? 0 : 1);
            server.Margin = sideBySide ? new Thickness(12,0,0,0) : new Thickness(0,14,0,0);
        }
        ArrangeUpdatePanels(ActualWidth-64);
        updates.SizeChanged+=(_,e)=>{if(e.WidthChanged)ArrangeUpdatePanels(e.NewSize.Width);};
        body.Children.Add(updates);

        app.Children.Add(new TextBlock { Text="Windows app", FontSize=14, FontWeight=FontWeights.SemiBold });
        var appVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        VersionComparison(app, appVersion, appUpdateResult ?? (appUpdatesAvailable ? null : "Updates unavailable in this build"));
        if (appUpdatesAvailable) {
            Toggle("Check for app updates automatically",manager.Settings.AutomaticAppChecks,v=>manager.Settings.AutomaticAppChecks=v,app);
            Button("Check for App Updates…",()=>AppUpdates.Check(this,manager),!manager.Busy,app);
        }

        server.Children.Add(new TextBlock { Text="Server", FontSize=14, FontWeight=FontWeights.SemiBold });
        var installed = manager.RuntimeVersion;
        var selected = serverRelease is { } release && (release.Channel,release.Pin,release.Installed)==(manager.Settings.Channel,manager.Settings.Pin,installed)
            ? release.Selected : null;
        VersionComparison(server, installed, selected == null ? null : selected == installed ? "Up to date" : $"Selected release {selected}");

        var channel=new ComboBox {
            ItemsSource=new[]{"Stable (recommended)","Development"},
            SelectedIndex=manager.Settings.Channel=="development"?1:0,
            HorizontalAlignment=HorizontalAlignment.Left, VerticalAlignment=VerticalAlignment.Center,
            IsEnabled=manager.Settings.Pin==null && !manager.Busy && !checkingServerRelease
        };
        System.Windows.Automation.AutomationProperties.SetName(channel,"Server update channel");
        channel.SelectionChanged+=(_,_)=>{
            manager.Settings.Channel=channel.SelectedIndex==1?"development":"stable";
            manager.Settings.Save();
            serverRelease=null;
            Refresh();
        };
        var channelRow = new DockPanel { Margin=new Thickness(0,8,0,0) };
        channelRow.Children.Add(new Label {
            Content="Channel", Target=channel, Padding=new Thickness(0,0,8,0),
            VerticalAlignment=VerticalAlignment.Center
        });
        channelRow.Children.Add(channel);
        server.Children.Add(channelRow);
        Button(checkingServerRelease ? "Checking for Server Updates…" : "Check for Server Updates",async()=>{
            if (checkingServerRelease || manager.Busy) return;
            var selection = (manager.Settings.Channel,manager.Settings.Pin,installed);
            serverRelease=null;
            checkingServerRelease=true;
            Refresh();
            try {
                var tag=await RuntimeInstaller.SelectedTag(selection.Channel,selection.Pin);
                ShowServerUpdateResult(selection.Channel,selection.Pin,selection.installed,tag.TrimStart('v'));
            } finally { checkingServerRelease=false; }
        },!manager.Busy && !checkingServerRelease,server);
        if (selected != null && selected != installed)
            Button(manager.Running?"Stop & Install Server Release…":"Install Server Release…",async()=>{
                if(System.Windows.MessageBox.Show(this,"Install the selected server release? Your data is kept and the server will be left stopped.","Server update",MessageBoxButton.OKCancel)==MessageBoxResult.OK)
                    await manager.Work(manager.InstallCore);
            },!manager.Busy,server);
    }

    void VersionComparison(Panel panel, string? installed, string? result) {
        var row=new WrapPanel { Margin=new Thickness(0,0,0,4) };
        row.Children.Add(new TextBlock { Text=installed=="Not installed" ? installed : $"Installed {installed}", Margin=new Thickness(0,0,14,0) });
        if (result != null) row.Children.Add(new TextBlock { Text=result });
        panel.Children.Add(row);
    }

    void TokenSettings(bool dark) {
        Text("GitHub token for higher rate limits (optional)",true);
        bool stored;
        try { stored=tokenStore.Read()!=null; }
        catch(Exception e) { Text(e.Message); return; }
        if (stored) {
            Button("Remove Token",async()=>{
                if(tokenSaving)return;
                tokenSaving=true;
                try { await Task.Run(tokenStore.Remove); }
                finally { tokenSaving=false; }
            },!manager.Busy&&!tokenSaving);
            return;
        }
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        var box=new PasswordBox {
            Password=tokenDraft, IsEnabled=!manager.Busy&&!tokenSaving,
            MinWidth=0, Margin=new Thickness(0,5,8,5)
        };
        System.Windows.Automation.AutomationProperties.SetName(box,"GitHub token");
        box.PasswordChanged+=(_,_)=>tokenDraft=box.Password;
        row.Children.Add(box);
        var save=Button("Save Token",async()=>{
            if(tokenSaving)return;
            var value=tokenDraft;
            tokenSaving=true;
            box.IsEnabled=false;
            try { await Task.Run(()=>tokenStore.Save(value)); tokenDraft=""; }
            finally { tokenSaving=false; }
        },!manager.Busy&&!tokenSaving,row);
        Grid.SetColumn(save,1);
        body.Children.Add(row);

        var link = new Hyperlink(new Run("Create GitHub token…")) {
            NavigateUri=new Uri("https://github.com/settings/personal-access-tokens/new"),
            Focusable=true,
            Foreground=SystemParameters.HighContrast ? SystemColors.HotTrackBrush :
                new SolidColorBrush(dark ? Color.FromRgb(126,187,255) : Color.FromRgb(0,93,179))
        };
        link.RequestNavigate+=(_,e)=>{
            e.Handled=true;
            try { Manager.OpenUrl(e.Uri.AbsoluteUri); }
            catch(Exception error) { System.Windows.MessageBox.Show(this,error.Message,"Switchboard"); }
        };
        var linkLine = new TextBlock { FontSize=14, Margin=new Thickness(0,6,0,6) };
        linkLine.Inlines.Add(link);
        body.Children.Add(linkLine);
    }
    async Task LoadVersions(ComboBox box){try{
        box.IsEnabled=false;
        var rows=await RuntimeInstaller.Releases(releasePage);
        versionTags.AddRange(rows.Where(r=>!r.GetProperty("draft").GetBoolean()&&r.GetProperty("assets").EnumerateArray().Any(a=>a.GetProperty("name").GetString()==$"runtime-win32-{RuntimeInstaller.Arch}.json")).Select(r=>r.GetProperty("tag_name").GetString()!));
        box.ItemsSource=versionTags.Distinct().ToArray();box.SelectedItem=manager.Settings.Pin;
        box.SelectionChanged+=(_,_)=>{if(box.SelectedItem is string tag){manager.Settings.Pin=tag;manager.Settings.Save();}};
        if(box.SelectedIndex<0&&versionTags.Count>0)box.SelectedIndex=0;box.IsEnabled=true;
    }catch(Exception e){box.IsEnabled=false;Text(e.Message);}}

    async Task Recover(){
        var result=await manager.Account("account-users");var users=result.GetProperty("users").EnumerateArray().ToArray();
        var window=new Window{Title="Recover a password",Width=420,Height=240,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner};var panel=new StackPanel{Margin=new Thickness(24)};window.Content=panel;
        panel.Children.Add(new TextBlock{Text="Choose an account. Windows will ask you to verify your identity.",TextWrapping=TextWrapping.Wrap});
        var pick=new ComboBox{ItemsSource=users.Select(u=>u.GetProperty("username").GetString()).ToArray(),SelectedIndex=0,Margin=new Thickness(0,16,0,16)};panel.Children.Add(pick);
        var button=new System.Windows.Controls.Button{Content="Authenticate & Recover",IsEnabled=users.Length>0};panel.Children.Add(button);button.Click+=async(_,_)=>{button.IsEnabled=false;try{var reply=await manager.RecoverAccount(users[pick.SelectedIndex].GetProperty("id").GetString()!,new WindowInteropHelper(window).Handle,()=>window.IsVisible);Manager.OpenUrl(reply.GetProperty("setupUrl").GetString()!);window.Close();}catch(Exception e){System.Windows.MessageBox.Show(window,e.Message,"Password recovery");button.IsEnabled=true;}};window.ShowDialog();
    }
}
