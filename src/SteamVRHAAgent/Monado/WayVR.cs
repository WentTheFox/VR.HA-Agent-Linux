using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;

namespace SteamVRHAAgent.Monado;

/// <summary>
/// Notifications and haptics through WayVR (the desktop overlay for Monado/SteamVR), since Monado itself has
/// neither. WayVR accepts XSOverlay-style notifications on UDP 42069 and has a CLI for haptics.
/// </summary>
public static class WayVR
{
    public const string ProcessName = "wayvr";
    private const int NotificationPort = 42069;
    private const int MaxDatagramBytes = 60 * 1024;

    /// <summary>Sends a notification. Returns an error message, or null on success.</summary>
    public static async Task<string?> NotifyAsync(string title, string message, string? pngBase64, float timeoutSeconds = 5)
    {
        var payload = new Dictionary<string, object>
        {
            ["messageType"] = 1,
            ["index"] = 0,
            ["timeout"] = timeoutSeconds,
            ["height"] = 175f,
            ["opacity"] = 1f,
            ["volume"] = 0.7f,
            ["audioPath"] = "default",
            ["title"] = title,
            ["content"] = message,
            ["useBase64Icon"] = pngBase64 != null,
            ["icon"] = pngBase64 ?? "default",
            ["sourceApp"] = "Home Assistant",
        };

        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
        if (bytes.Length > MaxDatagramBytes && pngBase64 != null)
        {
            // Too big for one datagram; drop the icon rather than the notification.
            payload["useBase64Icon"] = false;
            payload["icon"] = "default";
            bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
        }

        try
        {
            using var udp = new UdpClient();
            await udp.SendAsync(bytes, "127.0.0.1", NotificationPort);
            return null;
        }
        catch (SocketException e)
        {
            return $"Could not send notification to WayVR: {e.Message}";
        }
    }

    /// <summary>Vibrates the given controllers (0 = left, 1 = right). Returns an error message, or null on success.</summary>
    public static async Task<string?> HapticsAsync(IEnumerable<int> devices, float intensity, float durationSeconds)
    {
        foreach (var device in devices)
        {
            var info = new ProcessStartInfo("wayvrctl")
            {
                ArgumentList =
                {
                    "haptics", device.ToString(),
                    "--intensity", intensity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--duration", durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                using var process = Process.Start(info)!;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var stderr = process.StandardError.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);
                if (process.ExitCode != 0) return $"wayvrctl haptics failed: {(await stderr).Trim()}";
            }
            catch (OperationCanceledException)
            {
                return "wayvrctl haptics timed out";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return "wayvrctl not found; install WayVR to use haptics with Monado";
            }
        }

        return null;
    }
}
