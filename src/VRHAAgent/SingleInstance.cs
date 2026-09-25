using System.Net.Sockets;
using System.Text;

namespace VRHAAgent;

/// <summary>
/// One agent per user. A second launch (from the app menu, a terminal, or SteamVR) hands its activation to the
/// running instance over a Unix socket and exits, like the Windows app's AppInstance redirection.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string ActivationShow = "show";
    public const string ActivationSteamVR = "steamvr";

    private static string SocketPath => Path.Combine(Paths.RuntimeDir, Paths.AppId + ".sock");

    private readonly FileStream _lock;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();

    public event Action<string>? Activated;

    private SingleInstance(FileStream lockFile)
    {
        _lock = lockFile;
        File.Delete(SocketPath);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        _listener.Listen(4);
        _ = AcceptLoop();
    }

    /// <summary>
    /// Returns the instance if we are the first one, or null after activating the running one. With a
    /// <paramref name="wait"/> (used when restarting), keeps trying while the previous instance shuts down.
    /// </summary>
    public static SingleInstance? TryAcquire(string activation, TimeSpan wait = default)
    {
        Directory.CreateDirectory(Paths.RuntimeDir);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                var lockFile = new FileStream(Paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new SingleInstance(lockFile);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
            catch (IOException)
            {
                Signal(activation);
                return null;
            }
        }
    }

    private static void Signal(string activation)
    {
        // The running instance may have only just started (e.g. the app menu launcher starts the service
        // right before launching us), so give it a moment to open its socket.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
                socket.Send(Encoding.UTF8.GetBytes(activation + "\n"));
                return;
            }
            catch (SocketException e)
            {
                if (attempt >= 25)
                {
                    Log.Warn($"Could not activate the running instance: {e.Message}");
                    return;
                }

                Thread.Sleep(200);
            }
        }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptAsync(_cts.Token);
                var buffer = new byte[256];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var read = await client.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
                var activation = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                Log.Debug($"Activated by another launch: {activation}");
                Activated?.Invoke(activation);
            }
            catch (OperationCanceledException)
            {
                if (_cts.IsCancellationRequested) return;
            }
            catch (SocketException)
            {
                if (_cts.IsCancellationRequested) return;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Dispose();
        try
        {
            File.Delete(SocketPath);
        }
        catch (IOException)
        {
        }

        _lock.Dispose();
    }
}
