using System.Runtime.InteropServices;

namespace NodeRunner.App;

// Waits for a short time more precisely than Thread.Sleep (whose step is about 15 ms by default), with Windows'
// high-resolution timer (Windows 10 1803 and later); Thread.Sleep on older systems.
internal static partial class PreciseSleep
{
    private const uint HighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    private static readonly IntPtr Timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, HighResolution, TimerAllAccess);

    public static void For(double seconds)
    {
        if (seconds <= 0)
            return;
        long due = -(long)(seconds * 10_000_000.0); // Relative, in 100 ns steps.
        int milliseconds = (int)Math.Ceiling(seconds * 1000.0);
        if (Timer == IntPtr.Zero || due == 0 || !SetWaitableTimer(Timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            Thread.Sleep(milliseconds);
            return;
        }
        WaitForSingleObject(Timer, (uint)milliseconds + 50);
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr routine, IntPtr argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
