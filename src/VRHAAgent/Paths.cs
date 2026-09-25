namespace VRHAAgent;

public static class Paths
{
    public const string AppId = "vr-ha-agent";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Xdg(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(value) ? Path.Combine(Home, fallback) : value;
    }

    /// <summary>The name used before the project was renamed from "Home Assistant Agent for SteamVR".</summary>
    public const string LegacyAppId = "steamvr-ha-agent";

    /// <summary>Moves settings from the pre-rename config directory, once.</summary>
    public static void MigrateLegacyConfig()
    {
        var legacy = Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), LegacyAppId);
        if (!Directory.Exists(legacy) || Directory.Exists(ConfigDir)) return;
        try
        {
            Directory.Move(legacy, ConfigDir);
            Log.Info($"Moved settings from {legacy} to {ConfigDir}");
        }
        catch (Exception e)
        {
            Log.Warn($"Could not move settings from {legacy}: {e.Message}");
        }
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
            // When started via `dotnet vr-ha-agent.dll` the process is dotnet itself; prefer the apphost next to the dll.
            if (Path.GetFileNameWithoutExtension(processPath) == "dotnet")
            {
                var appHost = Path.Combine(AppContext.BaseDirectory, AppId);
                if (File.Exists(appHost)) return appHost;
            }

            return processPath;
        }
    }

    /// <summary>The product version, e.g. "0.3.0" or "0.3.0-ci.12" (without the +commit suffix).</summary>
    public static string Version { get; } =
        (System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?")
        .Split('+')[0];

    public static string IconFile => Path.Combine(AppContext.BaseDirectory, "Assets", "icon.png");
}
