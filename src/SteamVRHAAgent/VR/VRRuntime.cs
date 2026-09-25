using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SteamVRHAAgent.Protocol;
using Valve.VR;

namespace SteamVRHAAgent.VR;

/// <summary>
/// Owns the OpenVR connection. Every OpenVR call happens on one dedicated thread; other threads
/// use <see cref="InvokeAsync{T}"/> to run work there.
/// </summary>
public sealed class VRRuntime : IDisposable
{
    public const string AppKey = "antek.steamvr_ha_agent";

    private static readonly TimeSpan InitRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleTick = TimeSpan.FromMilliseconds(100);

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _nextInitAttempt = TimeSpan.Zero;
    private EVRInitError _lastInitError = EVRInitError.None;

    public bool IsConnected { get; private set; }
    public AdvancedNotifications Advanced { get; }

    /// <summary>Raised on the VR thread after OpenVR connects.</summary>
    public event Action? Connected;

    /// <summary>Raised on the VR thread after the runtime asked us to quit and the connection was closed.</summary>
    public event Action? Disconnected;

    /// <summary>Raised on the VR thread for every polled event (after internal handling).</summary>
    public event Action<VREvent_t>? EventReceived;

    /// <summary>Raised on the VR thread once per loop iteration while connected.</summary>
    public event Action<TimeSpan>? Tick;

    public VRRuntime()
    {
        Advanced = new AdvancedNotifications(this);
        _thread = new Thread(Run) { Name = "OpenVR", IsBackground = true };
    }

    public void Start() => _thread.Start();

    public TimeSpan Now => _clock.Elapsed;

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        });
        _wake.Set();
        return tcs.Task;
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return true;
    });

    private void Run()
    {
        while (!_stop)
        {
            while (_work.TryDequeue(out var item)) item();

            if (!IsConnected)
            {
                if (Now >= _nextInitAttempt)
                {
                    TryConnect();
                    _nextInitAttempt = Now + InitRetryInterval;
                }
            }
            else
            {
                PollEvents();
                if (IsConnected) Tick?.Invoke(Now);
            }

            var wait = IsConnected && Advanced.IsAnimating ? Advanced.FrameInterval : IdleTick;
            _wake.WaitOne(wait);
        }

        if (IsConnected) Disconnect(acknowledgeQuit: false);
    }

    private void TryConnect()
    {
        var error = EVRInitError.None;
        try
        {
            // Background: never launches SteamVR on its own, only attaches to a running instance.
            OpenVR.Init(ref error, EVRApplicationType.VRApplication_Background);
        }
        catch (DllNotFoundException)
        {
            error = EVRInitError.Init_InstallationNotFound;
        }

        if (error != EVRInitError.None)
        {
            if (error != _lastInitError) Log.Debug($"OpenVR not available: {error}");
            _lastInitError = error;
            return;
        }

        _lastInitError = error;
        IsConnected = true;
        Log.Info("Connected to OpenVR runtime");
        Connected?.Invoke();
    }

    private void PollEvents()
    {
        var system = OpenVR.System;
        if (system == null) return;

        var ev = new VREvent_t();
        var size = (uint)Marshal.SizeOf<VREvent_t>();
        while (IsConnected && system.PollNextEvent(ref ev, size))
        {
            var type = (EVREventType)ev.eventType;
            if (type == EVREventType.VREvent_Quit)
            {
                Log.Info("OpenVR runtime is quitting");
                EventReceived?.Invoke(ev);
                Disconnect(acknowledgeQuit: true);
                return;
            }

            EventReceived?.Invoke(ev);
        }
    }

    /// <summary>Drops the connection; used when the runtime quits or disappears.</summary>
    public void Disconnect(bool acknowledgeQuit)
    {
        if (!IsConnected) return;
        Advanced.Reset();
        ForgetNotificationOverlays();
        try
        {
            if (acknowledgeQuit) OpenVR.System?.AcknowledgeQuit_Exiting();
            OpenVR.Shutdown();
        }
        catch (Exception e)
        {
            Log.Warn($"OpenVR shutdown error: {e.Message}");
        }

        IsConnected = false;
        _nextInitAttempt = Now + InitRetryInterval;
        Log.Info("Disconnected from OpenVR runtime");
        Disconnected?.Invoke();
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(3));
    }

    #region Queries (VR thread only)

    public State GetState(bool processRunning)
    {
        if (!IsConnected || OpenVR.System == null)
            return new State { IsOpenVRConnected = false, IsSteamVRProcessRunning = processRunning };

        var system = OpenVR.System;
        var appKey = GetRunningApplicationKey();
        return new State
        {
            IsOpenVRConnected = true,
            IsSteamVRProcessRunning = processRunning,
            HmdActivityLevel = system.GetTrackedDeviceActivityLevel(OpenVR.k_unTrackedDeviceIndex_Hmd),
            CurrentApplicationKey = appKey,
            CurrentApplicationName = appKey == null ? null : GetApplicationName(appKey),
            RightController = GetController(ETrackedControllerRole.RightHand),
            LeftController = GetController(ETrackedControllerRole.LeftHand),
        };
    }

    private static Controller GetController(ETrackedControllerRole role)
    {
        var system = OpenVR.System;
        var index = system.GetTrackedDeviceIndexForControllerRole(role);
        if (index == OpenVR.k_unTrackedDeviceIndexInvalid || !system.IsTrackedDeviceConnected(index))
            return new Controller(false);

        var error = ETrackedPropertyError.TrackedProp_Success;
        var battery = system.GetFloatTrackedDeviceProperty(index,
            ETrackedDeviceProperty.Prop_DeviceBatteryPercentage_Float, ref error);
        int? batteryPercent = error == ETrackedPropertyError.TrackedProp_Success ? (int)Math.Round(battery * 100) : null;

        error = ETrackedPropertyError.TrackedProp_Success;
        var charging = system.GetBoolTrackedDeviceProperty(index, ETrackedDeviceProperty.Prop_DeviceIsCharging_Bool,
            ref error);
        bool? isCharging = error == ETrackedPropertyError.TrackedProp_Success ? charging : null;

        return new Controller(true, batteryPercent, isCharging);
    }

    private static string? GetRunningApplicationKey()
    {
        var applications = OpenVR.Applications;
        if (applications == null) return null;
        var pid = applications.GetCurrentSceneProcessId();
        if (pid == 0) return null;
        var key = new StringBuilder((int)OpenVR.k_unMaxApplicationKeyLength);
        var error = applications.GetApplicationKeyByProcessId(pid, key, OpenVR.k_unMaxApplicationKeyLength);
        return error == EVRApplicationError.None && key.Length > 0 ? key.ToString() : null;
    }

    private static string? GetApplicationName(string appKey)
    {
        var applications = OpenVR.Applications;
        if (applications == null) return null;
        var error = EVRApplicationError.None;
        var name = new StringBuilder(256);
        applications.GetApplicationPropertyString(appKey, EVRApplicationProperty.Name_String, name, (uint)name.Capacity,
            ref error);
        return error == EVRApplicationError.None ? name.ToString() : null;
    }

    public uint GetDeviceIndex(ETrackedControllerRole role) =>
        OpenVR.System?.GetTrackedDeviceIndexForControllerRole(role) ?? OpenVR.k_unTrackedDeviceIndexInvalid;

    public TrackedDevicePose_t[] GetPoses()
    {
        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System?.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, poses);
        return poses;
    }

    #endregion

    #region Actions (VR thread only)

    public void TriggerHapticPulse(ETrackedControllerRole role, ushort durationMicroSec)
    {
        var index = GetDeviceIndex(role);
        if (index == OpenVR.k_unTrackedDeviceIndexInvalid) return;
        OpenVR.System?.TriggerHapticPulse(index, 0, durationMicroSec);
    }

    public EVRApplicationError RegisterManifest(string manifestPath, bool autoLaunch)
    {
        var applications = OpenVR.Applications;
        if (applications == null) return EVRApplicationError.InvalidApplication;

        var error = applications.AddApplicationManifest(manifestPath, false);
        if (error != EVRApplicationError.None) return error;
        return applications.SetApplicationAutoLaunch(AppKey, autoLaunch);
    }

    public EVRApplicationError UnregisterManifest(string manifestPath)
    {
        var applications = OpenVR.Applications;
        if (applications == null) return EVRApplicationError.InvalidApplication;
        return applications.IsApplicationInstalled(AppKey)
            ? applications.RemoveApplicationManifest(manifestPath)
            : EVRApplicationError.None;
    }

    private readonly Dictionary<string, ulong> _notificationOverlays = new();
    private readonly LinkedList<string> _notificationOverlayOrder = new();
    private const int MaxNotificationOverlays = 16;

    /// <summary>Shows a standard SteamVR notification. Overlays (which provide the title) are cached per title.</summary>
    public string? ShowNotification(string title, string message, Rgba32Image? image)
    {
        var overlay = OpenVR.Overlay;
        var notifications = OpenVR.Notifications;
        if (overlay == null || notifications == null) return "The OpenVR runtime does not support notifications";

        if (!_notificationOverlays.TryGetValue(title, out var handle))
        {
            if (_notificationOverlays.Count >= MaxNotificationOverlays)
            {
                var oldest = _notificationOverlayOrder.First!.Value;
                _notificationOverlayOrder.RemoveFirst();
                overlay.DestroyOverlay(_notificationOverlays[oldest]);
                _notificationOverlays.Remove(oldest);
            }

            var error = overlay.CreateOverlay($"{AppKey}.notification.{Guid.NewGuid():N}", title, ref handle);
            if (error != EVROverlayError.None) return $"Could not create notification overlay: {error}";
            _notificationOverlays[title] = handle;
            _notificationOverlayOrder.AddLast(title);
        }

        var bitmap = new NotificationBitmap_t();
        var pinned = default(GCHandle);
        try
        {
            if (image != null)
            {
                pinned = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
                bitmap.m_pImageData = pinned.AddrOfPinnedObject();
                bitmap.m_nWidth = image.Width;
                bitmap.m_nHeight = image.Height;
                bitmap.m_nBytesPerPixel = 4;
            }

            uint id = 0;
            var result = notifications.CreateNotification(handle, 0, EVRNotificationType.Transient, message,
                EVRNotificationStyle.Application, ref bitmap, ref id);
            return result == EVRNotificationError.OK ? null : $"Could not create notification: {result}";
        }
        finally
        {
            if (pinned.IsAllocated) pinned.Free();
        }
    }

    internal void ForgetNotificationOverlays()
    {
        _notificationOverlays.Clear();
        _notificationOverlayOrder.Clear();
    }

    #endregion
}
