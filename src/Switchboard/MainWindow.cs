using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Shapes;
using System.Windows.Media;
using Microsoft.Win32;
namespace Switchboard;
public sealed class MainWindow : Window {
    readonly Manager manager; readonly StackPanel body=new(){Margin=new Thickness(24)};
    readonly StackPanel header = new() { Margin=new Thickness(24,12,24,0) };
    readonly StackPanel footer = new() { Margin=new Thickness(24,0,24,12) };
    readonly System.Windows.Controls.RadioButton generalTab = new() { Content="General", IsChecked=true, Margin=new Thickness(0,0,16,0) };
    readonly System.Windows.Controls.RadioButton advancedTab = new() { Content="Advanced" };
    readonly ScrollViewer scroll;
    readonly Dictionary<string,string> edits = new();
    bool copyFolders;
    (string Data, string Updater, string? Runtime)? verifiedCopy;
    string? startupError;
    string? preferenceFeedback;
    string? accountFeedback;
    string? tokenError;
    string? advancedFeedback;
    string? advancedErrorField;
    bool pinPickerOpen;
    string? versionError;
    bool loadingVersions;
    bool advanced;
    string? announcedFeedback;
    string? announcedStatus;
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
        manager=model;
        this.tokenStore=tokenStore;
        this.appUpdatesAvailable=appUpdatesAvailable;
        Title="Switchboard"; Width=600; Height=760; MinWidth=460; MinHeight=500;
        var layout = new DockPanel();
        var navigation = new StackPanel { Orientation=System.Windows.Controls.Orientation.Horizontal, Margin=new Thickness(24,16,24,0) };
        navigation.Children.Add(generalTab);
        navigation.Children.Add(advancedTab);
        DockPanel.SetDock(navigation,Dock.Top);
        DockPanel.SetDock(header,Dock.Top);
        DockPanel.SetDock(footer,Dock.Bottom);
        layout.Children.Add(navigation);
        layout.Children.Add(header);
        layout.Children.Add(footer);
        scroll=new ScrollViewer { Content=body, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        layout.Children.Add(scroll);
        Content=layout;
        generalTab.Checked+=(_,_)=>SelectPane(false);
        advancedTab.Checked+=(_,_)=>SelectPane(true);
        PreviewKeyDown+=(_,e)=>{
            if (advanced && e.Key==Key.S && Keyboard.Modifiers==ModifierKeys.Control && CanSaveAdvanced) {
                e.Handled=true;
                SaveAdvanced();
            }
        };
        Activated+=(_,_)=>{if(!advanced)Refresh();};
        Loaded+=(_,_)=>generalTab.Focus();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged+=(_,_)=>Dispatcher.Invoke(Refresh);
        Closing+=(_,e)=>{e.Cancel=true;Hide();};
        Refresh();
    }

    void SelectPane(bool showAdvanced) {
        advanced=showAdvanced;
        scroll.ScrollToTop();
        Refresh(forceBody:true);
    }

    void Text(string value,bool heading=false) {body.Children.Add(new TextBlock{Foreground=Foreground,Text=value,TextWrapping=TextWrapping.Wrap,FontSize=heading?20:14,FontWeight=heading?FontWeights.SemiBold:FontWeights.Normal,Margin=new Thickness(0,heading?20:6,0,6)});}
    System.Windows.Controls.Button Button(string label,Func<Task> action,bool enabled=true,Panel? parent=null) {var b=new System.Windows.Controls.Button{Content=label,IsEnabled=enabled,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,5,0,5),Padding=new Thickness(12,6,12,6)};b.Click+=async(_,_)=>{try{await action();}catch(Exception e){System.Windows.MessageBox.Show(this,e.Message,"Switchboard");}Refresh();};(parent??body).Children.Add(b);return b;}
    void Toggle(string label,bool value,Action<Settings,bool> change,Panel? parent=null) {
        var check=new CheckBox { Content=label, IsChecked=value, Margin=new Thickness(0,8,0,8) };
        check.Click+=(_,_)=>PersistPreference(settings=>change(settings,check.IsChecked==true));
        (parent??body).Children.Add(check);
    }

    Settings CopySettings() => System.Text.Json.JsonSerializer.Deserialize<Settings>(
        System.Text.Json.JsonSerializer.Serialize(manager.Settings))!;

    void PersistPreference(Action<Settings> change) {
        var settings=CopySettings();
        change(settings);
        preferenceFeedback=null;
        try { manager.ApplySettings(settings); }
        catch (Exception e) { preferenceFeedback="Could not save the preference. "+e.Message; }
        Refresh(forceBody:true);
    }

    void StartupRegistration() {
        const string path=@"Software\Microsoft\Windows\CurrentVersion\Run";
        var check=new CheckBox { Content="Open Switchboard at sign-in", Margin=new Thickness(0,8,0,8) };
        try {
            using var key=Registry.CurrentUser.OpenSubKey(path);
            check.IsChecked=key?.GetValue("Switchboard")!=null;
        } catch (Exception e) {
            startupError="Could not read startup registration. "+e.Message;
            check.IsEnabled=false;
        }
        check.Click+=(_,_)=>{
            try {
                using var key=Registry.CurrentUser.CreateSubKey(path);
                if (check.IsChecked==true) key.SetValue("Switchboard","\""+Environment.ProcessPath+"\"");
                else key.DeleteValue("Switchboard",false);
                startupError=null;
            } catch (Exception e) { startupError="Could not change startup registration. "+e.Message; }
            Refresh(forceBody:true);
        };
        body.Children.Add(check);
        if (startupError != null) {
            LiveMessage(startupError,body);
            Button("Open Windows Startup Apps",()=>{
                Manager.OpenUrl("ms-settings:startupapps");
                return Task.CompletedTask;
            });
        }
    }

    public void Refresh() => Refresh(forceBody:false);

    void Refresh(bool forceBody) {
        Dispatcher.Invoke(()=>{

            var theme=System.Windows.Application.Current.ThemeMode;
            var dark=theme==System.Windows.ThemeMode.Dark || theme==System.Windows.ThemeMode.System && (int?)Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1)==0;
            Background=SystemParameters.HighContrast?SystemColors.WindowBrush:new SolidColorBrush(dark?Color.FromRgb(32,32,32):Color.FromRgb(250,250,250));
            Foreground=SystemParameters.HighContrast?SystemColors.WindowTextBrush:new SolidColorBrush(dark?Color.FromRgb(245,245,245):Color.FromRgb(26,26,26));
            scroll.Background=Background;
            var focusedServerAction=Keyboard.FocusedElement is System.Windows.Controls.Button previous && header.IsAncestorOf(previous) ? previous.Content : null;
            header.Children.Clear();
            ShowServerStatus(dark);
            var serverActions=new WrapPanel();
            Button(manager.ServerStarted?"Open Switchboard":manager.Settings.Configured?"Start Switchboard":"Set Up Switchboard",async()=>{
                if(manager.ServerStarted)manager.Open();else await StartServer();
            },!manager.Busy && (manager.ServerStarted || !manager.Running),serverActions).Margin=new Thickness(0,4,8,4);
            if(manager.Running)Button("Stop",async()=>{await manager.Stop();},!manager.Busy && (manager.Status!="Stopping…" || manager.Error!=null),serverActions);
            header.Children.Add(serverActions);
            if (focusedServerAction != null) {
                var enabled=serverActions.Children.OfType<System.Windows.Controls.Button>().Where(button=>button.IsEnabled).ToArray();
                var next=enabled.FirstOrDefault(button=>Equals(button.Content,focusedServerAction)) ?? enabled.FirstOrDefault();
                if (next!=null) next.Focus(); else (advanced?advancedTab:generalTab).Focus();
            }
            if (manager.Error != null) LiveMessage(manager.Error,header,announce:false);
            if (manager.Installation != null)
                header.Children.Add(new System.Windows.Controls.ProgressBar { IsIndeterminate=true, Height=4, Margin=new Thickness(0,4,0,4) });
            if (manager.CanCancelInstallation)
                Button("Cancel Installation",()=>{manager.Installation?.Cancel();return Task.CompletedTask;},true,header);
            if (!forceBody && !manager.Busy && !manager.Running &&
                Keyboard.FocusedElement is TextBox field && body.IsAncestorOf(field)) return;
            if (!forceBody && Keyboard.FocusedElement is PasswordBox entry && body.IsAncestorOf(entry)) return;
            body.Children.Clear();
            footer.Children.Clear();
            if(!advanced){
                if(!manager.Settings.Configured)Text("Set up Switchboard to download the server and create your account in your browser. The recommended settings are ready.");
                if (manager.ServerStarted) {
                    Text("Account and connections",true);
                    Text(manager.AccountStatus ?? "Account status unavailable");
                    if (manager.NeedsAccountSetup)
                        Button("Continue Account Setup…",ContinueSetup,!manager.Busy);
                    if (manager.Attention>0) {
                        Text(manager.Attention==1 ? "1 connection needs sign-in" : $"{manager.Attention} connections need sign-in");
                        Button($"Sign in to connections ({manager.Attention})",()=>{
                            manager.OpenConnections();
                            return Task.CompletedTask;
                        });
                    }
                }
                Text("Startup",true);
                Toggle("Start server when Switchboard opens",manager.Settings.StartServer,(settings,v)=>settings.StartServer=v);
                StartupRegistration();
                if (preferenceFeedback != null) LiveMessage(preferenceFeedback,body);
                UpdateSettings();
                TokenSettings(dark);
                if (manager.ServerStarted && manager.AccountReady)
                    Button("Recover Password…",Recover,!manager.Busy);
                else if (!manager.Running && manager.Settings.Configured)
                    Button("Start & Continue to Recovery…",StartRecovery,!manager.Busy);
                if (accountFeedback != null) LiveMessage(accountFeedback,body);
            }else{
                AdvancedSettings();
                VersionPinning();
                Text("Maintenance",true);if(manager.UpdateState is "queued" or "preparing")Button("Cancel Server Update",manager.CancelServerUpdate,manager.Running);if(manager.UpdateState.Length>0)Text(manager.UpdateState);Button("Recover Server Lock…",()=>{if(System.Windows.MessageBox.Show(this,"Archive leftover lock metadata after checking exclusive ownership? Data stays in place.","Recover server lock",MessageBoxButton.OKCancel)==MessageBoxResult.OK)Maintenance.RecoverLock(manager.Settings);return Task.CompletedTask;},!manager.Running&&!manager.Busy);Button("Show Logs and Backups",()=>{manager.ShowLogs();return Task.CompletedTask;});Button("Reinstall Selected Release",manager.InstallAndStart,!manager.Busy);
            }

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
        var status=new TextBlock { Text="Server · "+manager.ServerStatus, Foreground=Foreground, TextWrapping=TextWrapping.Wrap, FontSize=14 };
        AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);
        row.Children.Add(status);
        header.Children.Add(row);
        if (status.Text!=announcedStatus) {
            announcedStatus=status.Text;
            Announce(status);
        }
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
            Toggle("Check for app updates automatically",manager.Settings.AutomaticAppChecks,(settings,v)=>settings.AutomaticAppChecks=v,app);
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
            var selectedChannel=channel.SelectedIndex==1?"development":"stable";
            PersistPreference(settings=>settings.Channel=selectedChannel);
        };
        var channelRow = new StackPanel { Margin=new Thickness(0,8,0,0) };
        channelRow.Children.Add(new Label {
            Content="Channel", Target=channel, Padding=new Thickness(0,0,0,4),
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
            Button("Install Server Release…",async()=>{
                if(System.Windows.MessageBox.Show(this,"Install the selected server release? Your data is kept. Switchboard starts automatically after installation. If the server is running, it will be unavailable during installation.","Server update",MessageBoxButton.OKCancel)==MessageBoxResult.OK)
                    await manager.InstallAndStart();
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
                try { await Task.Run(tokenStore.Remove); tokenError=null; }
                catch (Exception e) { tokenError=e.Message; }
                finally { tokenSaving=false; }
            },!manager.Busy&&!tokenSaving);
            if (tokenError != null) LiveMessage(tokenError,body);
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
            try { await Task.Run(()=>tokenStore.Save(value)); tokenDraft=""; tokenError=null; }
            catch (Exception e) { tokenError=e.Message; }
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
        if (tokenError != null) LiveMessage(tokenError,body);
    }
    bool CanSaveAdvanced => !manager.Running && !manager.Busy && edits.Count>0;

    void AdvancedSettings() {
        Text("Folders and network",true);
        if (manager.Running || manager.Busy) Text("Stop Switchboard before changing folders or network settings.");
        var previous=manager.Settings;
        void Field(string title,string value) {
            var field=new TextBox {
                Text=edits.GetValueOrDefault(title,value), IsEnabled=!manager.Running&&!manager.Busy
            };
            var label=new Label { Content=title, Target=field, Padding=new Thickness(0,6,0,4) };
            body.Children.Add(label);
            body.Children.Add(field);
            AutomationProperties.SetLabeledBy(field,label);
            if (advancedErrorField==title && advancedFeedback!=null) {
                AutomationProperties.SetHelpText(field,advancedFeedback);
                LiveMessage(advancedFeedback,body,announce:false);
            }
            field.TextChanged+=(_,_)=>{
                if (field.Text==value) edits.Remove(title); else edits[title]=field.Text;
                UpdateSaveFooter();
            };
        }
        Field("Data folder",previous.DataPath);
        Field("Updater folder",previous.StatePath);
        var copy = new CheckBox {
            Content=new TextBlock { Text="Copy and verify moved folders (keeps the originals)", TextWrapping=TextWrapping.Wrap },
            IsChecked=copyFolders, IsEnabled=!manager.Running&&!manager.Busy, Margin=new Thickness(0,8,0,4)
        };
        copy.Checked+=(_,_)=>copyFolders=true;
        copy.Unchecked+=(_,_)=>copyFolders=false;
        body.Children.Add(copy);
        Field("Listen address",previous.Host);
        Field("Port",previous.Port.ToString());
        Field("Browser URL",previous.BrowserUrl);
        UpdateSaveFooter();
    }

    void UpdateSaveFooter() {
        footer.Children.Clear();
        if (advancedFeedback != null) {
            LiveMessage(advancedErrorField!=null?"Settings could not be saved.":advancedFeedback,footer,advancedFeedback!=announcedFeedback);
            announcedFeedback=advancedFeedback;
        }
        else if (edits.Count>0) LiveMessage("Unsaved changes",footer,announce:false);
        Button("Save Changes",()=>{ SaveAdvanced(); return Task.CompletedTask; },CanSaveAdvanced,footer);
    }

    bool SaveAdvanced() {
        if (!CanSaveAdvanced) return false;
        var draft=CopySettings();
        draft.DataPath=edits.GetValueOrDefault("Data folder",draft.DataPath);
        draft.StatePath=edits.GetValueOrDefault("Updater folder",draft.StatePath);
        draft.Host=edits.GetValueOrDefault("Listen address",draft.Host);
        draft.BrowserUrl=edits.GetValueOrDefault("Browser URL",draft.BrowserUrl);
        var parsed=int.TryParse(edits.GetValueOrDefault("Port",draft.Port.ToString()),out var port);
        draft.Port=parsed?port:0;
        announcedFeedback=null;
        advancedErrorField="Data folder";
        try {
            draft.ValidateFolders();
            advancedErrorField="Listen address"; draft.ValidateAddress();
            advancedErrorField="Port"; draft.ValidatePort();
            advancedErrorField="Browser URL"; draft.ValidateBrowserUrl();
            advancedErrorField=null;
            var target=(draft.DataPath,draft.StatePath);
            if (verifiedCopy is { } verified && (verified.Data,verified.Updater)==target) {
                draft.RuntimePath=verified.Runtime;
            } else if (copyFolders) {
                Maintenance.Relocate(manager.Settings,draft);
                verifiedCopy=(target.DataPath,target.StatePath,draft.RuntimePath);
            }
            manager.ApplySettings(draft);
        } catch (Exception e) {
            advancedFeedback=e.Message;
            if (verifiedCopy is { } copied && (copied.Data,copied.Updater)==(draft.DataPath,draft.StatePath) && advancedErrorField==null)
                advancedFeedback+=" The folders were copied. Retry with the same paths to save the settings without copying again.";
            SelectAdvancedAfterError();
            return false;
        }
        edits.Clear();
        verifiedCopy=null;
        advancedFeedback="Changes saved";
        Refresh(forceBody:true);
        return true;
    }

    void SelectAdvancedAfterError() {
        advancedTab.IsChecked=true;
        Refresh(forceBody:true);
        if (advancedErrorField!=null && body.Children.OfType<Label>().FirstOrDefault(l=>Equals(l.Content,advancedErrorField))?.Target is TextBox field) {
            field.BringIntoView();
            field.Focus();
        }
    }

    internal Task StartServer() {
        if (edits.Count>0) {
            if (!IsVisible) { Show(); Activate(); }
            var choice=new Window {
                Title="Unsaved settings", Owner=this, Background=Background, Foreground=Foreground,
                SizeToContent=SizeToContent.WidthAndHeight,
                WindowStartupLocation=WindowStartupLocation.CenterOwner, ResizeMode=ResizeMode.NoResize
            };
            var panel=new StackPanel { Margin=new Thickness(20) };
            panel.Children.Add(new TextBlock { Text="Save your settings changes before starting Switchboard?" });
            var actions=new WrapPanel { Margin=new Thickness(0,12,0,0) };
            panel.Children.Add(actions);
            bool start=false;
            Button("Save Changes",()=>{
                choice.Close();
                start=SaveAdvanced();
                return Task.CompletedTask;
            },CanSaveAdvanced,actions).Margin=new Thickness(0,4,8,4);
            Button("Use Saved Settings",()=>{
                start=true;
                choice.Close();
                return Task.CompletedTask;
            },true,actions).Margin=new Thickness(0,4,8,4);
            actions.Children.Add(new System.Windows.Controls.Button { Content="Cancel", IsCancel=true, Padding=new Thickness(12,6,12,6) });
            choice.Content=panel;
            choice.ShowDialog();
            if (!start) return Task.CompletedTask;
        }
        return manager.Start();
    }

    void VersionPinning() {
        Text("Version pinning",true);
        if (manager.Settings.Pin is string pin) Text($"Pinned release {pin}");
        if (!pinPickerOpen)
            Button("Choose a Release…",async()=>{
                pinPickerOpen=true;
                await LoadVersions();
            });
        else {
            var choices=new ComboBox {
                ItemsSource=versionTags.Distinct().ToArray(), SelectedItem=manager.Settings.Pin,
                IsEnabled=!loadingVersions && versionTags.Count>0
            };
            AutomationProperties.SetName(choices,"Pinned release");
            choices.SelectionChanged+=(_,_)=>{
                if (choices.SelectedItem is string tag) PersistPreference(settings=>settings.Pin=tag);
            };
            body.Children.Add(choices);
            Button(versionError!=null?"Retry Release Lookup":"Load Older Versions",LoadVersions,!loadingVersions);
        }
        if (manager.Settings.Pin!=null)
            Button("Clear Pin",()=>{ PersistPreference(settings=>settings.Pin=null); return Task.CompletedTask; });
        if (versionError!=null) LiveMessage(versionError,body);
        if (preferenceFeedback!=null) LiveMessage(preferenceFeedback,body);
    }

    async Task LoadVersions() {
        if (loadingVersions) return;
        loadingVersions=true;
        versionError=null;
        Refresh();
        try {
            var rows=await RuntimeInstaller.Releases(releasePage);
            versionTags.AddRange(rows.Where(r=>!r.GetProperty("draft").GetBoolean() &&
                r.GetProperty("assets").EnumerateArray().Any(a=>a.GetProperty("name").GetString()==$"runtime-win32-{RuntimeInstaller.Arch}.json"))
                .Select(r=>r.GetProperty("tag_name").GetString()!));
            releasePage++;
            if (rows.Length==0 || versionTags.Count==0) versionError="No compatible releases found on this page.";
        } catch (Exception e) { versionError=e.Message; }
        finally { loadingVersions=false; Refresh(); }
    }

    static void LiveMessage(string message,Panel panel,bool announce=true) {
        var text=new TextBlock { Text=message, TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,4,0,4) };
        AutomationProperties.SetLiveSetting(text,AutomationLiveSetting.Polite);
        panel.Children.Add(text);
        if (announce) Announce(text);
    }

    static void Announce(TextBlock text) {
        if (UIElementAutomationPeer.CreatePeerForElement(text) is AutomationPeer peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    async Task ContinueSetup() {
        accountFeedback=null;
        try {
            var result=await manager.Account("account-setup");
            if (result.TryGetProperty("setupUrl",out var url) && url.ValueKind==System.Text.Json.JsonValueKind.String)
                Manager.OpenUrl(url.GetString()!);
            else accountFeedback="A setup link is unavailable. Refresh account setup and try again.";
        } catch (Exception e) { accountFeedback=e.Message; }
    }

    async Task StartRecovery() {
        await StartServer();
        if (!manager.Running) return;
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check() { if (manager.AccountStatus!=null || !manager.Running || manager.Error!=null) ready.TrySetResult(); }
        manager.Changed+=Check;
        try {
            Check();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(95));
            if (manager.ServerStarted && manager.AccountReady) await Recover();
            else accountFeedback="Password recovery is available after the administrator account is ready.";
        } catch (TimeoutException) { accountFeedback="Switchboard did not become ready. Check the server status and try again."; }
        finally { manager.Changed-=Check; }
    }

    async Task Recover() {
        accountFeedback=null;
        var session=manager.Session;
        var window=new Window {
            Title="Recover a password", Width=420, SizeToContent=SizeToContent.Height, Owner=this,
            Background=Background, Foreground=Foreground,
            WindowStartupLocation=WindowStartupLocation.CenterOwner, ResizeMode=ResizeMode.NoResize
        };
        var panel=new StackPanel { Margin=new Thickness(24) };
        window.Content=panel;
        var pick=new ComboBox { IsEnabled=false, Margin=new Thickness(0,4,0,12) };
        var label=new Label { Content="Account", Target=pick };
        panel.Children.Add(new TextBlock { Text="Choose an account. Windows will ask you to verify your identity.", TextWrapping=TextWrapping.Wrap });
        panel.Children.Add(label);
        panel.Children.Add(pick);
        var feedback=new StackPanel();
        panel.Children.Add(feedback);
        void Status(string message) { feedback.Children.Clear(); LiveMessage(message,feedback); }
        Status("Loading accounts…");
        var actions=new WrapPanel();
        var cancel=new System.Windows.Controls.Button { Content="Cancel", IsCancel=true, Padding=new Thickness(12,6,12,6) };
        var authenticate=new System.Windows.Controls.Button {
            Content="Verify & Recover", IsEnabled=false, Margin=new Thickness(8,0,0,0), Padding=new Thickness(12,6,12,6)
        };
        actions.Children.Add(cancel); actions.Children.Add(authenticate);
        panel.Children.Add(actions);
        System.Text.Json.JsonElement[] users=[];
        pick.SelectionChanged+=(_,_)=>authenticate.IsEnabled=pick.SelectedIndex>=0;
        bool Current() => window.IsVisible && manager.Session==session && manager.ServerStarted;
        window.ContentRendered+=async(_,_)=>{
            cancel.Focus();
            try {
                var result=await manager.Account("account-users");
                if (!Current()) return;
                users=result.GetProperty("users").EnumerateArray().ToArray();
                if (users.Length==0) { Status("No accounts are eligible for password recovery."); return; }
                pick.ItemsSource=users.Select(u=>u.GetProperty("username").GetString()).ToArray();
                pick.IsEnabled=true;
                Status("Select an account to continue.");
                pick.Focus();
            } catch (Exception e) { if (Current()) Status(e.Message); }
        };
        authenticate.Click+=async(_,_)=>{
            if (!Current() || pick.SelectedIndex<0) return;
            authenticate.IsEnabled=false;
            pick.IsEnabled=false;
            try {
                var reply=await manager.RecoverAccount(users[pick.SelectedIndex].GetProperty("id").GetString()!,
                    new WindowInteropHelper(window).Handle,Current);
                if (!Current()) return;
                Manager.OpenUrl(reply.GetProperty("setupUrl").GetString()!);
                accountFeedback="Recovery opened in your browser.";
                window.Close();
            } catch (Exception e) {
                if (Current()) {
                    Status(e.Message);
                    pick.IsEnabled=true;
                    authenticate.IsEnabled=true;
                    authenticate.Focus();
                }
            }
        };
        window.ShowDialog();
        await Task.CompletedTask;
    }
}
