using QuotaArc.Providers;

namespace QuotaArc;

internal static class Log
{
    // `Debug.WriteLine` alone is a no-op in Release, which is exactly how the
    // notch's startup crash used to vanish without a trace: the handler in
    // App.xaml.cs called `Log.Error` on the way down and there was nowhere
    // anyone could ever read it back from. A file gives a Release build
    // somewhere real to look.
    private static readonly object Gate = new();

    // A diagnostic trail, not an audit log — once it hits this size the
    // oldest lines are worth less than the disk space, so it just restarts.
    private const long MaxBytes = 1_000_000;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppBranding.ExeName,
        "quotaarc.log");

    public static void Info(string message) => Write("quotaarc", message);

    public static void Error(string message) => Write("quotaarc:error", message);

    private static void Write(string tag, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (dir is not null) Directory.CreateDirectory(dir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Delete(FilePath);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // This is diagnostic logging for exceptions the app already
            // swallows elsewhere — a locked file, a missing profile folder,
            // or a permissions error here must never become a new one.
        }
    }
}

internal static class Launch
{
    public static bool App(string name)
    {
        try
        {
            var candidates = name.ToLowerInvariant() switch
            {
                "cursor" => new[] { "cursor", @"Cursor\Cursor.exe" },
                "codex" => new[] { "codex" },
                "antigravity" => new[] { "Antigravity", "antigravity" },
                _ => new[] { name }
            };
            foreach (var c in candidates)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = c,
                        UseShellExecute = true
                    });
                    return true;
                }
                catch { /* try next */ }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"launch {name}: {ex.Message}");
        }
        return false;
    }
}
