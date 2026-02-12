using System.IO;

namespace Jellyfin.Plugin.MetadataRenamer.Services;

/// <summary>
/// Writes debug log lines only when the log directory exists, avoiding DirectoryNotFoundException.
/// Uses a path under the system temp directory to avoid hardcoded absolute paths.
/// </summary>
internal static class DebugLogHelper
{
    private static readonly string DebugLogPath = Path.Combine(Path.GetTempPath(), "MetadataRenamer", "debug.log");

    public static void SafeAppend(string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(DebugLogPath);
            if (!string.IsNullOrEmpty(dir))
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(DebugLogPath, content);
            }
        }
        catch
        {
            // Intentionally ignore to avoid impacting plugin load or event handling (debug instrumentation only).
        }
    }
}
