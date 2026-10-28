using System;
using System.Threading;

namespace TrFileTransfer
{
    /// <summary>
    /// Keeps the machine from going to sleep while transfers are active. Windows
    /// suspends an idle machine mid-transfer otherwise — the receive side dies
    /// with it, and a half-written checkpoint is the best case.
    ///
    /// SetThreadExecutionState is PER THREAD, so every Begin/End pair must run on
    /// the same long-lived thread. The UI thread wires all pairs (client sends,
    /// fan-out, monitor mode and per-listener server lifetimes); the counter is
    /// still Interlocked-guarded so a mismatched pair cannot wedge the machine
    /// awake forever — an unpaired End clamps at zero.
    /// </summary>
    public static class KeepAwake
    {
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private static int _refs;

        /// <summary>One more activity wants the machine awake; the first caller
        /// arms the execution state.</summary>
        public static void Begin()
        {
            if (Interlocked.Increment(ref _refs) == 1)
                TrySet(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
        }

        /// <summary>Releases one activity; the last one restores normal sleep.</summary>
        public static void End()
        {
            int n = Interlocked.Decrement(ref _refs);
            if (n == 0)
            {
                TrySet(ES_CONTINUOUS);
                return;
            }
            if (n < 0) Interlocked.Exchange(ref _refs, 0); // unpaired End: clamp so the count stays sane
        }

        private static void TrySet(uint flags)
        {
            try { SetThreadExecutionState(flags); }
            catch { }
        }

        /// <summary>Current depth (tests/inspection only).</summary>
        public static int RefCount { get { return Thread.VolatileRead(ref _refs); } }
    }
}
