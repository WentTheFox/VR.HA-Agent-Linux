using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace SteamVRHAAgent;

public class AgentConfig
{
    /// <summary>TCP port of the WebSocket server Home Assistant connects to.</summary>
    public int Port { get; set; } = 8077;

    /// <summary>Address to listen on. "+" listens on all interfaces.</summary>
    public string BindAddress { get; set; } = "+";

    /// <summary>Show only the tray icon (or a minimized window) on start.</summary>
    public bool LaunchMinimized { get; set; }

    /// <summary>Show a tray icon; minimizing the window hides it to the tray.</summary>
    public bool EnableTray { get; set; } = true;

    public bool AlwaysOnTop { get; set; }

    /// <summary>Which VR runtime(s) to connect to: "auto", "steamvr" or "monado".</summary>
    public string Runtime { get; set; } = "auto";

    /// <summary>
    /// Exit when the VR runtime (SteamVR or Monado) quits instead of waiting for it to start again. Off by
    /// default: the agent is meant to run as an always-on user service, since Monado has no way to launch it.
    /// </summary>
    public bool ExitWithSteamVR { get; set; }

    /// <summary>
    /// Shell command run for the start_steamvr command. Null picks a default based on <see cref="Runtime"/>:
    /// SteamVR via Steam, or `systemctl --user start monado.service` for "monado".
    /// </summary>
    public string? StartRuntimeCommand { get; set; }

    /// <summary>Register a SteamVR application manifest so SteamVR starts the agent automatically.</summary>
    public bool AutoLaunchWithSteamVR { get; set; } = true;

    /// <summary>Allow notifications with customProperties.enabled (image overlays with animations).</summary>
    public bool EnableAdvancedNotifications { get; set; } = true;

    /// <summary>Optional explicit path to libopenvr_api.so.</summary>
    public string? OpenVRLibraryPath { get; set; }

    /// <summary>Optional explicit path to libmonado.so.</summary>
    public string? LibMonadoPath { get; set; }

    [JsonIgnore] public bool UseSteamVR => Runtime is "auto" or "steamvr";
    [JsonIgnore] public bool UseMonado => Runtime is "auto" or "monado";

    public bool VerboseLogging { get; set; }

    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
        Formatting = Formatting.Indented,
    };

    public static AgentConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var config = new AgentConfig();
            try
            {
                config.Save(path);
                Log.Info($"Created default config at {path}");
            }
            catch (Exception e)
            {
                Log.Warn($"Could not write default config to {path}: {e.Message}");
            }

            return config;
        }

        return JsonConvert.DeserializeObject<AgentConfig>(File.ReadAllText(path), SerializerSettings) ?? new AgentConfig();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(this, SerializerSettings) + "\n");
    }

    public override string ToString() => JsonConvert.SerializeObject(this, SerializerSettings);
}
