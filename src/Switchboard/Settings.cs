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
        var data = Canonical(DataPath).TrimEnd('\\'); var state = Canonical(StatePath).TrimEnd('\\');
        if (!Path.IsPathFullyQualified(DataPath) || !Path.IsPathFullyQualified(StatePath) || data.StartsWith("\\\\") || state.StartsWith("\\\\") || data.Equals(state, StringComparison.OrdinalIgnoreCase) || data.StartsWith(state + "\\", StringComparison.OrdinalIgnoreCase) || state.StartsWith(data + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Choose separate, absolute local data and updater folders.");
        foreach(var folder in new[]{data,state}) { if(Path.GetPathRoot(folder)?.TrimEnd('\\').Equals(folder.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)==true || new DriveInfo(Path.GetPathRoot(folder)!).DriveFormat!="NTFS")throw new Exception("Choose user-owned folders on a local NTFS drive, not a drive root."); }
        if(RuntimePath!=null){var runtime=Canonical(RuntimePath).TrimEnd('\\');if(data.Equals(runtime,StringComparison.OrdinalIgnoreCase)||data.StartsWith(runtime+"\\",StringComparison.OrdinalIgnoreCase)||runtime.StartsWith(data+"\\",StringComparison.OrdinalIgnoreCase)||state.Equals(runtime,StringComparison.OrdinalIgnoreCase)||state.StartsWith(runtime+"\\",StringComparison.OrdinalIgnoreCase))throw new Exception("Runtime, data and updater folders overlap.");}
        if (Port < 1024 || Port > 65535 || Host != "localhost" && !System.Net.IPAddress.TryParse(Host, out _)) throw new Exception("Choose a local IP address and a port from 1024 to 65535.");
        if (!Uri.TryCreate(BrowserUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0) throw new Exception("Enter an HTTP or HTTPS browser URL without credentials.");
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)] static extern bool MoveFileEx(string source,string target,int flags);
    public static void Atomic(string file,string text) {
        var temporary=file+"."+Guid.NewGuid()+".tmp";
        try{using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){var bytes=System.Text.Encoding.UTF8.GetBytes(text);stream.Write(bytes);stream.Flush(true);}if(!MoveFileEx(temporary,file,9))throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());}finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public void Save() { Validate(); PrivateFolder(Root); Atomic(Path.Combine(Root,"settings.json"),JsonSerializer.Serialize(this)); }

    public static string Canonical(string value) {
        var absolute=Path.GetFullPath(value);
        if(absolute.TrimEnd('\\').Equals(Path.GetPathRoot(absolute)!.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return Path.GetPathRoot(absolute)!;
        var full=absolute.TrimEnd('\\');
        if(!Directory.Exists(full)) { var parent=Path.GetDirectoryName(full); return parent==null?full:Path.Combine(Canonical(parent),Path.GetFileName(full)); }
        var info=new DirectoryInfo(full); return (info.ResolveLinkTarget(true)?.FullName ?? info.FullName).TrimEnd('\\');
    }
    public static Settings Load() { var file = Path.Combine(Root, "settings.json"); return File.Exists(file) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(file))! : new(); }
}
