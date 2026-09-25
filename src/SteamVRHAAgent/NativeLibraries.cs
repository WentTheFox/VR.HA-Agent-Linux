using System.Reflection;
using System.Runtime.InteropServices;

namespace SteamVRHAAgent;

/// <summary>
/// Locates libopenvr_api.so on first use. (libmonado is loaded separately, see Monado/LibMonado.cs, because
/// it has to match the running Monado build.)
/// </summary>
public static class NativeLibraries
{
    public const string OpenVR = "openvr_api";

    private static string? _openVRPath;

    public static void Install(string? openVRPath)
    {
        _openVRPath = openVRPath;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static IEnumerable<string> OpenVRCandidates()
    {
        if (!string.IsNullOrEmpty(_openVRPath)) yield return _openVRPath;
        var env = Environment.GetEnvironmentVariable("OPENVR_API_PATH");
        if (!string.IsNullOrEmpty(env)) yield return env;

        // The client library is tiny and forwards to whatever ~/.config/openvr/openvrpaths.vrpath points at,
        // so any copy works: the system package (e.g. Arch `openvr`) first, then Steam's.
        yield return "/usr/lib/libopenvr_api.so";
        yield return "/usr/lib64/libopenvr_api.so";
        yield return "/usr/local/lib/libopenvr_api.so";
        yield return "libopenvr_api.so";

        foreach (var steam in new[] { ".local/share/Steam", ".steam/steam", ".steam/root",
                     ".var/app/com.valvesoftware.Steam/.local/share/Steam" })
        {
            yield return Path.Combine(Home, steam, "ubuntu12_64/libopenvr_api.so");
            yield return Path.Combine(Home, steam, "steamrt64/libopenvr_api.so");
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != OpenVR) return IntPtr.Zero;

        foreach (var candidate in OpenVRCandidates())
        {
            if (candidate.Contains('/') && !File.Exists(candidate)) continue;
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                Log.Info($"Loaded {libraryName} library: {candidate}");
                return handle;
            }
        }

        Log.Error("Could not find libopenvr_api.so. Install your distro's openvr package, or set " +
                  "openVRLibraryPath in the config / OPENVR_API_PATH in the environment.");
        return IntPtr.Zero;
    }
}
