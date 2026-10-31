using System.Text;
using System.Text.RegularExpressions;

namespace SevenZipDnD;

internal static class AppLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string? _path;

    public static string? Path => _path;

    public static string Initialize()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "7z-DnD",
            "logs");
        Directory.CreateDirectory(dir);

        _path = System.IO.Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(_path, append: false, Encoding.UTF8) { AutoFlush = true };
        Write($"=== 7z-DnD start pid={Environment.ProcessId} ===");
        return _path;
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {SanitizeForLog(message)}";
        lock (Gate)
        {
            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // ignore disk errors
            }
        }
    }

    public static void WriteException(string context, Exception ex)
    {
        Write($"{context}: {ex}");
    }

    /// <summary>
    /// Redact -pPASSWORD fragments that may leak into 7z output or process args.
    /// </summary>
    public static string SanitizeForLog(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return Regex.Replace(text, @"-p("".*?""|\S+)", "-p***", RegexOptions.IgnoreCase);
    }
}
