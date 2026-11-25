using System.IO;
using System.Text.Json.Nodes;
namespace Switchboard;
public static class Maintenance {
    public static void RecoverLock(Settings settings) {
        settings.Validate(); Settings.PrivateFolder(settings.StatePath); Settings.PrivateFolder(settings.DataPath);
        using var controller = RuntimeInstaller.Lease(Path.Combine(settings.StatePath,"controller-lease.db"));
        using var server = RuntimeInstaller.Lease(Path.Combine(settings.DataPath,".server-lease.db"));
        var file=Path.Combine(settings.DataPath,".server-lock"); if (!File.Exists(file)) return;
        if(File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new Exception("The server lock is not ordinary metadata. Inspect it before recovery.");
        var archive=Path.Combine(settings.StatePath,"recovered-locks");Settings.PrivateFolder(archive);File.Move(file,Path.Combine(archive,Guid.NewGuid()+".server-lock"));
    }
    public static void Relocate(Settings old, Settings next) {
        next.Validate();
        using var controller=RuntimeInstaller.Lease(Path.Combine(old.StatePath,"controller-lease.db"));
        using var server=RuntimeInstaller.Lease(Path.Combine(old.DataPath,".server-lease.db"));
        if(File.Exists(Path.Combine(old.DataPath,".server-lock"))) throw new Exception("Recover the stopped server lock before moving folders.");
        foreach(var pair in new[]{(old.DataPath,next.DataPath),(old.StatePath,next.StatePath)}) {
            if(pair.Item1.Equals(pair.Item2,StringComparison.OrdinalIgnoreCase))continue;
            if(Directory.Exists(pair.Item2))throw new Exception("Choose new destination folders. The originals will be kept.");
            RuntimeInstaller.Copy(pair.Item1,pair.Item2);
        }
        string Rebase(string value) { foreach(var pair in new[]{(old.DataPath,next.DataPath),(old.StatePath,next.StatePath)})if(value.Equals(pair.Item1,StringComparison.OrdinalIgnoreCase)||value.StartsWith(pair.Item1.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))return pair.Item2.TrimEnd('\\')+value[pair.Item1.TrimEnd('\\').Length..];return value; }
        void Rewrite(JsonNode? node) {
            if(node is JsonObject obj)foreach(var key in obj.Select(p=>p.Key).ToArray()) {
                if(key is "release" or "previous" or "target" or "backup" or "initialRelease" or "stateDir" or "dataDir" && obj[key] is JsonValue value && value.TryGetValue<string>(out var text))obj[key]=Rebase(text);
                else Rewrite(obj[key]);
            } else if(node is JsonArray array)foreach(var child in array)Rewrite(child);
        }
        foreach(var file in Directory.EnumerateFiles(next.StatePath,"*.json").Concat(Directory.Exists(Path.Combine(next.StatePath,"history"))?Directory.EnumerateFiles(Path.Combine(next.StatePath,"history"),"*.json"):Array.Empty<string>())) {var node=JsonNode.Parse(File.ReadAllText(file));Rewrite(node);File.WriteAllText(file,node!.ToJsonString());}
        foreach(var name in new[]{"child.json","controller.lock"})File.Delete(Path.Combine(next.StatePath,name));
        if(next.RuntimePath!=null)next.RuntimePath=Rebase(next.RuntimePath);
    }
}
