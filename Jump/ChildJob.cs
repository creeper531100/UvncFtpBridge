using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UvncFtpJump
{
    /// <summary>
    /// 一個 ft.exe 一個 Windows 工作物件（JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE）：
    /// 關掉 handle（Stop，或 UvncFtpJump 不管怎麼結束系統都會關）就連 ft.exe 底下的子程序一起收掉。
    /// Win7 上已在別的工作物件裡的程序可能加不進來（不支援巢狀），那時改用 taskkill /T 收整棵程序樹。
    /// </summary>
    internal sealed class ChildJob : IDisposable
    {
        IntPtr handle;
        readonly Process process;
        readonly bool assigned;

        public ChildJob(Process process)
        {
            this.process = process;
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) return;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (SetInformationJobObject(handle, 9 /* JobObjectExtendedLimitInformation */, buffer, (uint)size))
                    assigned = AssignProcessToJobObject(handle, process.Handle);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>收掉 ft.exe 和它的子程序。</summary>
        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                if (assigned) TerminateJobObject(handle, 1);
                CloseHandle(handle);
                handle = IntPtr.Zero;
            }
            try
            {
                if (process.HasExited) return;
                if (!assigned)
                {
                    using (var kill = Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + process.Id)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }))
                    {
                        if (kill != null) kill.WaitForExit(5000);
                    }
                }
                if (!process.HasExited) process.Kill();
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);
    }
}
