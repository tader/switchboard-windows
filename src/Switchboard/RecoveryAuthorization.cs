namespace Switchboard;
public static class RecoveryAuthorization {
    public static async Task Run(Func<Task<bool>> verify, Func<bool> current, Func<Task> request) {
        if (!await verify()) throw new Exception("Recovery was not authorized. No link was requested.");
        if (!current()) throw new Exception("Switchboard changed while verifying. Please retry.");
        await request();
    }
}
