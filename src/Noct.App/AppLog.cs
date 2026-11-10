using Noct.Core.Settings;

namespace Noct.App;

/// <summary>
/// A tiny append-only diagnostic log under %LocalAppData%\Noct. It exists so an unexpected error
/// leaves something to read; it records no music metadata beyond what an exception message holds.
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static void Trim()
    {
        try
        {
            var info = new FileInfo(AppPaths.LogFile);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Move(AppPaths.LogFile, AppPaths.LogFile + ".old", overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop the app from starting.
        }
    }

    public static void Write(string message, Exception? exception = null)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{(exception is null ? string.Empty : Environment.NewLine + exception)}{Environment.NewLine}";
                File.AppendAllText(AppPaths.LogFile, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing sensible to do if the log itself cannot be written.
            }
        }
    }
}
