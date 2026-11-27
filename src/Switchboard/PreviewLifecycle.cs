using System.Diagnostics;
using System.IO;
using System.Text.Json;
namespace Switchboard;

// Call only with the writer stopped and both installation leases held.
internal static class PreviewLifecycle {
    internal static string? SelectedRuntime(Settings settings) {
        var pointer = Path.Combine(settings.StatePath, "current.json");
        return File.Exists(pointer) ? JsonDocument.Parse(File.ReadAllText(pointer)).RootElement.GetProperty("release").GetString()
            ?? throw new Exception("The selected runtime metadata needs repair.") : settings.RuntimePath;
    }
    internal static bool Supports(string runtime) {
        var file = Path.Combine(runtime, "build-info.json");
        return File.Exists(file) && JsonDocument.Parse(File.ReadAllText(file)).RootElement.TryGetProperty("activityPreviewStore", out var capability)
            && capability.TryGetInt32(out var version) && version == 1;
    }
    internal static void Retire(Settings settings, params string[] destinations) {
        var file = Path.Combine(settings.StatePath, "activity-preview-control.json");
        if (!File.Exists(file)) return;
        var stores = JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("stores");
        foreach (var binding in stores.EnumerateArray()) {
            if (binding.GetProperty("kind").GetString() != "checkout") throw new Exception("Unrecognized desktop preview binding. Preserve the installation for recovery.");
            var root = Settings.Canonical(binding.GetProperty("location").GetString()!).TrimEnd('\\');
            foreach (var folder in new[] { settings.DataPath, settings.StatePath }.Concat(destinations)) {
                var other = Settings.Canonical(folder).TrimEnd('\\');
                if (root.Equals(other, StringComparison.OrdinalIgnoreCase) || root.StartsWith(other + "\\", StringComparison.OrdinalIgnoreCase) || other.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Preview storage must not overlap the original or destination folders.");
            }
        }
        if (!stores.EnumerateArray().Any(binding => !binding.TryGetProperty("retired", out var retired) || retired.ValueKind != JsonValueKind.True)) return;
        var runtime = SelectedRuntime(settings);
        if (runtime == null || !Supports(runtime)) throw new Exception("Preview storage needs recovery with a compatible runtime before changing this installation.");
        var start = new ProcessStartInfo(Path.Combine(runtime, "bin", "node.exe")) {
            WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var key in start.Environment.Keys.ToArray())
            if (key.StartsWith("SWITCHBOARD_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("HUB_", StringComparison.OrdinalIgnoreCase) || key is "NODE_OPTIONS" or "NODE_PATH") start.Environment.Remove(key);
        foreach (var arg in new[] { "updater/retire-desktop-previews.ts", settings.DataPath, settings.StatePath, runtime }.Concat(destinations)) start.ArgumentList.Add(arg);
        using var helper = Process.Start(start) ?? throw new Exception("Could not start preview retirement.");
        var output = helper.StandardOutput.ReadToEndAsync(); var error = helper.StandardError.ReadToEndAsync();
        if (!helper.WaitForExit(90000)) { helper.Kill(true); helper.WaitForExit(); throw new Exception("Preview retirement timed out. Check storage and retry."); }
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        if (helper.ExitCode != 0) throw new Exception("Could not retire the existing preview store. Check storage and retry.");
    }
    internal static void RecoverSelection(Settings current, Settings pending) {
        if (pending.RuntimePath == null || !File.Exists(Path.Combine(pending.RuntimePath, "runtime.json")))
            throw new Exception("Installation recovery requires inspection. Preserve both folders.");
        // Keep current.json pointing at the last capable helper until retirement is durable.
        // Replaying this after a crash only skips bindings already acknowledged by that helper.
        if (!Supports(pending.RuntimePath)) Retire(current);
        Settings.Atomic(Path.Combine(current.StatePath, "current.json"), JsonSerializer.Serialize(new { release = pending.RuntimePath }));
    }
    internal static void ResetCopiedIdentity(string statePath) {
        var file = Path.Combine(statePath, "activity-preview-control.json");
        if (!File.Exists(file)) return;
        var state = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        if (state.GetProperty("stores").EnumerateArray().Any(binding => !binding.TryGetProperty("retired", out var retired) || retired.ValueKind != JsonValueKind.True))
            throw new Exception("Copied preview state still has a live binding. Preserve both folders.");
        Settings.Atomic(file, JsonSerializer.Serialize(new { instance = Guid.NewGuid().ToString(), epoch = Guid.NewGuid().ToString(), stores = Array.Empty<object>() }));
    }
}
