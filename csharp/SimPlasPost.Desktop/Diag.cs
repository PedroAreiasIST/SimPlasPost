namespace SimPlasPost.Desktop;

/// <summary>
/// Process-wide diagnostic logger: writes every line to ~/simplaspost-gl.log
/// and to stderr. Used to trace startup so we can pinpoint where a native
/// crash (SIGSEGV) happens — the LAST line written before the process dies
/// identifies the offender.
/// </summary>
public static class Diag
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "simplaspost-gl.log");

    static Diag()
    {
        try
        {
            // Truncate at startup so each run is its own log.
            File.WriteAllText(LogPath, "");
        }
        catch { /* ignore */ }
    }

    public static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff} t{Environment.CurrentManagedThreadId}] {msg}";
        try { Console.Error.WriteLine(line); } catch { /* ignore */ }
        try { File.AppendAllText(LogPath, line + Environment.NewLine); }
        catch { /* ignore */ }
    }
}
