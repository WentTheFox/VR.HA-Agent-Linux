using Newtonsoft.Json;
using Valve.VR;

// Property names are part of the WebSocket protocol used by the Home Assistant integration.
// ReSharper disable InconsistentNaming

namespace SteamVRHAAgent.Protocol;

public class Response(string nonce, bool success = true, string errorCode = "", string errorMessage = "")
{
    public string nonce = nonce;
    public string type = "result";
    public bool success = success;
    public Error error = new() { code = errorCode, message = errorMessage };

    public class Error
    {
        public string code = "";
        public string message = "";
    }
}

public class Event(string eventType, string eventData)
{
    [JsonProperty("type")] public string Type = "event";
    [JsonProperty("event_type")] public string EventType = eventType;
    [JsonProperty("event_data")] public string EventData = eventData;
}

public class State
{
    [JsonProperty("type")] public string Type => "state";

    [JsonProperty("is_steamvr_process_running")]
    public bool IsSteamVRProcessRunning { get; init; }

    [JsonProperty("is_openvr_connected")] public bool IsOpenVRConnected { get; init; }

    [JsonProperty("hmd_activity_level")]
    public EDeviceActivityLevel HmdActivityLevel { get; init; } = EDeviceActivityLevel.k_EDeviceActivityLevel_Unknown;

    [JsonProperty("current_application_key")]
    public string? CurrentApplicationKey { get; init; }

    [JsonProperty("current_application_name")]
    public string? CurrentApplicationName { get; init; }

    [JsonProperty("right_controller")] public Controller? RightController { get; init; }
    [JsonProperty("left_controller")] public Controller? LeftController { get; init; }
}

public class Controller(bool isConnected = false, int? batteryPercentage = null, bool? isCharging = null)
{
    [JsonProperty("is_connected")] public bool IsConnected { get; } = isConnected;
    [JsonProperty("battery_percentage")] public int? BatteryPercentage { get; } = batteryPercentage;
    [JsonProperty("is_charging")] public bool? IsCharging { get; } = isCharging;
}
