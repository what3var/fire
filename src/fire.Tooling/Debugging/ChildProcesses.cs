using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace fire.Debugging
{
    /// <summary>
    /// The child processes of a host that must not outlive it (spark: its live sessions and runs in `fire`, forge, `forge serve`, native programs). Off until the
    /// host calls <see cref="TieToThisProcess"/>; forge and the tests leave it off.
    /// <list type="bullet">
    /// <item>Its end: when the process ends (<c>ProcessExit</c>, and SIGTERM/SIGHUP on Unix, which end it), each child that still runs is killed with its tree.</item>
    /// <item>Linux, also when the host is killed (SIGKILL, a crash): a child gets <see cref="EnvironmentVariable"/> with the host's pid, and `fire` asks the kernel
    /// for a signal when its parent ends (<c>prctl(PR_SET_PDEATHSIG)</c>, native/vm/fire.c) and ends then.</item>
    /// <item>Windows: every child goes into a job object that kills what is in it when its last handle closes - at the end of the host, however it ends (the
    /// children of a child are in it too).</item>
    /// </list>
    /// </summary>
    public static class ChildProcesses
    {
        /// <summary>The pid of the host for a child: `fire` ends with that process (only while it is the parent of `fire`: a grandchild that inherits the
        /// variable does not take it).</summary>
        public const string EnvironmentVariable = "FIRE_END_WITH_PARENT";

        private static readonly object Gate = new();
        private static readonly List<Process> Children = new();
        private static readonly List<PosixSignalRegistration> Signals = new();
        private static bool _tied;
        private static IntPtr _job;

        /// <summary>Whether the children of this process end with it (<see cref="TieToThisProcess"/> was called).</summary>
        public static bool Tied { get { lock (Gate) return _tied; } }

        /// <summary>From now on the children started through <see cref="Prepare"/>/<see cref="Add"/> end with this process.</summary>
        public static void TieToThisProcess()
        {
            lock (Gate)
            {
                if (_tied) return;
                _tied = true;
                if (OperatingSystem.IsWindows()) _job = CreateKillOnCloseJob();
                else
                    foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP })
                        try { Signals.Add(PosixSignalRegistration.Create(signal, _ => EndAll())); }   // (not cancelled: the signal still ends the process)
                        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException) { }
            }
            AppDomain.CurrentDomain.ProcessExit += (_, _) => EndAll();
        }

        /// <summary>For a child about to start: the variable that ties `fire` to this process (when tied).</summary>
        public static void Prepare(ProcessStartInfo info)
        {
            if (Tied) info.Environment[EnvironmentVariable] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>A child that has started: it ends with this process (when tied). Returns the process.</summary>
        public static Process Add(Process process)
        {
            lock (Gate)
            {
                if (!_tied) return process;
                Children.RemoveAll(Ended);
                Children.Add(process);
                if (OperatingSystem.IsWindows() && _job != IntPtr.Zero)
                    try { AssignProcessToJobObject(_job, process.Handle); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return process;
        }

        /// <summary>Kills every child that still runs, with its tree (the end of the host).</summary>
        public static void EndAll()
        {
            Process[] children;
            lock (Gate)
            {
                children = Children.ToArray();
                Children.Clear();
            }
            foreach (var p in children)
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }   // (ended, disposed)
        }

        private static bool Ended(Process p)
        {
            try { return p.HasExited; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return true; }
        }

        // ---- Windows: a job object that kills its processes when it closes ----

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformationData
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformationData info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        /// <summary>The job of the children (its handle stays open for the life of this process: the system closes it at the end, and the job kills them).
        /// Zero when the system does not give one (then only <see cref="EndAll"/>).</summary>
        private static IntPtr CreateKillOnCloseJob()
        {
            try
            {
                IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;
                var info = new JobObjectExtendedLimitInformationData();
                // (a child may still start a process outside the job on purpose: CREATE_BREAKAWAY_FROM_JOB)
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK;
                return SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationData>()) ? job : IntPtr.Zero;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return IntPtr.Zero; }
        }
    }
}
