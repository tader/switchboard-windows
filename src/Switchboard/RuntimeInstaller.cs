using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Net.Http;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
namespace Switchboard;
public static class RuntimeInstaller {
    public static string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    private static readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10), DefaultRequestHeaders = { { "User-Agent", "switchboard-windows" } } };
    public static async Task<JsonElement[]> Releases(int page=1,CancellationToken cancellation=default) => JsonSerializer.Deserialize<JsonElement[]>(await client.GetStringAsync("https://api.github.com/repos/tader/switchboard/releases?per_page=100&page="+page,cancellation))!;
    public static SqliteConnection Lease(string file) { var db = new SqliteConnection($"Data Source={file};Default Timeout=1;Pooling=False"); db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS lease(id INTEGER); BEGIN EXCLUSIVE;"; cmd.ExecuteNonQuery(); return db; }
    public static async Task<string> Install(Settings settings, Action<string> progress, CancellationToken cancellation = default) {
        settings.Validate(); Settings.PrivateFolder(settings.StatePath);
        var releases = await Releases(1,cancellation);
        var name = $"runtime-win32-{Arch}.json";
        var release = releases.FirstOrDefault(r => !r.GetProperty("draft").GetBoolean() && (settings.Pin != null ? r.GetProperty("tag_name").GetString() == settings.Pin : r.GetProperty("prerelease").GetBoolean() == (settings.Channel == "development")) && r.GetProperty("assets").EnumerateArray().Any(a => a.GetProperty("name").GetString() == name));
        if (release.ValueKind == JsonValueKind.Undefined) throw new Exception("No compatible Windows runtime is published for this selection yet.");
        var tag = release.GetProperty("tag_name").GetString()!;
        var prefix = "https://github.com/tader/switchboard/releases/download/" + Uri.EscapeDataString(tag) + "/";
        var manifest = JsonDocument.Parse(await client.GetStringAsync(prefix + name)).RootElement.Clone();
        var revision = manifest.GetProperty("revision").GetString()!;
        var archiveName = $"switchboard-runtime-win32-{Arch}.tar.gz";
        if (manifest.GetProperty("origin").GetString() != "tader/switchboard" || manifest.GetProperty("platform").GetString() != "win32" || manifest.GetProperty("arch").GetString() != Arch || manifest.GetProperty("version").GetString() != tag.TrimStart('v') || manifest.GetProperty("protocol").GetInt32() != 1 || manifest.GetProperty("archive").GetString() != archiveName || !System.Text.RegularExpressions.Regex.IsMatch(revision, "^[a-f0-9]{40}$")) throw new Exception("Incompatible runtime metadata.");
        var commit = JsonDocument.Parse(await client.GetStringAsync("https://api.github.com/repos/tader/switchboard/commits/" + Uri.EscapeDataString(tag))).RootElement;
        if (commit.GetProperty("sha").GetString() != revision) throw new Exception("Release revision does not match its runtime.");
        var stage = Path.Combine(settings.StatePath, "releases", Guid.NewGuid() + ".partial"); Settings.PrivateFolder(stage);
        try {
            var archive = Path.Combine(stage, "download.tar.gz");
            using (var response = await client.GetAsync(prefix + archiveName, HttpCompletionOption.ResponseHeadersRead,cancellation)) {
                response.EnsureSuccessStatusCode();
                var host = response.RequestMessage!.RequestUri!.Host;
                if (host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new Exception("Untrusted runtime origin.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellation); await using var output = File.Create(archive);
                var buffer = new byte[65536]; long received = 0; int count;
                while ((count = await input.ReadAsync(buffer, cancellation)) > 0) { received += count; if (received > 512L*1024*1024) throw new Exception("Runtime exceeds download limit."); await output.WriteAsync(buffer.AsMemory(0,count)); progress(response.Content.Headers.ContentLength is long total ? $"Downloading… {received*100/total}% · {received/1048576} MB" : $"Downloading… {received/1048576} MB"); }
            }
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))).ToLowerInvariant() != manifest.GetProperty("sha256").GetString()) throw new Exception("Runtime checksum mismatch.");
            progress("Verifying and unpacking…");
            using (var file = File.OpenRead(archive)) using (var gzip = new GZipStream(file, CompressionMode.Decompress)) using (var tar = new TarReader(gzip)) {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long size=0;
                while (tar.GetNextEntry() is { } entry) {
                    cancellation.ThrowIfCancellationRequested();
                    var parts = entry.Name.TrimEnd('/').Split('/');
                    if (parts.Any(p => p.Length==0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || System.Text.RegularExpressions.Regex.IsMatch(p, "^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:[.]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) || !names.Add(entry.Name.TrimEnd('/')) || entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile)) throw new Exception("Unsafe runtime archive.");
                    size += entry.Length; if(size>1024L*1024*1024) throw new Exception("Expanded runtime exceeds limit.");
                    var destination = Path.Combine(stage, Path.Combine(parts));
                    if(entry.EntryType==TarEntryType.Directory) Directory.CreateDirectory(destination); else { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); entry.ExtractToFile(destination,false); }
                }
            }
            File.Delete(archive);
            var info=JsonDocument.Parse(File.ReadAllText(Path.Combine(stage,"build-info.json"))).RootElement;
            var package=JsonDocument.Parse(File.ReadAllText(Path.Combine(stage,"package.json"))).RootElement;
            if(package.GetProperty("managedUpgradeProtocol").GetInt32()!=1 || package.GetProperty("version").GetString()!=manifest.GetProperty("version").GetString() || info.GetProperty("version").GetString()!=manifest.GetProperty("version").GetString())throw new Exception("Incompatible runtime build metadata.");
            if(info.GetProperty("revision").GetString()!=revision || info.GetProperty("platform").GetString()!="win32" || info.GetProperty("arch").GetString()!=Arch || !File.Exists(Path.Combine(stage,"bin","node.exe"))) throw new Exception("Runtime identity mismatch.");
            using var controller=Lease(Path.Combine(settings.StatePath,"controller-lease.db"));
            Settings.PrivateFolder(settings.DataPath);
            using var server=Lease(Path.Combine(settings.DataPath,".server-lease.db"));
            if(File.Exists(Path.Combine(settings.DataPath,".server-lock"))) throw new Exception("A server lock remains. Preserve the folders and resolve the previous instance before installing.");
            var coreJournal=Path.Combine(settings.StatePath,"journal.json");
            if(File.Exists(coreJournal) && JsonDocument.Parse(File.ReadAllText(coreJournal)).RootElement.GetProperty("phase").GetString() is not ("succeeded" or "rolled-back" or "failed" or "cancelled")) throw new Exception("Resolve the updater recovery journal before selecting a runtime.");
            var backup=Path.Combine(settings.StatePath,"backups",Guid.NewGuid().ToString()); Copy(settings.DataPath,backup);
            var rehearsal=Path.Combine(settings.StatePath,"rehearsal-"+Guid.NewGuid()); Copy(backup,rehearsal);
            try {
                var psi=new ProcessStartInfo(Path.Combine(stage,"bin","node.exe")) {WorkingDirectory=stage,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                psi.Environment["SWITCHBOARD_DATA_DIR"]=rehearsal; psi.Environment.Remove("NODE_OPTIONS");
                foreach(var arg in new[]{"--input-type=module","-e","const m=await import('./server/db.ts');m.initDb();m.db.close()"}) psi.ArgumentList.Add(arg);
                using var probe=Process.Start(psi)!; _=probe.StandardOutput.ReadToEndAsync(); _=probe.StandardError.ReadToEndAsync();
                try { await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); } catch { probe.Kill(true); throw; }
                if(probe.ExitCode!=0) throw new Exception("This runtime cannot open a copy of your database. Your data and backup are preserved.");
            } finally { await Settings.RemoveFolder(rehearsal); }
            progress("Finishing installation…");
            cancellation.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(stage,"runtime.json"),manifest.GetRawText()); File.WriteAllText(Path.Combine(stage,".prepared"),"");
            var final=Path.Combine(settings.StatePath,"releases",revision+"-"+Guid.NewGuid()); Directory.Move(stage,final);
            var pending = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
            pending.RuntimePath=final;pending.Configured=true;
            Settings.Atomic(Path.Combine(settings.StatePath,"runtime-install.json"),JsonSerializer.Serialize(pending));
            Settings.Atomic(Path.Combine(settings.StatePath,"current.json"),JsonSerializer.Serialize(new {release=final}));
            return final;
        } finally { await Settings.RemoveFolder(stage); }
    }
    public static void Copy(string source,string target,bool root=true) {
        Settings.PrivateFolder(target);
        foreach(var file in Directory.EnumerateFileSystemEntries(source)) {
            if(Path.GetFileName(file).StartsWith("Singleton")) continue;
            if(root && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file),@"^(?:[.]server-lease|controller-lease)[.]db(?:-|$)")) continue;
            var attrs=File.GetAttributes(file); if(attrs.HasFlag(FileAttributes.ReparsePoint)) throw new Exception("Snapshots require folders without reparse points.");
            var dest=Path.Combine(target,Path.GetFileName(file));
            if(attrs.HasFlag(FileAttributes.Directory)) Copy(file,dest,false); else {File.Copy(file,dest); using var saved=new FileStream(dest,FileMode.Open,FileAccess.ReadWrite,FileShare.Read);saved.Flush(true);using var original=File.OpenRead(file);if(!SHA256.HashData(original).SequenceEqual(SHA256.HashData(saved))) throw new Exception("Backup verification failed.");}
        }
    }
}
