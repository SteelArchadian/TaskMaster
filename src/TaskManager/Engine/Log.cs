namespace TaskManager.Engine;

/// <summary>Small append-only log in %LOCALAPPDATA%\TaskManager\logs. Errors are recorded, never swallowed silently.</summary>
public static class Log
{
    private static readonly object s_lock = new();
    private static readonly string s_path = System.IO.Path.Combine(Settings.Folder, "logs", "app.log");
    public static string FilePath => s_path;

    public static void Error(string where, Exception ex) => Write($"ERROR {where}: {ex}");

    public static void Info(string message) => Write($"INFO {message}");

    private static void Write(string line)
    {
        try
        {
            lock (s_lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(s_path)!);
                var fi = new FileInfo(s_path);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(s_path, s_path + ".old", overwrite: true);
                File.AppendAllText(s_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* logging must never take the app down */ }
        catch (UnauthorizedAccessException) { }
    }
}
