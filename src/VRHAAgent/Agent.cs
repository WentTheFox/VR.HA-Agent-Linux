using System.Collections.Concurrent;
using System.Diagnostics;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using VRHAAgent.Monado;
using VRHAAgent.Protocol;
using VRHAAgent.VR;
using Valve.VR;

namespace VRHAAgent;

/// <summary>
/// Bridges Home Assistant's WebSocket protocol to whichever VR runtime is running: SteamVR through OpenVR,
/// or Monado through libmonado (with WayVR for notifications and haptics).
/// </summary>
public sealed class Agent : IDisposable
{
    private static readonly TimeSpan StateInterval = TimeSpan.FromSeconds(1);

    private readonly AgentConfig _config;
    private readonly VRRuntime _vr = new();
    private readonly MonadoBackend _monado;
    private readonly WebSocketServer _server;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<EVREventType, ConcurrentDictionary<string, WebSocketClient>> _eventSubscriptions = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimeSpan _nextState = TimeSpan.Zero;
    private volatile bool _runtimeProcessRunning;
    private volatile bool _wayVRRunning;
    private volatile HeadsetDisplayState _headsetDisplay = new();
    private bool _headsetDisplayReported;

    public Agent(AgentConfig config)
    {
        _config = config;
        _monado = new MonadoBackend(config.LibMonadoPath);
        _server = new WebSocketServer(config.BindAddress, config.Port)
        {
            MessageReceived = HandleMessageAsync,
            ClientConnected = OnClientConnected,
            ClientDisconnected = OnClientDisconnected,
        };

        _vr.Connected += OnVRConnected;
        _vr.Disconnected += () => OnRuntimeStopped("SteamVR");
        _vr.EventReceived += OnVREvent;
        _vr.Tick += now =>
        {
            _vr.Advanced.Update(now);
            if (now >= _nextState) BroadcastVRState(now);
        };
        _monado.Disconnected += () => OnRuntimeStopped("Monado");
    }

    /// <summary>Completes when the agent decides to exit on its own (runtime quit with ExitWithSteamVR).</summary>
    public Task Exited => _exited.Task;

    public AgentConfig Config => _config;

    /// <summary>"SteamVR", "Monado" or null.</summary>
    public string? ConnectedRuntime => _vr.IsConnected ? "SteamVR" : _monado.IsConnected ? "Monado" : null;

    public bool IsServerListening => _server.IsListening;
    public int ClientCount => _server.ClientCount;

    public HeadsetDisplayState HeadsetDisplayState => _headsetDisplay;

    public void Start()
    {
        _server.Start(_shutdown.Token);
        _vr.Start();
        _ = WatchLoop(_shutdown.Token);
    }

    public async Task StopAsync()
    {
        if (_shutdown.IsCancellationRequested) return;
        await _shutdown.CancelAsync();
        await _server.StopAsync();
        _vr.Dispose();
        _monado.Dispose();
    }

    public void Dispose()
    {
        _vr.Dispose();
        _monado.Dispose();
        _shutdown.Dispose();
    }

    #region Runtime detection and state

    /// <summary>
    /// Once a second: scan processes, connect/disconnect backends accordingly, and send state updates
    /// (except while OpenVR is connected, when the VR thread sends them).
    /// </summary>
    private async Task WatchLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(StateInterval);
        var sawSteamVRWhileConnected = false;
        var steamVRMissingCount = 0;
        try
        {
            do
            {
                var processes = RuntimeProcesses.Scan();
                _runtimeProcessRunning = processes.AnyRuntime;
                UpdateHeadsetDisplay();
                _wayVRRunning = processes.WayVR;
                _vr.ConnectAllowed = _config.UseSteamVR && processes.SteamVR;

                // The runtime setting can change at any time from the settings window.
                if (_vr.IsConnected && !_config.UseSteamVR)
                    await _vr.InvokeAsync(() => _vr.Disconnect(acknowledgeQuit: false, raiseEvent: false));
                if (_monado.IsConnected && !_config.UseMonado) _monado.Close();

                if (_vr.IsConnected)
                {
                    // A crashed or killed SteamVR never sends VREvent_Quit, so notice it going away ourselves.
                    if (processes.SteamVR)
                    {
                        sawSteamVRWhileConnected = true;
                        steamVRMissingCount = 0;
                    }
                    else if (sawSteamVRWhileConnected && ++steamVRMissingCount >= 3)
                    {
                        Log.Warn("SteamVR process disappeared without quitting");
                        sawSteamVRWhileConnected = false;
                        await _vr.InvokeAsync(() => _vr.Disconnect(acknowledgeQuit: false));
                    }

                    continue;
                }

                sawSteamVRWhileConnected = false;
                if (_config.UseMonado) _monado.Update(processes.Monado || processes.WiVRn);
                _server.Broadcast(Serialize(_monado.GetState(_runtimeProcessRunning) with { HeadsetDisplay = _headsetDisplay }));
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Log.Error($"Runtime watcher crashed: {e}");
        }
    }

    private void BroadcastVRState(TimeSpan now)
    {
        _nextState = now + StateInterval;
        if (_server.ClientCount == 0) return;
        try
        {
            _server.Broadcast(Serialize(_vr.GetState(_runtimeProcessRunning) with { HeadsetDisplay = _headsetDisplay }));
        }
        catch (Exception e)
        {
            Log.Warn($"Failed to read VR state: {e.Message}");
        }
    }

    private void OnRuntimeStopped(string runtime)
    {
        _server.Broadcast(Serialize(new State
        {
            IsSteamVRProcessRunning = _runtimeProcessRunning,
            HeadsetDisplay = _headsetDisplay,
        }));
        if (_config.ExitWithSteamVR)
        {
            Log.Info($"Exiting because {runtime} quit (exitWithSteamVR is enabled)");
            _exited.TrySetResult();
        }
    }

    #endregion

    #region Settings actions

    /// <summary>Tells clients about the new port (like the Windows app), then moves the server to it.</summary>
    public async Task<bool> SetPortAsync(int port, int oldPort)
    {
        if (port != oldPort) _server.Broadcast(Serialize(new Event("port_changed", port.ToString())));
        await Task.Delay(200); // let the event go out before connections close
        return await _server.RestartAsync(port, _shutdown.Token);
    }

    public enum ManifestResult { Success, SteamVRNotRunning, FailedToRegister, FailedToSave }

    public async Task<ManifestResult> RegisterManifestAsync()
    {
        if (!_vr.IsConnected) return ManifestResult.SteamVRNotRunning;
        try
        {
            SteamVRManifest.Write();
        }
        catch (Exception e)
        {
            Log.Warn($"Could not write SteamVR manifest: {e.Message}");
            return ManifestResult.FailedToSave;
        }

        var error = await _vr.InvokeAsync(() => _vr.RegisterManifest(Paths.ManifestFile, autoLaunch: true));
        if (error == EVRApplicationError.None)
        {
            Log.Info($"Registered SteamVR auto-launch manifest: {Paths.ManifestFile}");
            return ManifestResult.Success;
        }

        Log.Warn($"Could not register SteamVR manifest: {error}");
        return ManifestResult.FailedToRegister;
    }

    /// <summary>Unregisters now if SteamVR is connected; otherwise it happens on the next connection.</summary>
    public async Task UnregisterManifestAsync()
    {
        if (_vr.IsConnected) await _vr.InvokeAsync(UnregisterManifest);
    }

    #endregion

    #region SteamVR (VR thread)

    private void OnVRConnected()
    {
        _nextState = TimeSpan.Zero;
        if (_config.AutoLaunchWithSteamVR) RegisterManifest();
        else UnregisterManifest();
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

    private const string HeadsetDisplayEvent = "headset_display";

    /// <summary>
    /// Sent as an event (not only in the state) because the Home Assistant integration forwards events to
    /// its event bus as steamvr_event, which a trigger-based template sensor can turn into an entity.
    /// </summary>
    private void UpdateHeadsetDisplay()
    {
        var current = HeadsetDisplay.Read();
        var previous = _headsetDisplay;
        _headsetDisplay = current;
        if (_headsetDisplayReported && current.State == previous.State && current.Connector == previous.Connector)
            return;
        _headsetDisplayReported = true;

        var message = $"Headset display: {current.State}" + (current.Connector != null ? $" ({current.Connector})" : "");
        if (current.State == HeadsetDisplayState.FallbackEdid) Log.Warn(message + ", power-cycle the headset to fix it");
        else Log.Info(message);
        _server.Broadcast(Serialize(new Event(HeadsetDisplayEvent, current.State)));
    }

    private void OnClientConnected(WebSocketClient client) =>
        _ = client.SendAsync(Serialize(new Event(HeadsetDisplayEvent, _headsetDisplay.State)));

    private void OnClientDisconnected(WebSocketClient client)
    {
        foreach (var subscribers in _eventSubscriptions.Values) subscribers.TryRemove(client.Id, out _);
    }

    private void Reply(WebSocketClient client, string nonce, bool success = true, string code = "", string message = "")
    {
        if (!success) Log.Warn($"{code}: {message}");
        _server.Send(client, Serialize(new Response(nonce, success, code, message)));
    }

    private void ReplyResult(WebSocketClient client, string nonce, string? error, string errorCode)
    {
        if (error == null) Reply(client, nonce);
        else Reply(client, nonce, false, errorCode, error);
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

        // Works without a runtime connection.
        if (payload is { type: "command", command: "start_steamvr" })
        {
            try
            {
                StartRuntime();
                Reply(client, nonce);
            }
            catch (Exception e)
            {
                Reply(client, nonce, false, "start_steamvr_error", e.Message);
            }

            return;
        }

        if (_vr.IsConnected) await HandleSteamVRMessage(client, payload);
        else if (_monado.IsConnected) await HandleMonadoMessage(client, payload);
        else Reply(client, nonce, false, "steamvr_disconnected", "No VR runtime (SteamVR or Monado) is connected");
    }

    private static ETrackedControllerRole[]? VibrationRoles(string command) => command switch
    {
        "vibrate_controller_right" => [ETrackedControllerRole.RightHand],
        "vibrate_controller_left" => [ETrackedControllerRole.LeftHand],
        "vibrate_controller_both" => [ETrackedControllerRole.RightHand, ETrackedControllerRole.LeftHand],
        _ => null,
    };

    private async Task HandleSteamVRMessage(WebSocketClient client, Payload payload)
    {
        var nonce = payload.customProperties.nonce;
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
                var roles = VibrationRoles(payload.command);
                if (roles == null)
                {
                    Reply(client, nonce, false, "invalid_command", $"Unknown command '{payload.command}'");
                    break;
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

    private async Task HandleMonadoMessage(WebSocketClient client, Payload payload)
    {
        var nonce = payload.customProperties.nonce;
        switch (payload.type)
        {
            case "notification" when payload.customProperties.enabled:
                Reply(client, nonce, false, "unsupported_runtime",
                    "Advanced notifications are only supported with SteamVR");
                break;
            case "notification" when !string.IsNullOrEmpty(payload.basicMessage):
                if (!_wayVRRunning)
                {
                    Reply(client, nonce, false, "notification_error", "Notifications on Monado need WayVR running");
                    break;
                }

                string? icon = null;
                try
                {
                    using var image = await Images.LoadAsync(payload);
                    if (image != null) icon = Images.ToPngBase64(image, 128);
                }
                catch (Exception e)
                {
                    Reply(client, nonce, false, "image_read_error", $"Image Read Failure: {e.Message}");
                }

                var error = await WayVR.NotifyAsync(payload.basicTitle, payload.basicMessage, icon);
                if (error != null || icon != null || !HasImage(payload))
                    ReplyResult(client, nonce, error, "notification_error");
                break;
            case "notification":
                Reply(client, nonce, false, "invalid_notification", "Basic message is empty");
                break;
            case "command":
                var roles = VibrationRoles(payload.command);
                if (roles == null)
                {
                    Reply(client, nonce, false, "invalid_command", $"Unknown command '{payload.command}'");
                    break;
                }

                if (!_wayVRRunning)
                {
                    Reply(client, nonce, false, "haptics_error", "Controller vibration on Monado needs WayVR running");
                    break;
                }

                var devices = roles.Select(r => r == ETrackedControllerRole.LeftHand ? 0 : 1);
                ReplyResult(client, nonce, await WayVR.HapticsAsync(devices, 1f, 0.25f), "haptics_error");
                break;
            case "register_event" or "unregister_event":
                Reply(client, nonce, false, "unsupported_runtime", "OpenVR events are only available with SteamVR");
                break;
            default:
                Reply(client, nonce, false, "invalid_type", "Invalid payload type");
                break;
        }
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
        if (error != null || image != null || !HasImage(payload)) ReplyResult(client, nonce, error, "notification_error");
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
        ReplyResult(client, nonce, error, "notification_error");
    }

    private static bool HasImage(Payload p) =>
        !string.IsNullOrEmpty(p.imageData) || !string.IsNullOrEmpty(p.imagePath) || !string.IsNullOrEmpty(p.imageUrl);

    private static Image<Rgba32> BlankCanvasFor(IEnumerable<Payload.TextArea> areas)
    {
        var width = Math.Clamp(areas.Max(a => a.xPositionPx + a.widthPx), 1, AdvancedNotifications.MaxImageDimension);
        var height = Math.Clamp(areas.Max(a => a.yPositionPx + a.heightPx), 1, AdvancedNotifications.MaxImageDimension);
        return new Image<Rgba32>(width, height);
    }

    private void StartRuntime()
    {
        var command = _config.StartRuntimeCommand;
        if (string.IsNullOrWhiteSpace(command))
        {
            if (_config.Runtime != "monado")
            {
                // UseShellExecute goes through xdg-open, which hands steam:// URLs to the Steam client.
                Process.Start(new ProcessStartInfo("steam://rungameid/250820") { UseShellExecute = true })?.Dispose();
                return;
            }

            command = "systemctl --user start monado.service";
        }

        Log.Info($"Starting VR runtime: {command}");
        Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } })?.Dispose();
    }

    #endregion

    private static string Serialize(object value) => JsonConvert.SerializeObject(value);
}
