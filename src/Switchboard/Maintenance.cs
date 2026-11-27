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
    internal sealed class RelocationRecord {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string SourceData { get; set; } = "";
        public string SourceState { get; set; } = "";
        public string TargetData { get; set; } = "";
        public string TargetState { get; set; } = "";
        public string? Runtime { get; set; }
        public string? PreviewControlHash { get; set; }
        public bool Ready { get; set; }
        public Dictionary<string,string>? SourceDataInventory { get; set; }
        public Dictionary<string,string>? SourceStateInventory { get; set; }
        public Dictionary<string,string>? DataInventory { get; set; }
        public Dictionary<string,string>? StateInventory { get; set; }
        public string? DataStage { get; set; }
        public string? StateStage { get; set; }
    }
    internal const string RelocationFile = "desktop-relocation.json";
    // Test seams model a process interruption without weakening the persisted checks.
    internal static Action<string>? RelocationCheckpoint;
    internal static Action<string,string> CopyRelocation = (source,target) => RuntimeInstaller.Copy(source,target);
    internal static bool Same(string a,string b) => Settings.Canonical(a).Equals(Settings.Canonical(b),StringComparison.OrdinalIgnoreCase);
    internal static bool RelocationMetadata(string name) => name == RelocationFile || name.StartsWith(RelocationFile+".",StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase);
    static string? PreviewControlHash(Settings settings) {
        var file=Path.Combine(settings.StatePath,"activity-preview-control.json");
        return File.Exists(file)?Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))):null;
    }
    static Dictionary<string,string> Inventory(string folder) {
        var entries = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder)) return entries;
        if (File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint)) throw new Exception("Relocation requires folders without reparse points.");
        void Visit(string directory,string prefix,bool root) {
            foreach(var file in Directory.EnumerateFileSystemEntries(directory)) {
                var name=Path.GetFileName(file);
                if(name.StartsWith("Singleton") || root && (RelocationMetadata(name) || System.Text.RegularExpressions.Regex.IsMatch(name,@"^(?:[.]server-lease|controller-lease)[.]db(?:-|$)"))) continue;
                var attrs=File.GetAttributes(file);if(attrs.HasFlag(FileAttributes.ReparsePoint))throw new Exception("Relocation requires folders without reparse points.");
                var relative=Path.Combine(prefix,name);
                if(attrs.HasFlag(FileAttributes.Directory)){entries[relative]="directory";Visit(file,relative,false);}
                else {using var stream=File.OpenRead(file);entries[relative]=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));}
            }
        }
        Visit(folder,"",true);return entries;
    }
    static bool Matches(string folder,Dictionary<string,string> expected) {
        var actual=Inventory(folder);return actual.Count==expected.Count && expected.All(pair=>actual.TryGetValue(pair.Key,out var hash)&&hash==pair.Value);
    }
    public static void Relocate(Settings old, Settings next, bool copy = true, Action<Settings>? commit = null) {
        next.Validate();
        bool movingData=!Same(old.DataPath,next.DataPath),movingState=!Same(old.StatePath,next.StatePath);
        if(!movingData&&!movingState)return;
        if(!copy && (movingData&&Directory.Exists(old.DataPath) || movingState&&Directory.Exists(old.StatePath)))
            throw new Exception("Choose Copy and verify to relocate existing folders. The originals will be kept.");
        var journal=Path.Combine(old.StatePath,RelocationFile);
        var record=File.Exists(journal)?System.Text.Json.JsonSerializer.Deserialize<RelocationRecord>(File.ReadAllText(journal))!:null;
        if(record!=null && (!Same(record.SourceData,old.DataPath)||!Same(record.SourceState,old.StatePath)||!Same(record.TargetData,next.DataPath)||!Same(record.TargetState,next.StatePath)))
            throw new Exception("A folder move is pending. Retry its original destination folders before starting another move.");
        foreach(var pair in new[]{(old.DataPath,next.DataPath),(old.StatePath,next.StatePath)}) {
            if(Same(pair.Item1,pair.Item2))continue;
            if((Directory.Exists(pair.Item2)||File.Exists(pair.Item2)) && record?.Ready!=true)
                throw new Exception("Choose new destination folders. The originals will be kept.");
            foreach(var source in new[]{old.DataPath,old.StatePath}) {
                var from=Settings.Canonical(source).TrimEnd('\\');var to=Settings.Canonical(pair.Item2).TrimEnd('\\');
                if(to.Equals(from,StringComparison.OrdinalIgnoreCase)||to.StartsWith(from+"\\",StringComparison.OrdinalIgnoreCase)||from.StartsWith(to+"\\",StringComparison.OrdinalIgnoreCase))
                    throw new Exception("New folders must not overlap either original folder.");
            }
        }
        Settings.PrivateFolder(old.StatePath);Settings.PrivateFolder(old.DataPath);
        using var controller=RuntimeInstaller.Lease(Path.Combine(old.StatePath,"controller-lease.db"));
        using var server=RuntimeInstaller.Lease(Path.Combine(old.DataPath,".server-lease.db"));
        if(File.Exists(Path.Combine(old.DataPath,".server-lock")))throw new Exception("Recover the stopped server lock before moving folders.");
        using var targetController=movingState&&Directory.Exists(next.StatePath)?RuntimeInstaller.Lease(Path.Combine(next.StatePath,"controller-lease.db")):null;
        using var targetServer=movingData&&Directory.Exists(next.DataPath)?RuntimeInstaller.Lease(Path.Combine(next.DataPath,".server-lease.db")):null;
        if(movingData&&File.Exists(Path.Combine(next.DataPath,".server-lock")) || movingState&&File.Exists(Path.Combine(next.StatePath,"controller.lock")))
            throw new Exception("Stop the destination server and updater before resuming this move.");
        void Save() => Settings.Atomic(journal,System.Text.Json.JsonSerializer.Serialize(record));
        if(record==null) {
            // All destinations are valid before advancing the source epoch.
            if(movingData)_=Inventory(old.DataPath);
            if(movingState)_=Inventory(old.StatePath);
            PreviewLifecycle.Retire(old,next.DataPath,next.StatePath);
            record=new RelocationRecord{SourceData=old.DataPath,SourceState=old.StatePath,TargetData=next.DataPath,TargetState=next.StatePath,Runtime=next.RuntimePath,
                PreviewControlHash=PreviewControlHash(old),
                SourceDataInventory=movingData?Inventory(old.DataPath):null,SourceStateInventory=movingState?Inventory(old.StatePath):null};
            Save();
        }
        if(PreviewControlHash(old)!=record.PreviewControlHash)throw new Exception("Preview ownership changed during a pending move. Preserve both installations for recovery.");
        if(movingData&&!Matches(old.DataPath,record.SourceDataInventory!) || movingState&&!Matches(old.StatePath,record.SourceStateInventory!))
            throw new Exception("An original folder changed during a pending move. Preserve the originals and staged copies for recovery.");
        if(!record.Ready) {
            // A partial stage is retained as evidence; a retry gets a fresh stage, never
            // deletes user files or treats a partial copy as a verified destination.
            record.DataStage=movingData?next.DataPath+".switchboard-relocation-"+Guid.NewGuid():null;
            record.StateStage=movingState?next.StatePath+".switchboard-relocation-"+Guid.NewGuid():null;
            Save();
            if(movingData)CopyRelocation(old.DataPath,record.DataStage!);
            RelocationCheckpoint?.Invoke("data-copied");
            if(movingState)CopyRelocation(old.StatePath,record.StateStage!);
            RelocationCheckpoint?.Invoke("state-copied");
            if(movingData&&!Matches(record.DataStage!,record.SourceDataInventory!) || movingState&&!Matches(record.StateStage!,record.SourceStateInventory!))
                throw new Exception("Folder verification failed. The original folders are preserved.");
            if(movingState) {
                RebaseState(record.StateStage!,old,next);
                PreviewLifecycle.ResetCopiedIdentity(record.StateStage!);
                foreach(var name in new[]{"child.json","controller.lock"})File.Delete(Path.Combine(record.StateStage!,name));
            }
            record.Runtime=Rebase(record.Runtime,old,next);
            record.DataInventory=movingData?Inventory(record.DataStage!):null;
            record.StateInventory=movingState?Inventory(record.StateStage!):null;
            record.Ready=true;Save();
            RelocationCheckpoint?.Invoke("verified");
        }
        void Publish(string? stage,string target,Dictionary<string,string>? expected) {
            if(expected==null)return;
            if(Directory.Exists(target)) {
                if(!Matches(target,expected))throw new Exception("A retained relocation destination changed. Preserve both folders for recovery.");
            } else {
                if(!Directory.Exists(stage)||!Matches(stage!,expected))throw new Exception("A verified relocation stage changed. Preserve the originals for recovery.");
                Directory.Move(stage!,target);
            }
        }
        Publish(record.DataStage,next.DataPath,record.DataInventory);RelocationCheckpoint?.Invoke("data-published");
        Publish(record.StateStage,next.StatePath,record.StateInventory);RelocationCheckpoint?.Invoke("state-published");
        using var publishedController=movingState&&targetController==null?RuntimeInstaller.Lease(Path.Combine(next.StatePath,"controller-lease.db")):null;
        using var publishedServer=movingData&&targetServer==null?RuntimeInstaller.Lease(Path.Combine(next.DataPath,".server-lease.db")):null;
        if(!movingState)RebaseState(old.StatePath,old,next);
        next.RuntimePath=record.Runtime;
        RelocationCheckpoint?.Invoke("before-settings");
        // Keep both installations' leases through the settings commit, including retries.
        if(commit!=null){commit(next);CompleteRelocation(old);}
    }
    static string? Rebase(string? value,Settings old,Settings next) {
        if(value==null)return null;
        foreach(var pair in new[]{(old.DataPath,next.DataPath),(old.StatePath,next.StatePath)}) {
            var prefix=pair.Item1.TrimEnd('\\');
            if(value.Equals(prefix,StringComparison.OrdinalIgnoreCase)||value.StartsWith(prefix+"\\",StringComparison.OrdinalIgnoreCase))return pair.Item2.TrimEnd('\\')+value[prefix.Length..];
        }
        return value;
    }
    static void RebaseState(string directory,Settings old,Settings next) {
        void Rewrite(JsonNode? node) {
            if(node is JsonObject obj)foreach(var key in obj.Select(p=>p.Key).ToArray()) {
                if(key is "release" or "previous" or "target" or "backup" or "initialRelease" or "stateDir" or "dataDir" && obj[key] is JsonValue value && value.TryGetValue<string>(out var text))obj[key]=Rebase(text,old,next);
                else Rewrite(obj[key]);
            } else if(node is JsonArray array)foreach(var child in array)Rewrite(child);
        }
        foreach(var file in Directory.EnumerateFiles(directory,"*.json").Concat(Directory.Exists(Path.Combine(directory,"history"))?Directory.EnumerateFiles(Path.Combine(directory,"history"),"*.json"):Array.Empty<string>())) {
            if(Path.GetFileName(file) is RelocationFile or "activity-preview-control.json")continue;
            var node=JsonNode.Parse(File.ReadAllText(file));Rewrite(node);Settings.Atomic(file,node!.ToJsonString());
        }
    }
    internal static void CompleteRelocation(Settings source) => File.Delete(Path.Combine(source.StatePath,RelocationFile));
    internal static RelocationRecord? PendingRelocation(Settings settings) {
        var journal=Path.Combine(settings.StatePath,RelocationFile);
        return File.Exists(journal)?System.Text.Json.JsonSerializer.Deserialize<RelocationRecord>(File.ReadAllText(journal)):null;
    }
    internal static void RecoverCommittedRelocation(Settings settings) {
        var journal=Path.Combine(settings.StatePath,RelocationFile);
        if(!File.Exists(journal))return;
        var record=System.Text.Json.JsonSerializer.Deserialize<RelocationRecord>(File.ReadAllText(journal))!;
        if(record.Ready&&Same(record.TargetData,settings.DataPath)&&Same(record.TargetState,settings.StatePath))File.Delete(journal);
    }
}
