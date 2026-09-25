namespace SteamVRHAAgent;

public static class Log
{
    public static bool Verbose { get; set; }

    // journald picks up the <N> syslog priority prefix when running under systemd.
    private static readonly bool UseSyslogPrefix = IsStdoutJournal();
    private static readonly Lock WriteLock = new();

    public static void Debug(string message)
    {
        if (Verbose) Write(7, "DEBUG", message);
    }

    public static void Info(string message) => Write(6, "INFO", message);
    public static void Warn(string message) => Write(4, "WARN", message);
    public static void Error(string message) => Write(3, "ERROR", message);

    /// <summary>JOURNAL_STREAM is inherited by children, so check that stdout really is that journal socket.</summary>
    private static bool IsStdoutJournal()
    {
        var stream = Environment.GetEnvironmentVariable("JOURNAL_STREAM");
        if (string.IsNullOrEmpty(stream)) return false;
        try
        {
            var inode = stream.Split(':').Last();
            return new FileInfo("/proc/self/fd/1").LinkTarget == $"socket:[{inode}]";
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Write(int priority, string level, string message)
    {
        var line = UseSyslogPrefix
            ? $"<{priority}>{message}"
            : $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {message}";
        lock (WriteLock)
        {
            (priority <= 4 ? Console.Error : Console.Out).WriteLine(line);
        }
    }
}
