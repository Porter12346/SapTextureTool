using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SapTextureTool.Services;

// File-based session logger. Truncates on Init, appends thereafter.
// Path:
//   Windows: %APPDATA%/SapTextureTool/log.txt
//   macOS:   ~/Library/Application Support/SapTextureTool/log.txt
//            (Environment.SpecialFolder.ApplicationData on .NET resolves to ~/.config which
//             is hidden and confusing — use the native location so users can actually find it.)
public static class Logger
{
    private static readonly object _gate = new();
    private static string? _path;

    public static string LogPath => _path ?? "";

    public static string LogDir
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, "Library", "Application Support", "SapTextureTool");
            }
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "SapTextureTool");
        }
    }

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            _path = Path.Combine(LogDir, "log.txt");
            var header = $"=== SapTextureTool session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}" +
                         $"OS: {RuntimeInformation.OSDescription}{Environment.NewLine}" +
                         $"Arch: {RuntimeInformation.OSArchitecture}{Environment.NewLine}" +
                         $"Log: {_path}{Environment.NewLine}";
            File.WriteAllText(_path, header, Encoding.UTF8);
        }
        catch { _path = null; }
    }

    public static void Info(string msg) => Write("INFO ", msg, null);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", msg, ex);

    private static void Write(string level, string msg, Exception? ex)
    {
        if (_path == null) return;
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').Append(level).Append(' ').AppendLine(msg);
            if (ex != null) sb.AppendLine(ex.ToString());
            lock (_gate) File.AppendAllText(_path, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }
}
