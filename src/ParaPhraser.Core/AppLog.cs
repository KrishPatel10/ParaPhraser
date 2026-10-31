using System.Globalization;

namespace ParaPhraser.Core;

/// <summary>
/// A small local log for diagnosing failures. It records what happened, never
/// what was written: no selected text, model replies, window titles, or API keys.
/// </summary>
public static class AppLog
{
    private const long MaxFileBytes = 1024 * 1024;

    private static readonly object Gate = new();

    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ParaPhraser",
        "logs",
        "paraphraser.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    /// <summary>Logging must never be the reason a rewrite fails.</summary>
    private static void Write(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                RotateIfFull();
                File.AppendAllText(FilePath, $"{timestamp} {level} {message}{Environment.NewLine}");
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
            }
        }
    }

    // ponytail: one previous file, so the log stays near 2 MB. Add dated files if support needs more history.
    private static void RotateIfFull()
    {
        var file = new FileInfo(FilePath);
        if (file.Exists && file.Length >= MaxFileBytes)
        {
            File.Move(FilePath, Path.ChangeExtension(FilePath, ".previous.log"), overwrite: true);
        }
    }
}
