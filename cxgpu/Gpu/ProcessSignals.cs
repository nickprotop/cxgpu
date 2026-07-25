using System.Diagnostics;
using System.Runtime.InteropServices;

namespace cxgpu.Gpu;

/// <summary>
/// Delivers process signals, shared by the backends that support it.
///
/// Lives here rather than in the UI because signalling is a BACKEND capability: which mechanism can
/// stop a process is a property of the vendor and platform, not of the view. Keeping it out of the
/// tab also means the demo backend's refusal cannot be bypassed by a UI code path.
/// </summary>
internal static class ProcessSignals
{
    // POSIX kill(2). Needed because .NET has no portable "send SIGTERM":
    // Process.CloseMainWindow() is a no-op for a headless GPU compute job (there is no window) and
    // Process.Kill() is SIGKILL — so neither can deliver the graceful stop SIGTERM exists for.
    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int PosixKill(int pid, int sig);

    private const int SIGTERM = 15;

    // errno values checked after a failed kill(2).
    private const int EPERM = 1;
    private const int ESRCH = 3;

    /// <summary>
    /// Sends <paramref name="signal"/> to <paramref name="pid"/> and reports what actually happened.
    ///
    /// Never assumes success. On a shared GPU box "belongs to another user" (EPERM) and "already
    /// exited" (ESRCH) are both ordinary outcomes that mean different things to the operator, so they
    /// are returned distinctly rather than collapsed into a generic failure.
    /// </summary>
    public static GpuSignalResult Send(int pid, GpuSignal signal)
    {
        try
        {
            // Windows has no SIGTERM; a forced terminate is the only option there, and Process.Kill
            // is that. On POSIX only Kill maps to Process.Kill (SIGKILL).
            if (signal == GpuSignal.Kill || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
                return GpuSignalResult.Ok;
            }

            if (PosixKill(pid, SIGTERM) == 0)
                return GpuSignalResult.Ok;

            return Marshal.GetLastWin32Error() switch
            {
                EPERM => GpuSignalResult.PermissionDenied,
                ESRCH => GpuSignalResult.NoSuchProcess,
                _ => GpuSignalResult.Failed
            };
        }
        catch (ArgumentException)
        {
            // Process.GetProcessById throws this when the pid is gone.
            return GpuSignalResult.NoSuchProcess;
        }
        catch (UnauthorizedAccessException)
        {
            return GpuSignalResult.PermissionDenied;
        }
        catch
        {
            return GpuSignalResult.Failed;
        }
    }
}
