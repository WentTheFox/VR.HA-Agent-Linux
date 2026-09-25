using System.Numerics;
using System.Runtime.InteropServices;
using SteamVRHAAgent.Protocol;
using Valve.VR;
using CustomProperties = SteamVRHAAgent.Protocol.Payload.CustomProperties;

namespace SteamVRHAAgent.VR;

/// <summary>
/// Image notifications rendered as OpenVR overlays, with anchors, transitions, animations and follow.
/// On Windows this was handled by the closed-source SteamVR.NotifyPlugin.exe; here it runs in-process.
/// All members must be used on the VR thread.
/// </summary>
public sealed class AdvancedNotifications(VRRuntime vr)
{
    public const int MaxImageDimension = 1024;
    private const int MaxQueuedPerChannel = 32;
    private static readonly TimeSpan DefaultFrameInterval = TimeSpan.FromSeconds(1.0 / 90);

    private readonly Dictionary<int, Channel> _channels = new();

    public bool IsAnimating => _channels.Values.Any(c => c.Current != null || c.Queue.Count > 0);

    public TimeSpan FrameInterval
    {
        get
        {
            var hz = _channels.Values
                .Select(c => c.Current?.Props.animationHz ?? -1)
                .Where(h => h > 0)
                .DefaultIfEmpty(-1)
                .Max();
            return hz > 0 ? TimeSpan.FromSeconds(1.0 / hz) : DefaultFrameInterval;
        }
    }

    public string? Enqueue(CustomProperties props, Rgba32Image image)
    {
        var overlay = OpenVR.Overlay;
        if (overlay == null) return "The OpenVR runtime does not support overlays";

        if (!_channels.TryGetValue(props.overlayChannel, out var channel))
        {
            ulong handle = 0;
            var error = overlay.CreateOverlay($"{VRRuntime.AppKey}.channel.{props.overlayChannel}",
                $"Home Assistant notification {props.overlayChannel}", ref handle);
            if (error != EVROverlayError.None) return $"Could not create overlay: {error}";
            overlay.SetOverlaySortOrder(handle, (uint)Math.Max(0, props.overlayChannel));
            channel = new Channel(handle);
            _channels[props.overlayChannel] = channel;
        }

        if (channel.Queue.Count >= MaxQueuedPerChannel) channel.Queue.Dequeue();
        channel.Queue.Enqueue(new Item(props, image));
        return null;
    }

    public void Update(TimeSpan now)
    {
        var overlay = OpenVR.Overlay;
        if (overlay == null) return;

        foreach (var channel in _channels.Values)
        {
            if (channel.Current == null)
            {
                if (!channel.Queue.TryDequeue(out var next)) continue;
                channel.Current = Begin(overlay, channel.Handle, next, now);
                if (channel.Current == null) continue;
            }

            if (!Render(overlay, channel.Handle, channel.Current, now))
            {
                overlay.HideOverlay(channel.Handle);
                channel.Current = null;
            }
        }
    }

    public void Reset()
    {
        var overlay = OpenVR.Overlay;
        foreach (var channel in _channels.Values) overlay?.DestroyOverlay(channel.Handle);
        _channels.Clear();
    }

    private Showing? Begin(CVROverlay overlay, ulong handle, Item item, TimeSpan now)
    {
        var pinned = GCHandle.Alloc(item.Image.Pixels, GCHandleType.Pinned);
        EVROverlayError error;
        try
        {
            error = overlay.SetOverlayRaw(handle, pinned.AddrOfPinnedObject(), (uint)item.Image.Width,
                (uint)item.Image.Height, 4);
        }
        finally
        {
            pinned.Free();
        }

        if (error != EVROverlayError.None)
        {
            Log.Warn($"SetOverlayRaw failed: {error}");
            return null;
        }

        var props = item.Props;
        var showing = new Showing(props, now);

        var device = AnchorDevice(props.anchorType);
        if (props.attachToAnchor && props.anchorType != 0 && device != OpenVR.k_unTrackedDeviceIndexInvalid)
        {
            showing.AttachedDevice = device;
        }
        else
        {
            showing.Anchor = props.attachToAnchor ? Matrix4x4.Identity : ComputeAnchor(props, device);
        }

        // Start fully transparent so the first frame doesn't flash before the transform is applied.
        overlay.SetOverlayAlpha(handle, 0);
        Render(overlay, handle, showing, now);
        overlay.ShowOverlay(handle);
        return showing;
    }

    /// <summary>Applies one frame. Returns false once the notification has finished.</summary>
    private bool Render(CVROverlay overlay, ulong handle, Showing showing, TimeSpan now)
    {
        var props = showing.Props;
        var elapsed = (now - showing.Start).TotalMilliseconds;

        var transitionIn = props.transitions.Length > 0 ? props.transitions[0] : null;
        var transitionOut = props.transitions.Length > 1 ? props.transitions[1] : transitionIn;
        double inMs = transitionIn?.durationMs ?? 0, stayMs = Math.Max(0, props.durationMs), outMs = transitionOut?.durationMs ?? 0;

        if (elapsed >= inMs + stayMs + outMs) return false;

        var baseState = OverlayState.Base(props);
        OverlayState state;
        if (elapsed < inMs && transitionIn != null)
        {
            var p = Tween.Apply(transitionIn.tweenType, elapsed / inMs, Tween.Mode.Out);
            state = OverlayState.Lerp(baseState.With(transitionIn), baseState, p);
        }
        else if (elapsed >= inMs + stayMs && transitionOut != null)
        {
            var p = Tween.Apply(transitionOut.tweenType, (elapsed - inMs - stayMs) / outMs, Tween.Mode.In);
            state = OverlayState.Lerp(baseState, baseState.With(transitionOut), p);
        }
        else
        {
            state = baseState;
        }

        state = state.Animate(props.animations, elapsed / 1000.0);

        overlay.SetOverlayWidthInMeters(handle, Math.Max(0.001f, props.widthM * state.Scale));
        overlay.SetOverlayAlpha(handle, Math.Clamp(state.Opacity, 0, 1));

        var local = state.ToMatrix();
        if (showing.AttachedDevice is { } device)
        {
            var m = ToHmd(local);
            overlay.SetOverlayTransformTrackedDeviceRelative(handle, device, ref m);
        }
        else
        {
            if (props.follow.enabled) UpdateFollow(showing, baseState, now);
            var m = ToHmd(local * showing.Anchor);
            overlay.SetOverlayTransformAbsolute(handle, ETrackingUniverseOrigin.TrackingUniverseStanding, ref m);
        }

        return true;
    }

    private void UpdateFollow(Showing showing, OverlayState baseState, TimeSpan now)
    {
        var follow = showing.Props.follow;
        if (showing.FollowFrom is { } from && showing.FollowTo is { } to)
        {
            var p = Math.Clamp((now - showing.FollowStart).TotalMilliseconds / Math.Max(1, follow.durationMs), 0, 1);
            showing.Anchor = Interpolate(from, to, (float)Tween.Apply(follow.tweenType, p, Tween.Mode.InOut));
            if (p >= 1) showing.FollowFrom = showing.FollowTo = null;
            return;
        }

        var poses = vr.GetPoses();
        var hmd = poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (!hmd.bPoseIsValid) return;

        var hmdMatrix = FromHmd(hmd.mDeviceToAbsoluteTracking);
        var hmdPosition = hmdMatrix.Translation;
        var hmdForward = -new Vector3(hmdMatrix.M31, hmdMatrix.M32, hmdMatrix.M33);
        var overlayPosition = (baseState.ToMatrix() * showing.Anchor).Translation;
        var toOverlay = overlayPosition - hmdPosition;
        if (toOverlay.LengthSquared() < 1e-6f) return;

        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(toOverlay), Vector3.Normalize(hmdForward)), -1, 1))
                    * 180 / MathF.PI;
        if (angle <= follow.triggerAngle) return;

        showing.FollowFrom = showing.Anchor;
        showing.FollowTo = ComputeAnchor(showing.Props, AnchorDevice(showing.Props.anchorType), poses);
        showing.FollowStart = now;
    }

    private uint AnchorDevice(int anchorType) => anchorType switch
    {
        2 => vr.GetDeviceIndex(ETrackedControllerRole.LeftHand),
        3 => vr.GetDeviceIndex(ETrackedControllerRole.RightHand),
        _ => OpenVR.k_unTrackedDeviceIndex_Hmd,
    };

    /// <summary>World-space anchor taken from a device's current pose, with ignored axes flattened out.</summary>
    private Matrix4x4 ComputeAnchor(CustomProperties props, uint device, TrackedDevicePose_t[]? poses = null)
    {
        poses ??= vr.GetPoses();
        if (device == OpenVR.k_unTrackedDeviceIndexInvalid || device >= poses.Length || !poses[device].bPoseIsValid)
            device = OpenVR.k_unTrackedDeviceIndex_Hmd;
        var pose = poses[device];
        if (!pose.bPoseIsValid) return Matrix4x4.Identity;

        var m = FromHmd(pose.mDeviceToAbsoluteTracking);
        var forward = -new Vector3(m.M31, m.M32, m.M33);
        var yaw = MathF.Atan2(-forward.X, -forward.Z);
        var pitch = MathF.Asin(Math.Clamp(forward.Y, -1, 1));
        var withoutYawPitch = m * Matrix4x4.Transpose(Matrix4x4.CreateRotationX(pitch) * Matrix4x4.CreateRotationY(yaw));
        var roll = MathF.Atan2(withoutYawPitch.M12, withoutYawPitch.M11);

        if (props.ignoreAnchorYaw) yaw = 0;
        if (props.ignoreAnchorPitch) pitch = 0;
        if (props.ignoreAnchorRoll) roll = 0;

        var anchor = Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll);
        anchor.Translation = m.Translation;
        return anchor;
    }

    private static Matrix4x4 Interpolate(Matrix4x4 a, Matrix4x4 b, float t)
    {
        Matrix4x4.Decompose(a, out _, out var ra, out var ta);
        Matrix4x4.Decompose(b, out _, out var rb, out var tb);
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(ra, rb, t));
        m.Translation = Vector3.Lerp(ta, tb, t);
        return m;
    }

    // System.Numerics uses row vectors (v * M); OpenVR's 3x4 matrices use column vectors.
    private static Matrix4x4 FromHmd(HmdMatrix34_t m) => new(
        m.m0, m.m4, m.m8, 0,
        m.m1, m.m5, m.m9, 0,
        m.m2, m.m6, m.m10, 0,
        m.m3, m.m7, m.m11, 1);

    private static HmdMatrix34_t ToHmd(Matrix4x4 m) => new()
    {
        m0 = m.M11, m1 = m.M21, m2 = m.M31, m3 = m.M41,
        m4 = m.M12, m5 = m.M22, m6 = m.M32, m7 = m.M42,
        m8 = m.M13, m9 = m.M23, m10 = m.M33, m11 = m.M43,
    };

    private sealed record Item(CustomProperties Props, Rgba32Image Image);

    private sealed class Channel(ulong handle)
    {
        public ulong Handle { get; } = handle;
        public Queue<Item> Queue { get; } = new();
        public Showing? Current { get; set; }
    }

    private sealed class Showing(CustomProperties props, TimeSpan start)
    {
        public CustomProperties Props { get; } = props;
        public TimeSpan Start { get; } = start;
        public uint? AttachedDevice { get; set; }
        public Matrix4x4 Anchor { get; set; } = Matrix4x4.Identity;
        public Matrix4x4? FollowFrom { get; set; }
        public Matrix4x4? FollowTo { get; set; }
        public TimeSpan FollowStart { get; set; }
    }

    private readonly record struct OverlayState(
        float X, float Y, float Z, float Yaw, float Pitch, float Roll, float Scale, float Opacity)
    {
        public static OverlayState Base(CustomProperties p) => new(p.xDistanceM, p.yDistanceM, p.zDistanceM,
            p.yawDeg, p.pitchDeg, p.rollDeg, 1, p.opacityPer);

        /// <summary>The state a transition starts from (in) or ends at (out).</summary>
        public OverlayState With(Payload.Transition t) => new(X + t.xDistanceM, Y + t.yDistanceM, Z + t.zDistanceM,
            Yaw + t.yawDeg, Pitch + t.pitchDeg, Roll + t.rollDeg, Scale * t.scalePer, Opacity * t.opacityPer);

        public static OverlayState Lerp(OverlayState a, OverlayState b, double t)
        {
            var f = (float)t;
            return new OverlayState(
                float.Lerp(a.X, b.X, f), float.Lerp(a.Y, b.Y, f), float.Lerp(a.Z, b.Z, f),
                float.Lerp(a.Yaw, b.Yaw, f), float.Lerp(a.Pitch, b.Pitch, f), float.Lerp(a.Roll, b.Roll, f),
                float.Lerp(a.Scale, b.Scale, f), float.Lerp(a.Opacity, b.Opacity, f));
        }

        public OverlayState Animate(IEnumerable<Payload.Animation> animations, double seconds)
        {
            var s = this;
            foreach (var a in animations)
            {
                if (a.property == 0) continue;
                var angle = 2 * Math.PI * a.frequency * seconds;
                var wave = a.phase switch
                {
                    1 => Math.Cos(angle),
                    2 => -Math.Sin(angle),
                    3 => -Math.Cos(angle),
                    _ => Math.Sin(angle),
                };
                if (a.flipWaveform) wave = -wave;
                var v = (float)(a.amplitude * wave);
                s = a.property switch
                {
                    1 => s with { Yaw = s.Yaw + v },
                    2 => s with { Pitch = s.Pitch + v },
                    3 => s with { Roll = s.Roll + v },
                    4 => s with { Z = s.Z + v },
                    5 => s with { Y = s.Y + v },
                    6 => s with { X = s.X + v },
                    7 => s with { Scale = s.Scale * (1 + v) },
                    8 => s with { Opacity = s.Opacity + v },
                    _ => s,
                };
            }

            return s;
        }

        /// <summary>Overlay pose relative to its anchor. OpenVR looks down -Z, so a positive distance is in front.</summary>
        public Matrix4x4 ToMatrix()
        {
            const float deg = MathF.PI / 180;
            var m = Matrix4x4.CreateFromYawPitchRoll(Yaw * deg, Pitch * deg, Roll * deg);
            m.Translation = new Vector3(X, Y, -Z);
            return m;
        }
    }
}

/// <summary>Easing curves; type numbers follow OpenVRNotificationPipe (0 = linear ... 9 = bounce).</summary>
public static class Tween
{
    public enum Mode { In, Out, InOut }

    public static double Apply(int type, double t, Mode mode)
    {
        t = Math.Clamp(t, 0, 1);
        return mode switch
        {
            Mode.In => EaseIn(type, t),
            Mode.Out => 1 - EaseIn(type, 1 - t),
            _ => t < 0.5 ? EaseIn(type, t * 2) / 2 : 1 - EaseIn(type, (1 - t) * 2) / 2,
        };
    }

    private static double EaseIn(int type, double t) => type switch
    {
        1 => 1 - Math.Cos(t * Math.PI / 2),
        2 => t * t,
        3 => t * t * t,
        4 => Math.Pow(t, 4),
        5 => Math.Pow(t, 5),
        6 => 1 - Math.Sqrt(1 - t * t),
        7 => 2.70158 * t * t * t - 1.70158 * t * t,
        8 => t is 0 or 1 ? t : -Math.Pow(2, 10 * t - 10) * Math.Sin((t * 10 - 10.75) * (2 * Math.PI / 3)),
        9 => 1 - BounceOut(1 - t),
        _ => t,
    };

    private static double BounceOut(double t)
    {
        const double n = 7.5625, d = 2.75;
        if (t < 1 / d) return n * t * t;
        if (t < 2 / d) return n * (t -= 1.5 / d) * t + 0.75;
        if (t < 2.5 / d) return n * (t -= 2.25 / d) * t + 0.9375;
        return n * (t -= 2.625 / d) * t + 0.984375;
    }
}
