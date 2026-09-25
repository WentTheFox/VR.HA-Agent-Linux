using System.Diagnostics;

namespace VRHAAgent;

/// <summary>Which VR-related processes are running. /proc comm names are truncated to 15 characters.</summary>
public readonly record struct RuntimeProcesses(bool SteamVR, bool Monado, bool WiVRn, bool WayVR)
{
    private static readonly string[] SteamVRNames = ["vrserver", "vrmonitor", "vrcompositor"];

    public bool AnyRuntime => SteamVR || Monado || WiVRn;

    public static RuntimeProcesses Scan()
    {
        var names = new HashSet<string>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                names.Add(process.ProcessName);
            }
            catch (InvalidOperationException)
            {
                // Exited while we were looking.
            }
            finally
            {
                process.Dispose();
            }
        }

        return new RuntimeProcesses(
            SteamVR: SteamVRNames.Any(names.Contains),
            Monado: names.Contains("monado-service"),
            WiVRn: names.Contains("wivrn-server"),
            WayVR: names.Contains(global::VRHAAgent.Monado.WayVR.ProcessName));
    }
}
