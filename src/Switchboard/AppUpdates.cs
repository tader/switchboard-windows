using System.Windows;
namespace Switchboard;
public static class AppUpdates {
    public static Task Check(Window owner,Manager manager) {
        // Publication remains gated until the signing identity and authenticated
        // update feed have been configured and exercised on both architectures.
        System.Windows.MessageBox.Show(owner,"App updates are not configured for this development build.","Windows app updates");
        return Task.CompletedTask;
    }
}
