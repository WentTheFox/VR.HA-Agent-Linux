using System.Diagnostics;
using SteamVRHAAgent.VR;

namespace SteamVRHAAgent.UI;

/// <summary>"Start with login" through the systemd user service installed by install.sh.</summary>
public static class StartWithLogin
{
    private static async Task<(int ExitCode, string Output)> Systemctl(params string[] args)
    {
        var info = new ProcessStartInfo("systemctl") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--user");
        foreach (var arg in args) info.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(info)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, output.Trim());
        }
        catch (Exception e)
        {
            return (-1, e.Message);
        }
    }

    /// <summary>Null when the service isn't installed (the toggle is then disabled).</summary>
    public static async Task<bool?> IsEnabledAsync()
    {
        var (code, output) = await Systemctl("is-enabled", SteamVRManifest.SystemdUnit);
        return output switch
        {
            "enabled" => true,
            "disabled" => false,
            _ => code == 0 ? true : null,
        };
    }

    public static async Task<bool> SetEnabledAsync(bool enabled)
    {
        // Plain enable/disable, not --now: the running instance may be the service itself.
        var (code, _) = await Systemctl(enabled ? "enable" : "disable", SteamVRManifest.SystemdUnit);
        return code == 0;
    }
}
