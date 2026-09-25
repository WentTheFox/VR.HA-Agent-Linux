using System.Runtime.InteropServices;

namespace SteamVRHAAgent.Monado;

/// <summary>
/// Bindings for libmonado (monado.h, API 1.x), loaded at runtime from a specific path. libmonado refuses to
/// talk to a service from a different Monado build, so the library has to come from the same build or
/// prefix as the running monado-service (see <see cref="CandidatePaths"/>).
/// </summary>
internal sealed unsafe class LibMonado
{
    public const int Success = 0;

    [Flags]
    public enum ClientFlags : uint
    {
        PrimaryApp = 1u << 0,
        SessionActive = 1u << 1,
        SessionVisible = 1u << 2,
        SessionFocused = 1u << 3,
        SessionOverlay = 1u << 4,
        IoActive = 1u << 5,
    }

    private static readonly Dictionary<string, LibMonado?> Loaded = new();

    private readonly delegate* unmanaged<uint*, uint*, uint*, void> _getVersion;
    private readonly delegate* unmanaged<IntPtr*, int> _rootCreate;
    private readonly delegate* unmanaged<IntPtr*, void> _rootDestroy;
    private readonly delegate* unmanaged<IntPtr, int> _updateClientList;
    private readonly delegate* unmanaged<IntPtr, uint*, int> _getNumberClients;
    private readonly delegate* unmanaged<IntPtr, uint, uint*, int> _getClientIdAtIndex;
    private readonly delegate* unmanaged<IntPtr, uint, IntPtr*, int> _getClientName;
    private readonly delegate* unmanaged<IntPtr, uint, uint*, int> _getClientState;
    private readonly delegate* unmanaged<IntPtr, byte*, int*, int> _getDeviceFromRole;
    private readonly delegate* unmanaged<IntPtr, uint, byte*, byte*, float*, int> _getBatteryStatus; // optional

    public string Path { get; }

    private LibMonado(string path, IntPtr handle)
    {
        Path = path;
        _getVersion = (delegate* unmanaged<uint*, uint*, uint*, void>)NativeLibrary.GetExport(handle, "mnd_api_get_version");
        _rootCreate = (delegate* unmanaged<IntPtr*, int>)NativeLibrary.GetExport(handle, "mnd_root_create");
        _rootDestroy = (delegate* unmanaged<IntPtr*, void>)NativeLibrary.GetExport(handle, "mnd_root_destroy");
        _updateClientList = (delegate* unmanaged<IntPtr, int>)NativeLibrary.GetExport(handle, "mnd_root_update_client_list");
        _getNumberClients = (delegate* unmanaged<IntPtr, uint*, int>)NativeLibrary.GetExport(handle, "mnd_root_get_number_clients");
        _getClientIdAtIndex = (delegate* unmanaged<IntPtr, uint, uint*, int>)NativeLibrary.GetExport(handle, "mnd_root_get_client_id_at_index");
        _getClientName = (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)NativeLibrary.GetExport(handle, "mnd_root_get_client_name");
        _getClientState = (delegate* unmanaged<IntPtr, uint, uint*, int>)NativeLibrary.GetExport(handle, "mnd_root_get_client_state");
        _getDeviceFromRole = (delegate* unmanaged<IntPtr, byte*, int*, int>)NativeLibrary.GetExport(handle, "mnd_root_get_device_from_role");
        if (NativeLibrary.TryGetExport(handle, "mnd_root_get_device_battery_status", out var battery))
            _getBatteryStatus = (delegate* unmanaged<IntPtr, uint, byte*, byte*, float*, int>)battery;
    }

    /// <summary>Loads (once) the library at <paramref name="path"/>; null if it can't be used.</summary>
    public static LibMonado? TryLoad(string path)
    {
        if (Loaded.TryGetValue(path, out var cached)) return cached;

        LibMonado? lib = null;
        if (NativeLibrary.TryLoad(path, out var handle))
        {
            try
            {
                lib = new LibMonado(path, handle);
                lib.GetVersion(out var major, out var minor);
                if (major != 1)
                {
                    Log.Warn($"{path}: unsupported libmonado API {major}.{minor} (need 1.x)");
                    lib = null;
                }
            }
            catch (EntryPointNotFoundException e)
            {
                Log.Warn($"{path} is not a usable libmonado: {e.Message}");
                lib = null;
            }
        }

        Loaded[path] = lib;
        return lib;
    }

    /// <summary>
    /// Where to look for libmonado, best match first: explicit override, next to the running service binary
    /// (install prefix or build tree, as used by Envision), then system locations.
    /// </summary>
    public static IEnumerable<string> CandidatePaths(string? explicitPath, string? serviceExecutable)
    {
        if (!string.IsNullOrEmpty(explicitPath)) yield return explicitPath;
        var env = Environment.GetEnvironmentVariable("LIBMONADO_PATH");
        if (!string.IsNullOrEmpty(env)) yield return env;

        if (serviceExecutable != null)
        {
            var dir = System.IO.Path.GetDirectoryName(serviceExecutable)!;
            yield return System.IO.Path.Combine(dir, "../lib/libmonado.so");
            yield return System.IO.Path.Combine(dir, "../lib64/libmonado.so");
            yield return System.IO.Path.Combine(dir, "../lib/wivrn/libmonado.so");
            yield return System.IO.Path.Combine(dir, "../libmonado/libmonado.so"); // build tree: targets/service -> targets/libmonado
        }

        yield return "/usr/lib/libmonado.so";
        yield return "/usr/lib64/libmonado.so";
        yield return "/usr/local/lib/libmonado.so";
    }

    public void GetVersion(out uint major, out uint minor)
    {
        uint ma, mi, pa;
        _getVersion(&ma, &mi, &pa);
        major = ma;
        minor = mi;
    }

    public int RootCreate(out IntPtr root)
    {
        IntPtr r;
        var result = _rootCreate(&r);
        root = r;
        return result;
    }

    public void RootDestroy(ref IntPtr root)
    {
        var r = root;
        _rootDestroy(&r);
        root = r;
    }

    public int UpdateClientList(IntPtr root) => _updateClientList(root);

    public int GetNumberClients(IntPtr root, out uint count)
    {
        uint c;
        var result = _getNumberClients(root, &c);
        count = c;
        return result;
    }

    public int GetClientIdAtIndex(IntPtr root, uint index, out uint id)
    {
        uint i;
        var result = _getClientIdAtIndex(root, index, &i);
        id = i;
        return result;
    }

    public int GetClientName(IntPtr root, uint id, out string name)
    {
        IntPtr p;
        var result = _getClientName(root, id, &p);
        name = result == Success ? Marshal.PtrToStringUTF8(p) ?? "" : "";
        return result;
    }

    public int GetClientState(IntPtr root, uint id, out ClientFlags flags)
    {
        uint f;
        var result = _getClientState(root, id, &f);
        flags = (ClientFlags)f;
        return result;
    }

    public int GetDeviceFromRole(IntPtr root, string role, out int index)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(role + "\0");
        int i;
        int result;
        fixed (byte* p = bytes) result = _getDeviceFromRole(root, p, &i);
        index = i;
        return result;
    }

    public bool TryGetBatteryStatus(IntPtr root, uint device, out bool charging, out float charge)
    {
        charging = false;
        charge = 0;
        if (_getBatteryStatus == null) return false;
        byte present, isCharging;
        float c;
        if (_getBatteryStatus(root, device, &present, &isCharging, &c) != Success || present == 0) return false;
        charging = isCharging != 0;
        charge = c;
        return true;
    }
}
