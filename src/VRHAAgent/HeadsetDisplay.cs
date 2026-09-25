using System.Text;
using Newtonsoft.Json;

namespace VRHAAgent;

/// <summary>
/// Whether the headset's display came up properly, read from sysfs (no runtime or root needed).
///
/// Some Valve Index power-ups leave its DisplayPort connector "connected" but with a blank fallback EDID
/// (seen with NVIDIA), so VR runtimes can't find the headset display. Only power-cycling the headset (link
/// box) fixes it, which Home Assistant can automate from this state.
/// </summary>
public sealed record HeadsetDisplayState
{
    public const string Ok = "ok";
    public const string FallbackEdid = "fallback_edid";
    public const string NotDetected = "not_detected";
    public const string HeadsetOff = "headset_off";

    /// <summary>ok, fallback_edid (connected with a placeholder EDID), not_detected, or headset_off.</summary>
    [JsonProperty("state")] public string State { get; init; } = HeadsetOff;

    /// <summary>DRM connector of the headset display (or of the fallback display), e.g. card1-DP-3.</summary>
    [JsonProperty("connector")] public string? Connector { get; init; }

    [JsonProperty("edid_manufacturer")] public string? EdidManufacturer { get; init; }
    [JsonProperty("edid_product")] public string? EdidProduct { get; init; }
    [JsonProperty("edid_name")] public string? EdidName { get; init; }
}

public static class HeadsetDisplay
{
    private const string DrmDir = "/sys/class/drm";
    private const string UsbDir = "/sys/bus/usb/devices";

    // Valve Index HMD: USB 28de:2300, display EDID manufacturer "VLV".
    private const string IndexUsbVendor = "28de";
    private const string IndexUsbProduct = "2300";
    private const string ValveEdidManufacturer = "VLV";

    /// <param name="drmDir">sysfs DRM class directory (overridable for tests).</param>
    /// <param name="usbDir">sysfs USB devices directory (overridable for tests).</param>
    public static HeadsetDisplayState Read(string drmDir = DrmDir, string usbDir = UsbDir)
    {
        try
        {
            var connected = ConnectedConnectors(drmDir).ToList();

            var headset = connected.FirstOrDefault(c => c.Edid?.Manufacturer == ValveEdidManufacturer);
            if (headset != null) return Describe(HeadsetDisplayState.Ok, headset);

            if (!IsIndexOnUsb(usbDir)) return new HeadsetDisplayState { State = HeadsetDisplayState.HeadsetOff };

            var fallback = connected.FirstOrDefault(c => c.IsFallback);
            return fallback != null
                ? Describe(HeadsetDisplayState.FallbackEdid, fallback)
                : new HeadsetDisplayState { State = HeadsetDisplayState.NotDetected };
        }
        catch (Exception e)
        {
            Log.Debug($"Could not read headset display state: {e.Message}");
            return new HeadsetDisplayState { State = HeadsetDisplayState.HeadsetOff };
        }
    }

    private static HeadsetDisplayState Describe(string state, Connector connector) => new()
    {
        State = state,
        Connector = connector.Name,
        EdidManufacturer = connector.Edid?.Manufacturer,
        EdidProduct = connector.Edid == null ? null : $"0x{connector.Edid.Product:x4}",
        EdidName = connector.Edid?.Name,
    };

    private static bool IsIndexOnUsb(string usbDir)
    {
        if (!Directory.Exists(usbDir)) return false;
        foreach (var device in Directory.EnumerateDirectories(usbDir))
        {
            if (ReadText(Path.Combine(device, "idVendor")) == IndexUsbVendor &&
                ReadText(Path.Combine(device, "idProduct")) == IndexUsbProduct)
                return true;
        }

        return false;
    }

    private sealed record Connector(string Name, Edid? Edid, string[] Modes)
    {
        /// <summary>
        /// The placeholder the driver uses when it can't read the display's EDID: e.g. NVIDIA's 128-byte "NVD"
        /// EDID with product 0 and only 640x480.
        /// </summary>
        public bool IsFallback =>
            Edid == null || (Edid.Manufacturer == "NVD" && Edid.Product == 0) || Modes is ["640x480"];
    }

    private static IEnumerable<Connector> ConnectedConnectors(string drmDir)
    {
        if (!Directory.Exists(drmDir)) yield break;
        // Connectors are named like card1-DP-3; plain cardN / renderDN entries are the GPUs themselves.
        foreach (var dir in Directory.EnumerateFileSystemEntries(drmDir, "card*-*"))
        {
            if (ReadText(Path.Combine(dir, "status")) != "connected") continue;
            byte[] edid;
            try
            {
                edid = File.ReadAllBytes(Path.Combine(dir, "edid"));
            }
            catch (IOException)
            {
                edid = [];
            }

            var modes = (ReadText(Path.Combine(dir, "modes")) ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
            yield return new Connector(Path.GetFileName(dir), Edid.Parse(edid), modes);
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record Edid(string Manufacturer, ushort Product, string? Name)
    {
        private static readonly byte[] Header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

        public static Edid? Parse(byte[] data)
        {
            if (data.Length < 128 || !data.AsSpan(0, 8).SequenceEqual(Header)) return null;

            // Bytes 8-9: three 5-bit letters, big-endian ('A' = 1).
            var packed = (data[8] << 8) | data[9];
            var manufacturer = new string([
                (char)('@' + ((packed >> 10) & 0x1F)),
                (char)('@' + ((packed >> 5) & 0x1F)),
                (char)('@' + (packed & 0x1F)),
            ]);
            var product = (ushort)(data[10] | (data[11] << 8));

            // Four 18-byte descriptors from byte 54; tag 0xFC holds the monitor name.
            string? name = null;
            for (var offset = 54; offset <= 108; offset += 18)
            {
                if (data[offset] != 0 || data[offset + 1] != 0 || data[offset + 3] != 0xFC) continue;
                name = Encoding.ASCII.GetString(data, offset + 5, 13).Split('\n')[0].Trim();
            }

            return new Edid(manufacturer, product, name);
        }
    }
}
