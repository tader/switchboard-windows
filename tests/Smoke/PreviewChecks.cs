using System.Diagnostics;
using System.Text.Json;
using Switchboard;

static class PreviewChecks {
    static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    static JsonElement Control(Settings s) => JsonDocument.Parse(File.ReadAllText(Path.Combine(s.StatePath, "activity-preview-control.json"))).RootElement;
    static void Seed(Settings s, string runtime) {
        Settings.PrivateFolder(s.StatePath); Settings.PrivateFolder(s.DataPath);
        var start = new ProcessStartInfo(Path.Combine(runtime,"bin","node.exe")) { WorkingDirectory=runtime, UseShellExecute=false, RedirectStandardError=true, RedirectStandardOutput=true };
        start.Environment.Remove("NODE_OPTIONS"); start.Environment.Remove("NODE_PATH");
        foreach (var arg in new[] { "--input-type=module", "-e", "const fs=await import('node:fs');const m=await import('./updater/activity-previews.ts');const e=await import('./updater/engine.ts');const [dataDir,stateDir,initialRelease]=process.argv.slice(1);const s={instance:'local-manager',mode:'runtime',dataDir,stateDir,initialRelease,env:{}};const env=await m.provisionPreviews(s,initialRelease,[dataDir,stateDir,initialRelease],async()=>'',e.atomic);const p=await import('./shared/activity-preview-store.ts');p.writePreviewFile(env.SWITCHBOARD_ACTIVITY_PREVIEW_ROOT,'preview-'+env.SWITCHBOARD_ACTIVITY_PREVIEW_STORE_ID+'-22222222-2222-2222-2222-222222222222.enc','synthetic', [dataDir,stateDir,initialRelease],env.SWITCHBOARD_ACTIVITY_PREVIEW_INSTANCE,env.SWITCHBOARD_ACTIVITY_PREVIEW_STORE_ID);fs.writeFileSync(env.SWITCHBOARD_ACTIVITY_PREVIEW_ROOT+'/sentinel','unrelated');", s.DataPath,s.StatePath,runtime }) start.ArgumentList.Add(arg);
        using var child=Process.Start(start)!;var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();
        if(!child.WaitForExit(60000)){child.Kill(true);child.WaitForExit();throw new Exception("Preview fixture timed out");}
        Task.WhenAll(output,error).GetAwaiter().GetResult();Require(child.ExitCode==0,"Preview fixture failed: "+error.Result);
    }
    public static void Run(string root,string runtime) {
        Require(PreviewLifecycle.Supports(runtime),"Core fixture does not support preview storage");
        foreach (var move in new[]{"data","state","both"}) {
            var source=new Settings{DataPath=Path.Combine(root,move,"Data"),StatePath=Path.Combine(root,move,"State"),RuntimePath=runtime};
            Seed(source,runtime);var before=Control(source);File.WriteAllText(Path.Combine(source.DataPath,"synthetic.txt"),"preserved");
            var next=new Settings{DataPath=move=="state"?source.DataPath:Path.Combine(root,move,"NewData"),StatePath=move=="data"?source.StatePath:Path.Combine(root,move,"NewState"),RuntimePath=runtime};
            Maintenance.Relocate(source,next);
            var retired=Control(source);Require(retired.GetProperty("epoch").GetString()!=before.GetProperty("epoch").GetString(),"Source epoch did not advance");
            Require(retired.GetProperty("stores").EnumerateArray().All(b=>b.GetProperty("retired").GetBoolean()),"Source binding still live");
            var preview=Settings.Canonical(source.StatePath)+".activity-previews";
            Require(!Directory.EnumerateFiles(preview,"*.enc").Any(),"Relocation left old ciphertext");
            Require(File.ReadAllText(Path.Combine(preview,"sentinel"))=="unrelated","Relocation removed sentinel");
            if(move!="data")Require(Control(next).GetProperty("instance").GetString()!=before.GetProperty("instance").GetString(),"Copied installation identity was reused");
            Seed(next,runtime);Require(File.ReadAllText(Path.Combine(next.DataPath,"synthetic.txt"))=="preserved","Relocation lost data");
        }
        var old=new Settings{DataPath=Path.Combine(root,"downgrade","Data"),StatePath=Path.Combine(root,"downgrade","State"),RuntimePath=runtime};Seed(old,runtime);
        var older=Path.Combine(root,"incapable");Directory.CreateDirectory(older);File.WriteAllText(Path.Combine(older,"runtime.json"),"{}");
        Settings.Atomic(Path.Combine(old.StatePath,"current.json"),JsonSerializer.Serialize(new{release=runtime}));
        var pending=new Settings{DataPath=old.DataPath,StatePath=old.StatePath,RuntimePath=older};
        Settings.Atomic(Path.Combine(old.StatePath,"runtime-install.json"),JsonSerializer.Serialize(pending));
        var store=Settings.Canonical(old.StatePath)+".activity-previews";
        var ciphertext=Directory.GetFiles(store,"*.enc").Single();File.Delete(ciphertext);Directory.CreateDirectory(ciphertext);
        using(var controller=RuntimeInstaller.Lease(Path.Combine(old.StatePath,"controller-lease.db")))
        using(var server=RuntimeInstaller.Lease(Path.Combine(old.DataPath,".server-lease.db"))) {
            try{PreviewLifecycle.RecoverSelection(old,pending);throw new Exception("Unresolved retirement committed downgrade");}
            catch(Exception e){Require(e.Message.Contains("retire"),"Unexpected downgrade failure");}
            Require(PreviewLifecycle.SelectedRuntime(old)==runtime,"Failed retirement advanced current.json");
            Require(File.Exists(Path.Combine(old.StatePath,"runtime-install.json")),"Failed retirement removed journal");
            Directory.Delete(ciphertext);PreviewLifecycle.RecoverSelection(old,pending);
            Require(PreviewLifecycle.SelectedRuntime(old)==older,"Recovered downgrade did not select target");
        }
        Require(File.ReadAllText(Path.Combine(store,"sentinel"))=="unrelated","Downgrade deleted sentinel");
        Seed(old,runtime);Require(Directory.GetFiles(store,"*.enc").Length==1,"Re-upgrade failed fresh capture");
        Console.WriteLine("PASS: native data/state/combined relocation, copied identity, source retirement, interrupted downgrade journal replay, re-upgrade, sentinel preservation");
    }
}
