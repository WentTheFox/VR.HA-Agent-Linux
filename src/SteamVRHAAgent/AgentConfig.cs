using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace SteamVRHAAgent;

public class AgentConfig
{
    /// <summary>TCP port of the WebSocket server Home Assistant connects to.</summary>
    public int Port { get; set; } = 8077;

    /// <summary>Address to listen on. "+" listens on all interfaces.</summary>
    public string BindAddress { get; set; } = "+";

    /// <summary>Exit when SteamVR quits instead of waiting for it to start again.</summary>
    public bool ExitWithSteamVR { get; set; } = true;

    /// <summary>Register a SteamVR application manifest so SteamVR starts the agent automatically.</summary>
    public bool AutoLaunchWithSteamVR { get; set; } = true;

    /// <summary>Allow notifications with customProperties.enabled (image overlays with animations).</summary>
    public bool EnableAdvancedNotifications { get; set; } = true;

    /// <summary>Optional explicit path to libopenvr_api.so.</summary>
    public string? OpenVRLibraryPath { get; set; }

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
