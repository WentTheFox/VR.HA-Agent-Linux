using System.Collections.Concurrent;
using System.Diagnostics;
using Newtonsoft.Json;
using SteamVRHAAgent.Protocol;
using SteamVRHAAgent.VR;
using Valve.VR;

namespace SteamVRHAAgent;

public sealed class Agent : IDisposable
{
    private static readonly TimeSpan StateInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Processes that mean a VR runtime is up. /proc comm names are truncated to 15 characters.
    /// </summary>
    private static readonly string[] RuntimeProcessNames =
        ["vrserver", "vrmonitor", "vrcompositor", "monado-service", "wivrn-server"];

    private readonly AgentConfig _config;
    private readonly VRRuntime _vr = new();
    private readonly WebSocketServer _server;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<EVREventType, ConcurrentDictionary<string, WebSocketClient>> _eventSubscriptions = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimeSpan _nextState = TimeSpan.Zero;
    private volatile bool _runtimeProcessRunning;

    public Agent(AgentConfig config)
    {
        _config = config;
        _server = new WebSocketServer(config.BindAddress, config.Port)
        {
            MessageReceived = HandleMessageAsync,
            ClientDisconnected = OnClientDisconnected,
        };

        _vr.Connected += OnVRConnected;
        _vr.Disconnected += OnVRDisconnected;
        _vr.EventReceived += OnVREvent;
        _vr.Tick += now =>
        {
            _vr.Advanced.Update(now);
            if (now >= _nextState) BroadcastState(now);
        };
    }

    /// <summary>Completes when the agent decides to exit on its own (SteamVR quit with ExitWithSteamVR).</summary>
    public Task Exited => _exited.Task;

    public void Start()
    {
        _server.Start(_shutdown.Token);
        _vr.Start();
        _ = ProcessWatchLoop(_shutdown.Token);
    }

    public async Task StopAsync()
    {
        if (_shutdown.IsCancellationRequested) return;
        await _shutdown.CancelAsync();
        await _server.StopAsync();
        _vr.Dispose();
    }

    public void Dispose()
    {
        _vr.Dispose();
        _shutdown.Dispose();
    }

    #region State

    private static bool IsRuntimeProcessRunning()
    {
        foreach (var name in RuntimeProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            var found = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            if (found) return true;
        }

        return false;
    }

    /// <summary>
    /// Runs off the VR thread, since scanning /proc is slow. While OpenVR is disconnected there are no VR
    /// ticks, so this loop also sends the state updates.
    /// </summary>
    private async Task ProcessWatchLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(StateInterval);
        var sawProcessWhileConnected = false;
        var missingCount = 0;
        try
        {
            do
            {
                _runtimeProcessRunning = IsRuntimeProcessRunning();
                if (!_vr.IsConnected)
                {
                    sawProcessWhileConnected = false;
                    _server.Broadcast(Serialize(new State { IsSteamVRProcessRunning = _runtimeProcessRunning }));
                    continue;
                }

                // A crashed or killed runtime never sends VREvent_Quit, so notice it going away ourselves.
                if (_runtimeProcessRunning)
                {
                    sawProcessWhileConnected = true;
                    missingCount = 0;
                }
                else if (sawProcessWhileConnected && ++missingCount >= 3)
                {
                    Log.Warn("VR runtime process disappeared without quitting");
                    sawProcessWhileConnected = false;
                    await _vr.InvokeAsync(() => _vr.Disconnect(acknowledgeQuit: false));
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void BroadcastState(TimeSpan now)
    {
        _nextState = now + StateInterval;
        if (_server.ClientCount == 0) return;
        try
        {
            _server.Broadcast(Serialize(_vr.GetState(_runtimeProcessRunning)));
        }
        catch (Exception e)
        {
            Log.Warn($"Failed to read VR state: {e.Message}");
        }
    }

    #endregion

    #region VR lifecycle (VR thread)

    private void OnVRConnected()
    {
        _nextState = TimeSpan.Zero;
        if (_config.AutoLaunchWithSteamVR) RegisterManifest();
        else UnregisterManifest();
    }

    private void OnVRDisconnected()
    {
        _server.Broadcast(Serialize(new State { IsSteamVRProcessRunning = _runtimeProcessRunning }));
        if (_config.ExitWithSteamVR)
        {
            Log.Info("Exiting because the VR runtime quit (exitWithSteamVR is enabled)");
            _exited.TrySetResult();
        }
    }

    private void OnVREvent(VREvent_t ev)
    {
        var type = (EVREventType)ev.eventType;
        if (!_eventSubscriptions.TryGetValue(type, out var subscribers) || subscribers.IsEmpty) return;

        var message = Serialize(new Event(type.ToString(), ev.trackedDeviceIndex.ToString()));
        foreach (var client in subscribers.Values) _ = client.SendAsync(message);
    }

    private void RegisterManifest()
    {
        try
        {
            SteamVRManifest.Write();
            var error = _vr.RegisterManifest(Paths.ManifestFile, autoLaunch: true);
            if (error == EVRApplicationError.None)
                Log.Info($"Registered SteamVR auto-launch manifest: {Paths.ManifestFile}");
            else
                Log.Warn($"Could not register SteamVR manifest: {error}");
        }
        catch (Exception e)
        {
            Log.Warn($"Could not register SteamVR manifest: {e.Message}");
        }
    }

    private void UnregisterManifest()
    {
        if (!File.Exists(Paths.ManifestFile)) return;
        try
        {
            var error = _vr.UnregisterManifest(Paths.ManifestFile);
            if (error == EVRApplicationError.None) Log.Info("Removed SteamVR auto-launch manifest");
        }
        catch (Exception e)
        {
            Log.Warn($"Could not unregister SteamVR manifest: {e.Message}");
        }
    }

    #endregion

    #region Messages

    private void OnClientDisconnected(WebSocketClient client)
    {
        foreach (var subscribers in _eventSubscriptions.Values) subscribers.TryRemove(client.Id, out _);
    }

    private void Reply(WebSocketClient client, string nonce, bool success = true, string code = "", string message = "")
    {
        if (!success) Log.Warn($"{code}: {message}");
        _server.Send(client, Serialize(new Response(nonce, success, code, message)));
    }

    private async Task HandleMessageAsync(WebSocketClient client, string json)
    {
        Payload payload;
        try
        {
            payload = JsonConvert.DeserializeObject<Payload>(json) ?? throw new JsonException("Empty payload");
            payload.customProperties ??= new Payload.CustomProperties();
        }
        catch (Exception e)
        {
            Reply(client, "", false, "json_parse_error", $"JSON Parsing Exception: {e.Message}");
            return;
        }

        var nonce = payload.customProperties.nonce;
        Log.Debug($"Received {payload.type} {payload.command} from {client.Remote}");

        // Works without an OpenVR connection.
        if (payload is { type: "command", command: "start_steamvr" })
        {
            try
            {
                StartSteamVR();
                Reply(client, nonce);
            }
            catch (Exception e)
            {
                Reply(client, nonce, false, "start_steamvr_error", e.Message);
            }

            return;
        }

        if (!_vr.IsConnected)
        {
            Reply(client, nonce, false, "steamvr_disconnected", "SteamVR is disconnected");
            return;
        }

        switch (payload.type)
        {
            case "notification" when payload.customProperties.enabled:
                await PostAdvancedNotification(client, payload);
                break;
            case "notification" when !string.IsNullOrEmpty(payload.basicMessage):
                await PostNotification(client, payload);
                break;
            case "notification":
                Reply(client, nonce, false, "invalid_notification",
                    "Custom notification is not enabled and basic message is empty");
                break;
            case "command":
                await HandleCommand(client, payload);
                break;
            case "register_event" or "unregister_event" when string.IsNullOrEmpty(payload.command):
                Reply(client, nonce, false, "no_event_command", "No event type specified");
                break;
            case "register_event":
                if (!Enum.TryParse<EVREventType>(payload.command, out var registerType))
                {
                    Reply(client, nonce, false, "register_event_error", $"Unknown event type '{payload.command}'");
                    break;
                }

                _eventSubscriptions.GetOrAdd(registerType, _ => new())[client.Id] = client;
                Log.Info($"Registered event {registerType} for {client.Remote}");
                Reply(client, nonce);
                break;
            case "unregister_event":
                if (!Enum.TryParse<EVREventType>(payload.command, out var unregisterType))
                {
                    Reply(client, nonce, false, "unregister_event_error", $"Unknown event type '{payload.command}'");
                    break;
                }

                if (_eventSubscriptions.TryGetValue(unregisterType, out var subscribers))
                    subscribers.TryRemove(client.Id, out _);
                Reply(client, nonce);
                break;
            default:
                Reply(client, nonce, false, "invalid_type", "Invalid payload type");
                break;
        }
    }

    private async Task HandleCommand(WebSocketClient client, Payload payload)
    {
        var nonce = payload.customProperties.nonce;
        ETrackedControllerRole[] roles = payload.command switch
        {
            "vibrate_controller_right" => [ETrackedControllerRole.RightHand],
            "vibrate_controller_left" => [ETrackedControllerRole.LeftHand],
            "vibrate_controller_both" => [ETrackedControllerRole.RightHand, ETrackedControllerRole.LeftHand],
            _ => [],
        };

        if (roles.Length == 0)
        {
            Reply(client, nonce, false, "invalid_command", $"Unknown command '{payload.command}'");
            return;
        }

        // Legacy haptic pulses max out at ~4ms, so repeat them to get a noticeable buzz.
        for (var i = 0; i < 20; i++)
        {
            await _vr.InvokeAsync(() =>
            {
                foreach (var role in roles) _vr.TriggerHapticPulse(role, 3999);
            });
            await Task.Delay(5);
        }

        Reply(client, nonce);
    }

    private async Task PostNotification(WebSocketClient client, Payload payload)
    {
        var nonce = payload.customProperties.nonce;
        Rgba32Image? image = null;
        try
        {
            using var loaded = await Images.LoadAsync(payload);
            if (loaded != null) image = Images.ToRaw(loaded, 512);
        }
        catch (Exception e)
        {
            // Like the original agent: report the image problem but still show the text.
            Reply(client, nonce, false, "image_read_error", $"Image Read Failure: {e.Message}");
        }

        var error = await _vr.InvokeAsync(() => _vr.ShowNotification(payload.basicTitle, payload.basicMessage, image));
        if (error != null) Reply(client, nonce, false, "notification_error", error);
        else if (image != null || !HasImage(payload)) Reply(client, nonce);
    }

    private async Task PostAdvancedNotification(WebSocketClient client, Payload payload)
    {
        var nonce = payload.customProperties.nonce;
        if (!_config.EnableAdvancedNotifications)
        {
            Reply(client, nonce, false, "notify_plugin_disabled", "Advanced notifications are disabled in the agent config");
            return;
        }

        Rgba32Image image;
        try
        {
            using var loaded = await Images.LoadAsync(payload);
            if (loaded == null && payload.customProperties.textAreas.Length == 0)
            {
                Reply(client, nonce, false, "image_read_error", "Advanced notifications need an image or text areas");
                return;
            }

            using var canvas = loaded ?? BlankCanvasFor(payload.customProperties.textAreas);
            Images.DrawTextAreas(canvas, payload.customProperties.textAreas);
            image = Images.ToRaw(canvas, AdvancedNotifications.MaxImageDimension);
        }
        catch (Exception e)
        {
            Reply(client, nonce, false, "image_read_error", $"Image Read Failure: {e.Message}");
            return;
        }

        var error = await _vr.InvokeAsync(() => _vr.Advanced.Enqueue(payload.customProperties, image));
        if (error != null) Reply(client, nonce, false, "notification_error", error);
        else Reply(client, nonce);
    }

    private static bool HasImage(Payload p) =>
        !string.IsNullOrEmpty(p.imageData) || !string.IsNullOrEmpty(p.imagePath) || !string.IsNullOrEmpty(p.imageUrl);

    private static SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> BlankCanvasFor(
        IEnumerable<Payload.TextArea> areas)
    {
        var width = Math.Clamp(areas.Max(a => a.xPositionPx + a.widthPx), 1, AdvancedNotifications.MaxImageDimension);
        var height = Math.Clamp(areas.Max(a => a.yPositionPx + a.heightPx), 1, AdvancedNotifications.MaxImageDimension);
        return new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
    }

    private static void StartSteamVR()
    {
        // UseShellExecute goes through xdg-open, which hands steam:// URLs to the Steam client.
        Process.Start(new ProcessStartInfo("steam://rungameid/250820") { UseShellExecute = true })?.Dispose();
    }

    #endregion

    private static string Serialize(object value) => JsonConvert.SerializeObject(value);
}
