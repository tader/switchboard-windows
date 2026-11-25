using System.Diagnostics;
using System.Runtime.InteropServices;
namespace Switchboard;
public sealed class Job : IDisposable {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job,int kind,IntPtr data,uint size);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] struct Basic {public long A,B; public uint Flags; public UIntPtr Min,Max; public uint Active; public UIntPtr Affinity; public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)] struct Io {public ulong A,B,C,D,E,F;}
    [StructLayout(LayoutKind.Sequential)] struct Extended {public Basic Basic; public Io Io; public UIntPtr A,B,C,D;}
    IntPtr handle=CreateJobObject(IntPtr.Zero,null);
    public Job() {var info=new Extended{Basic=new Basic{Flags=0x2000}};var size=Marshal.SizeOf<Extended>();var ptr=Marshal.AllocHGlobal(size);try{Marshal.StructureToPtr(info,ptr,false);if(handle==IntPtr.Zero || !SetInformationJobObject(handle,9,ptr,(uint)size))throw new Exception("Could not create owned process job.");}finally{Marshal.FreeHGlobal(ptr);}}
    public void Attach(Process process) {if(!AssignProcessToJobObject(handle,process.Handle)){process.Kill(true);throw new Exception("Could not contain the owned server process.");}}
    public void Dispose(){var owned=Interlocked.Exchange(ref handle,IntPtr.Zero);if(owned!=IntPtr.Zero)CloseHandle(owned);}
}
