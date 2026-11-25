using System.Windows;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
namespace Switchboard;
public static class AppUpdates {
    private static readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static bool busy;
    public static bool VerifyFeed(byte[] data, byte[] signature, string pem) {using var key=ECDsa.Create();key.ImportFromPem(pem);return key.VerifyData(data,signature,HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence);}

    public static async Task Check(Window owner, Manager manager, bool automatic = false) {
        if (busy || manager.Busy) return;
        busy = true;
        string? temporary = null;
        try {
            if(!new UpdateManager("https://github.com/tader/switchboard-windows/releases/latest/download/").IsInstalled){if(!automatic)System.Windows.MessageBox.Show(owner,"App updates are disabled in unsigned development builds.","Windows app updates");return;}
            var keyStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/update-public-key.pem"));
            if (keyStream == null) { if (!automatic) System.Windows.MessageBox.Show(owner,"App updates are not configured for this development build.","Windows app updates"); return; }
            var channel = "win-" + (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");
            var prefix = "https://github.com/tader/switchboard-windows/releases/latest/download/";
            var feed = await client.GetByteArrayAsync(prefix + "releases." + channel + ".json");
            var signature = await client.GetByteArrayAsync(prefix + "releases." + channel + ".json.sig");
            using var reader = new StreamReader(keyStream.Stream);
            if (!VerifyFeed(feed, signature, await reader.ReadToEndAsync())) throw new Exception("The app update feed signature is invalid.");
            temporary = Path.Combine(Settings.Root,"UpdateDownloads",Guid.NewGuid().ToString()); Settings.PrivateFolder(temporary);
            await File.WriteAllBytesAsync(Path.Combine(temporary,"releases." + channel + ".json"), feed);
            var updater = new UpdateManager(temporary, new UpdateOptions { ExplicitChannel = channel, MaximumDeltasBeforeFallback = -1 });
            var update = await updater.CheckForUpdatesAsync();
            if (update == null) { if (!automatic) System.Windows.MessageBox.Show(owner,"The Windows app is up to date.","Windows app updates"); return; }
            if(automatic){owner.Show();owner.Activate();}
            if (System.Windows.MessageBox.Show(owner,"Download and install the Windows app update? Switchboard will stop safely and the app will reopen.","Windows app update",MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            var assets = JsonDocument.Parse(feed).RootElement.GetProperty("Assets").EnumerateArray().Where(a=>a.GetProperty("Type").GetString()=="Full").ToArray();
            if (assets.Length != 1) throw new Exception("Unexpected update feed contents.");
            var asset = assets[0]; var file = asset.GetProperty("FileName").GetString()!;
            if (Path.GetFileName(file)!=file || !file.EndsWith(".nupkg",StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe update package name.");
            var bytes = await client.GetByteArrayAsync(prefix + Uri.EscapeDataString(file));
            if (bytes.LongLength != asset.GetProperty("Size").GetInt64() || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(asset.GetProperty("SHA256").GetString(),StringComparison.OrdinalIgnoreCase)) throw new Exception("The app update package failed verification.");
            await File.WriteAllBytesAsync(Path.Combine(temporary,file),bytes);
            // Verify the Windows trust signature on the main executable before
            // handing the authenticated package to the installer.
            using(var zip=System.IO.Compression.ZipFile.OpenRead(Path.Combine(temporary,file))) {
                var executable=zip.Entries.Single(e=>e.FullName.EndsWith("/Switchboard.exe",StringComparison.OrdinalIgnoreCase));
                var verifyFile=Path.Combine(temporary,"verify.exe");System.IO.Compression.ZipFileExtensions.ExtractToFile(executable,verifyFile);
                var command=new System.Diagnostics.ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var arg in new[]{"-NoProfile","-NonInteractive","-Command","if ((Get-AuthenticodeSignature -LiteralPath $env:SWITCHBOARD_VERIFY_FILE).Status -ne 'Valid') { exit 1 }"})command.ArgumentList.Add(arg);
                command.Environment["SWITCHBOARD_VERIFY_FILE"]=verifyFile;
                using var verification=System.Diagnostics.Process.Start(command)!;
                _=verification.StandardOutput.ReadToEndAsync();_=verification.StandardError.ReadToEndAsync();
                await verification.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                if(verification.ExitCode!=0)throw new Exception("The Windows app signature is invalid.");
            }
            await updater.DownloadUpdatesAsync(update);
            if (!await manager.Stop()) return;
            updater.ApplyUpdatesAndRestart(update);
        } catch (Exception e) { if (!automatic) System.Windows.MessageBox.Show(owner,e.Message,"Windows app update unavailable"); }
        finally { if (temporary != null && Directory.Exists(temporary)) Directory.Delete(temporary,true); busy=false; }
    }
}
