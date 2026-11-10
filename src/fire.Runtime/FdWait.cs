using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>`__fd_wait(fd, events, ms)` in the VM (docs/ROADMAP_HTTP.md step 0): waits until the socket `fd` of the net package (its library is in this
    /// process) is readable (bit 1) or writable (bit 2), `ms` ran out (0) or the task is cancelled. It asks the system with poll()/WSAPoll in short slices, not
    /// deaf like Sleep (terminate, the main queue, the token); -1 where poll is not there.</summary>
    internal static class FdPoll
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd { public int Fd; public short Events; public short Revents; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WsaPollFd { public nint Fd; public short Events; public short Revents; }

        [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
        private static extern int poll([In, Out] PollFd[] fds, nuint count, int timeout);
        [DllImport("ws2_32", EntryPoint = "WSAPoll", SetLastError = true)]
        private static extern int WSAPoll([In, Out] WsaPollFd[] fds, uint count, int timeout);

        private static bool _broken;

        /// <summary>One look of at most `ms` milliseconds: the ready bits, 0 nothing yet, -1 cannot ask.</summary>
        public static int Once(long fd, int events, int ms)
        {
            if (_broken) return -1;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var a = new[] { new WsaPollFd { Fd = (nint)fd, Events = (short)(((events & 1) != 0 ? 0x100 : 0) | ((events & 2) != 0 ? 0x10 : 0)) } };
                    if (WSAPoll(a, 1, ms) < 0) return 0;
                    int r = a[0].Revents;
                    const int bad = 0x1 | 0x2 | 0x4;   // POLLERR, POLLHUP, POLLNVAL
                    return ((r & (0x100 | 0x200 | bad)) != 0 ? 1 : 0) | ((r & (0x10 | bad)) != 0 ? 2 : 0);
                }
                var p = new[] { new PollFd { Fd = (int)fd, Events = (short)(((events & 1) != 0 ? 0x1 : 0) | ((events & 2) != 0 ? 0x4 : 0)) } };
                if (poll(p, 1, ms) < 0) return 0;
                int rv = p[0].Revents;
                const int err = 0x8 | 0x10 | 0x20;   // POLLERR, POLLHUP, POLLNVAL
                return ((rv & (0x1 | err)) != 0 ? 1 : 0) | ((rv & (0x4 | err)) != 0 ? 2 : 0);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { _broken = true; return -1; }
        }
    }

    public sealed partial class VM
    {
        internal Value FdWaitNative(Value[] a)
        {
            if (a.Length < 3 || a[0].Kind != ValueKind.Int || a[0].AsInt() < 0 || a[1].Kind != ValueKind.Int || (a[1].AsInt() & 3) == 0) return Value.MakeInt(-1);
            long fd = a[0].AsInt();
            int events = (int)(a[1].AsInt() & 3);
            long ms = a[2].Kind == ValueKind.Int ? a[2].AsInt() : a[2].Kind == ValueKind.Float ? (long)a[2].AsFloat() : -1;
            if (ms == 0) return Value.MakeInt(0);
            int first = FdPoll.Once(fd, events, 0);
            if (first != 0) return Value.MakeInt(first);
            long deadline = ms > 0 ? Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000 : 0;
            var token = CurrentToken;
            int held = YieldGlobalsSection();
            bool blocked = PoolTask != null;   // (a task of the pool waits on its worker: another one may start)
            if (blocked) VmScheduler.BlockEnter();
            string? failure = null;
            int result = 0;
            try
            {
                while (true)
                {
                    PollWhileWaiting();
                    if (_stopExecutionRequested) return Value.MakeUndefined();
                    if (CancelledClass() is { } cancelled) { failure = cancelled; break; }
                    long now = Stopwatch.GetTimestamp();
                    if (deadline != 0 && now >= deadline) break;
                    long until = deadline;
                    if (token != null && token.Deadline is var t and not 0 && (until == 0 || t < until)) until = t;
                    // (short slices: the main program serves its queue and windows between them, a thread looks at terminate and its token)
                    int slice = PoolTask == null && !IsFireThreadVm ? 5 : 20;
                    if (until != 0) slice = (int)Math.Clamp((until - now) * 1000 / Stopwatch.Frequency, 1, slice);
                    result = FdPoll.Once(fd, events, slice);
                    if (result != 0) break;
                }
            }
            finally { if (blocked) VmScheduler.BlockLeave(); RegainGlobalsSection(held); }
            if (failure != null) return NativeFail(failure, failure == "TimeoutException" ? "The time limit ran out." : "The task was cancelled.");
            return Value.MakeInt(result);
        }
    }
}
