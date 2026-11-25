using System.IO;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
namespace Switchboard;
public sealed class Settings {
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switchboard");
    public string DataPath { get; set; } = Path.Combine(Root, "Data");
    public string StatePath { get; set; } = Path.Combine(Root, "Updater");
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8770;
    public string BrowserUrl { get; set; } = "http://127.0.0.1:8770";
    public bool StartServer { get; set; } = true;
    public bool Configured { get; set; }
    public string? RuntimePath { get; set; }
    public string Channel { get; set; } = "stable";
    public string? Pin { get; set; }
    public bool AutomaticAppChecks { get; set; } = true;
    public string ControlUrl => $"http://{(Host == "0.0.0.0" ? "127.0.0.1" : Host == "::" ? "[::1]" : Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}";
    public static void PrivateFolder(string path) {
        Directory.CreateDirectory(path);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    public void Validate() {
        var data = Path.GetFullPath(DataPath).TrimEnd('\\'); var state = Path.GetFullPath(StatePath).TrimEnd('\\');
        if (!Path.IsPathFullyQualified(DataPath) || !Path.IsPathFullyQualified(StatePath) || data.StartsWith("\\\\") || state.StartsWith("\\\\") || data.Equals(state, StringComparison.OrdinalIgnoreCase) || data.StartsWith(state + "\\", StringComparison.OrdinalIgnoreCase) || state.StartsWith(data + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Choose separate, absolute local data and updater folders.");
        if (Port < 1024 || Port > 65535 || Host != "localhost" && !System.Net.IPAddress.TryParse(Host, out _)) throw new Exception("Choose a local IP address and a port from 1024 to 65535.");
        if (!Uri.TryCreate(BrowserUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0) throw new Exception("Enter an HTTP or HTTPS browser URL without credentials.");
    }
    public void Save() { Validate(); PrivateFolder(Root); var file = Path.Combine(Root, "settings.json"); File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this)); File.Move(file + ".tmp", file, true); }
    public static Settings Load() { var file = Path.Combine(Root, "settings.json"); return File.Exists(file) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(file))! : new(); }
}
