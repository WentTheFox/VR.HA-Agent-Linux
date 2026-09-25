using System.Diagnostics;
using SteamVRHAAgent.Protocol;
using Valve.VR;
using ClientFlags = SteamVRHAAgent.Monado.LibMonado.ClientFlags;

namespace SteamVRHAAgent.Monado;

/// <summary>
/// Reads runtime state from a running Monado (or WiVRn) service through libmonado. Connecting to Monado's
/// IPC socket can socket-activate the service, so the agent only calls <see cref="Update"/> with
/// serviceRunning=true while a service process already exists.
/// </summary>
public sealed class MonadoBackend(string? libMonadoPath) : IDisposable
{
    private static readonly string[] ServiceProcessNames = ["monado-service", "wivrn-server"];
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private LibMonado? _lib;
    private IntPtr _root;
    private DateTime _nextAttempt = DateTime.MinValue;
    private int? _failedServicePid;

    public bool IsConnected
    {
        get
        {
            lock (_lock) return _root != IntPtr.Zero;
        }
    }

    /// <summary>Raised after an established connection is lost.</summary>
    public event Action? Disconnected;

    /// <summary>Disconnects without raising <see cref="Disconnected"/> (the user switched runtimes).</summary>
    public void Close()
    {
        lock (_lock)
        {
            CloseRoot();
            _nextAttempt = DateTime.MinValue;
        }
    }

    /// <summary>Connects or disconnects to match whether the service is running. Call about once a second.</summary>
    public void Update(bool serviceRunning)
    {
        var lost = false;
        lock (_lock)
        {
            if (!serviceRunning)
            {
                lost = _root != IntPtr.Zero;
                CloseRoot();
                _nextAttempt = DateTime.MinValue;
            }
            else if (_root == IntPtr.Zero)
            {
                if (DateTime.UtcNow >= _nextAttempt) TryConnect();
            }
            else if (_lib!.UpdateClientList(_root) != LibMonado.Success)
            {
                Log.Warn("Lost connection to Monado");
                lost = true;
                CloseRoot();
            }
        }

        if (lost)
        {
            Log.Info("Disconnected from Monado");
            Disconnected?.Invoke();
        }
    }

    private static (int Pid, string? Executable)? FindService()
    {
        foreach (var name in ServiceProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process) return (process.Id, ExecutablePath(process.Id));
            }
        }

        return null;
    }

    /// <summary>
    /// The service's binary path. /proc/PID/exe is unreadable when the binary has file capabilities
    /// (monado-service is usually installed with cap_sys_nice), but the command line stays readable.
    /// </summary>
    private static string? ExecutablePath(int pid)
    {
        try
        {
            var target = new FileInfo($"/proc/{pid}/exe").LinkTarget;
            if (!string.IsNullOrEmpty(target)) return target;
        }
        catch (Exception)
        {
            // Fall through to the command line.
        }

        try
        {
            var argv0 = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0')[0];
            if (Path.IsPathRooted(argv0)) return argv0;
            if (argv0.Contains('/')) return null; // relative to a working directory we can't see

            // Bare name: resolve through PATH, like the shell that started it would have.
            return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')
                .Select(dir => Path.Combine(dir, argv0))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void TryConnect()
    {
        var service = FindService();
        if (service == null) return;
        // Every failed attempt makes libmonado print errors, so retry slowly (but immediately for a new service).
        if (service.Value.Pid != _failedServicePid) _nextAttempt = DateTime.MinValue;

        foreach (var path in LibMonado.CandidatePaths(libMonadoPath, service.Value.Executable).Distinct())
        {
            if (!File.Exists(path)) continue;
            var lib = LibMonado.TryLoad(Path.GetFullPath(path));
            if (lib == null) continue;

            if (lib.RootCreate(out var root) != LibMonado.Success || root == IntPtr.Zero)
            {
                Log.Debug($"{lib.Path} could not connect to the running service");
                continue;
            }

            _lib = lib;
            _root = root;
            _failedServicePid = null;
            _lib.UpdateClientList(_root);
            Log.Info($"Connected to Monado (pid {service.Value.Pid}) using {lib.Path}");
            return;
        }

        if (_failedServicePid != service.Value.Pid)
        {
            Log.Warn($"Could not connect to the Monado service (pid {service.Value.Pid}, {service.Value.Executable}). " +
                     "libmonado must come from the same Monado build as the service; set libMonadoPath in the " +
                     $"config if it isn't found automatically. Retrying every {RetryInterval.TotalSeconds:0}s.");
        }

        _failedServicePid = service.Value.Pid;
        _nextAttempt = DateTime.UtcNow + RetryInterval;
    }

    private void CloseRoot()
    {
        if (_root != IntPtr.Zero) _lib?.RootDestroy(ref _root);
        _root = IntPtr.Zero;
    }

    public State GetState(bool processRunning)
    {
        lock (_lock)
        {
            if (_root == IntPtr.Zero) return new State { IsSteamVRProcessRunning = processRunning };

            var app = GetForegroundApp();
            return new State
            {
                IsSteamVRProcessRunning = processRunning,
                // The Home Assistant integration treats this as "runtime connected".
                IsOpenVRConnected = true,
                // Monado has no proximity sensor data; an app with a focused session is the closest signal.
                HmdActivityLevel = app is { Focused: true }
                    ? EDeviceActivityLevel.k_EDeviceActivityLevel_UserInteraction
                    : EDeviceActivityLevel.k_EDeviceActivityLevel_Idle,
                CurrentApplicationKey = app?.Name,
                CurrentApplicationName = app?.Name,
                LeftController = GetController("left"),
                RightController = GetController("right"),
            };
        }
    }

    private sealed record ClientInfo(string Name, bool Focused);

    /// <summary>The focused non-overlay app, falling back to the primary app.</summary>
    private ClientInfo? GetForegroundApp()
    {
        var lib = _lib!;
        if (lib.GetNumberClients(_root, out var count) != LibMonado.Success) return null;

        ClientInfo? primary = null;
        for (uint i = 0; i < count; i++)
        {
            if (lib.GetClientIdAtIndex(_root, i, out var id) != LibMonado.Success) continue;
            if (lib.GetClientState(_root, id, out var flags) != LibMonado.Success) continue;
            if (flags.HasFlag(ClientFlags.SessionOverlay)) continue;
            if (lib.GetClientName(_root, id, out var name) != LibMonado.Success) continue;

            if (flags.HasFlag(ClientFlags.SessionFocused)) return new ClientInfo(name, true);
            if (flags.HasFlag(ClientFlags.PrimaryApp)) primary = new ClientInfo(name, false);
        }

        return primary;
    }

    private Controller GetController(string role)
    {
        var lib = _lib!;
        if (lib.GetDeviceFromRole(_root, role, out var index) != LibMonado.Success || index < 0)
            return new Controller(false);

        return lib.TryGetBatteryStatus(_root, (uint)index, out var charging, out var charge)
            ? new Controller(true, (int)Math.Round(charge * 100), charging)
            : new Controller(true);
    }

    public void Dispose()
    {
        lock (_lock) CloseRoot();
    }
}
