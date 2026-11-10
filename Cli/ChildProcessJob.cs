using System.Runtime.InteropServices;
using System.Diagnostics;

namespace UvncFtpBridge.Cli;

/// <summary>
/// 把子程序綁在 Windows 工作物件（KILL_ON_JOB_CLOSE）上：通道主機不管怎麼結束（當掉、被強制結束），
/// 系統關掉 handle 時 ft.exe 就跟著結束。殘留的 ft.exe 會繼續寫同一個共用檔、佔住 SOCKS 埠，
/// 跳板新開的 ft.exe 會讀到它的位置，新的連線請求就永遠到不了。
/// </summary>
internal sealed class ChildProcessJob : IDisposable
{
    const uint KillOnJobClose = 0x2000;
    const int ExtendedLimitInformation = 9;

    IntPtr handle;

    ChildProcessJob(IntPtr handle) => this.handle = handle;

    /// <summary>綁不上（系統不允許）回 null；這時只靠正常關閉和下次啟動時清殘留。</summary>
    public static ChildProcessJob? Attach(Process process)
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return null;
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
        int size = Marshal.SizeOf<ExtendedLimit>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (SetInformationJobObject(job, ExtendedLimitInformation, buffer, (uint)size) &&
                AssignProcessToJobObject(job, process.Handle))
                return new ChildProcessJob(job);
        }
        finally { Marshal.FreeHGlobal(buffer); }
        CloseHandle(job);
        return null;
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        CloseHandle(handle);
        handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
