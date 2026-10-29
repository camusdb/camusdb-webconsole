using System.Diagnostics;

namespace CamusDB.WebConsole.Hosting;

/// <summary>Opens the console in the user's default browser when the tool starts.</summary>
public static class BrowserLauncher
{
    /// <summary>
    /// Opens <paramref name="url"/>. A machine with no browser, such as a server over SSH, only gets a
    /// log line, because the console still works from another machine's browser through a tunnel.
    /// </summary>
    public static void TryOpen(string url, ILogger logger)
    {
        try
        {
            ProcessStartInfo start = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open", [url])
                : OperatingSystem.IsLinux()
                    ? new ProcessStartInfo("xdg-open", [url])
                    : new ProcessStartInfo(url) { UseShellExecute = true };

            start.RedirectStandardOutput = !start.UseShellExecute;
            start.RedirectStandardError = !start.UseShellExecute;

            using Process? process = Process.Start(start);
        }
        catch (Exception ex)
        {
            logger.LogInformation("Could not open a browser ({Reason}). Open {Url} yourself.", ex.Message, url);
        }
    }
}
