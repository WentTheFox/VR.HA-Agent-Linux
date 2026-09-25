namespace SteamVRHAAgent;

public static class Paths
{
    public const string AppId = "steamvr-ha-agent";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Xdg(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(value) ? Path.Combine(Home, fallback) : value;
    }

    public static string ConfigDir => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), AppId);
    public static string DataDir => Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), AppId);

    public static string RuntimeDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return string.IsNullOrEmpty(dir) ? Path.GetTempPath() : dir;
        }
    }

    public static string DefaultConfigFile => Path.Combine(ConfigDir, "config.json");
    public static string ManifestFile => Path.Combine(DataDir, AppId + ".vrmanifest");
    public static string LauncherScript => Path.Combine(DataDir, "launch-from-steamvr.sh");
    public static string LockFile => Path.Combine(RuntimeDir, AppId + ".lock");

    /// <summary>Absolute path of the native executable (apphost) that is running us.</summary>
    public static string Executable
    {
        get
        {
            var processPath = Environment.ProcessPath ?? "";
            // When started via `dotnet steamvr-ha-agent.dll` the process is dotnet itself; prefer the apphost next to the dll.
            if (Path.GetFileNameWithoutExtension(processPath) == "dotnet")
            {
                var appHost = Path.Combine(AppContext.BaseDirectory, AppId);
                if (File.Exists(appHost)) return appHost;
            }

            return processPath;
        }
    }

    public static string IconFile => Path.Combine(AppContext.BaseDirectory, "Assets", "icon.png");
}
