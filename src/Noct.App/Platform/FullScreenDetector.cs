namespace Noct.App.Platform;

/// <summary>
/// Best-effort check for a full-screen app that should not be interrupted by an overlay reveal.
/// </summary>
/// <remarks>
/// Uses the shell's user-notification state, which is the same signal Windows uses to hold back
/// toast notifications. It reports exclusive and most borderless full-screen games and slideshows,
/// but it is not a per-monitor check and can miss some borderless-window cases. That limit is
/// documented, and the island can be switched off in Settings if detection is not enough.
/// </remarks>
internal static class FullScreenDetector
{
    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    public static bool IsFullScreenAppActive()
    {
        try
        {
            if (NativeMethods.SHQueryUserNotificationState(out var state) != 0)
            {
                return false;
            }

            return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
