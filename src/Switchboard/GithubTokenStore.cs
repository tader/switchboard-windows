using System.Runtime.InteropServices;
using System.Text;
namespace Switchboard;

/// A local, per-user credential. It never participates in Settings serialization.
public sealed class GithubTokenStore {
    readonly string target;
    public GithubTokenStore(string target = "Switchboard/GitHubInstallToken") { this.target = target; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint="CredReadW", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool ReadCredential(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint="CredWriteW", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool WriteCredential(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint="CredDeleteW", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool DeleteCredential(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr credential);
    static Exception Failure() => new Exception("Could not access the GitHub token in Windows Credential Manager. Check your Windows sign-in and retry.");
    public string? Read() {
        if (!ReadCredential(target, 1, 0, out var pointer)) {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw Failure();
        }
        try {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var bytes = new byte[credential.CredentialBlobSize];
            try { Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes); }
            finally { Array.Clear(bytes); }
        } finally { CredFree(pointer); }
    }
    public static string Validate(string value) {
        var token = value.Trim();
        if (token.Length == 0 || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || Encoding.UTF8.GetByteCount(token) > 2560)
            throw new Exception("Enter a GitHub token without spaces or line breaks (maximum 2560 bytes).");
        return token;
    }
    public void Save(string value) {
        var bytes = Encoding.UTF8.GetBytes(Validate(value));
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type=1, TargetName=target, UserName="GitHub", CredentialBlob=blob, CredentialBlobSize=(uint)bytes.Length, Persist=2 };
            if (!WriteCredential(ref credential, 0)) throw Failure();
        } finally { for(var i=0;i<bytes.Length;i++)Marshal.WriteByte(blob,i,0); Marshal.FreeHGlobal(blob); Array.Clear(bytes); }
    }
    public void Remove() { if (!DeleteCredential(target, 1, 0) && Marshal.GetLastWin32Error()!=1168) throw Failure(); }
}
