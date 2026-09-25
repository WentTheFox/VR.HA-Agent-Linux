using System.Reflection;
using System.Runtime.InteropServices;

namespace SteamVRHAAgent.VR;

/// <summary>
/// Locates libopenvr_api.so. The client library is tiny and forwards to whatever runtime
/// ~/.config/openvr/openvrpaths.vrpath points at (SteamVR, xrizer, ...), so any copy works.
/// </summary>
public static class OpenVRLibrary
{
    private static string? _explicitPath;

    public static void Install(string? explicitPath)
    {
        _explicitPath = explicitPath;
        NativeLibrary.SetDllImportResolver(typeof(Valve.VR.OpenVR).Assembly, Resolve);
    }

    private static IEnumerable<string> Candidates()
    {
        if (!string.IsNullOrEmpty(_explicitPath)) yield return _explicitPath;
        var env = Environment.GetEnvironmentVariable("OPENVR_API_PATH");
        if (!string.IsNullOrEmpty(env)) yield return env;

        // System package (e.g. Arch `openvr`), then the linker's default search path.
        yield return "/usr/lib/libopenvr_api.so";
        yield return "/usr/lib64/libopenvr_api.so";
        yield return "/usr/local/lib/libopenvr_api.so";
        yield return "libopenvr_api.so";

        // Steam ships one as well.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var steam in new[] { ".local/share/Steam", ".steam/steam", ".steam/root",
                     ".var/app/com.valvesoftware.Steam/.local/share/Steam" })
        {
            yield return Path.Combine(home, steam, "ubuntu12_64/libopenvr_api.so");
            yield return Path.Combine(home, steam, "steamrt64/libopenvr_api.so");
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "openvr_api") return IntPtr.Zero;

        foreach (var candidate in Candidates())
        {
            if (candidate.Contains('/') && !File.Exists(candidate)) continue;
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                Log.Info($"Loaded OpenVR client library: {candidate}");
                return handle;
            }
        }

        Log.Error("Could not find libopenvr_api.so. Install your distro's openvr package, or set " +
                  "openVRLibraryPath in the config / OPENVR_API_PATH in the environment.");
        return IntPtr.Zero;
    }
}
