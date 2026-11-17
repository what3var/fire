namespace fire.Editor
{
    /// <summary>Ends the process when spark has shut down but something keeps it alive: .NET waits for every foreground thread before it ends - the
    /// threads of a program that ran in the VM (DebugThreadContext, the `fire` threads of FireRuntime), a device driver's program - so a program that
    /// still waits or loops would keep an invisible spark running after its window closed.</summary>
    internal static class ExitGuard
    {
        /// <summary>How long what is left may take to end by itself.</summary>
        public static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

        /// <summary>Starts a background thread that ends the process with <paramref name="code"/> after <paramref name="grace"/> (it does not keep the
        /// process alive itself: when everything else ends before, the process ends at once). <paramref name="exit"/>: what ends it (tests).</summary>
        public static Thread Arm(int code, TimeSpan grace, Action<int>? exit = null)
        {
            var thread = new Thread(() =>
            {
                Thread.Sleep(grace);
                (exit ?? Environment.Exit)(code);
            }) { IsBackground = true, Name = "spark exit guard" };
            thread.Start();
            return thread;
        }
    }
}
