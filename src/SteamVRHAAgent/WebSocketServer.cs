using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace SteamVRHAAgent;

public sealed class WebSocketClient(WebSocket socket, string id, string remote)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string Id { get; } = id;
    public string Remote { get; } = remote;
    public WebSocket Socket { get; } = socket;
    public bool IsOpen => Socket.State == WebSocketState.Open;

    public async Task SendAsync(string message)
    {
        if (!IsOpen) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        await _sendLock.WaitAsync();
        try
        {
            if (IsOpen)
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException)
        {
            Log.Debug($"Send to {Id} failed: {e.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }
}

public sealed class WebSocketServer(string bindAddress, int port)
{
    private const int MaxMessageBytes = 32 * 1024 * 1024; // base64 images can be large

    private readonly ConcurrentDictionary<string, WebSocketClient> _clients = new();
    private HttpListener? _listener;

    public Func<WebSocketClient, string, Task> MessageReceived { get; set; } = (_, _) => Task.CompletedTask;
    public Action<WebSocketClient> ClientDisconnected { get; set; } = _ => { };

    public int ClientCount => _clients.Count;

    public void Start(CancellationToken token)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{bindAddress}:{port}/");
        _listener.Start();
        Log.Info($"WebSocket server listening on ws://{(bindAddress is "+" or "*" ? "0.0.0.0" : bindAddress)}:{port}/");
        _ = AcceptLoop(_listener, token);
    }

    public async Task StopAsync()
    {
        foreach (var client in _clients.Values)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.Socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Agent shutting down", cts.Token);
            }
            catch (Exception e)
            {
                Log.Debug($"Error closing {client.Id}: {e.Message}");
            }
        }

        _listener?.Close();
        _listener = null;
    }

    public void Broadcast(string message)
    {
        foreach (var client in _clients.Values) _ = client.SendAsync(message);
    }

    /// <summary>Sends to one client, or to everyone if that client has gone away (matches the original agent).</summary>
    public void Send(WebSocketClient? client, string message)
    {
        if (client is { IsOpen: true }) _ = client.SendAsync(message);
        else Broadcast(message);
    }

    private async Task AcceptLoop(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            _ = HandleContext(context, token);
        }
    }

    private async Task HandleContext(HttpListenerContext context, CancellationToken token)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 426;
            var body = "Home Assistant Agent for SteamVR: connect using WebSocket.\n"u8.ToArray();
            context.Response.ContentType = "text/plain";
            await context.Response.OutputStream.WriteAsync(body, token);
            context.Response.Close();
            return;
        }

        WebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(null);
        }
        catch (Exception e)
        {
            Log.Warn($"WebSocket handshake failed: {e.Message}");
            context.Response.StatusCode = 500;
            context.Response.Close();
            return;
        }

        var client = new WebSocketClient(wsContext.WebSocket, Guid.NewGuid().ToString(),
            context.Request.RemoteEndPoint?.ToString() ?? "?");
        _clients[client.Id] = client;
        Log.Info($"Client connected: {client.Remote} ({_clients.Count} total)");

        try
        {
            await ReceiveLoop(client, token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            Log.Debug($"Client {client.Remote} receive ended: {e.Message}");
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            Log.Info($"Client disconnected: {client.Remote} ({_clients.Count} total)");
            ClientDisconnected(client);
            client.Socket.Dispose();
        }
    }

    private async Task ReceiveLoop(WebSocketClient client, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (client.IsOpen && !token.IsCancellationRequested)
        {
            var result = await client.Socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await client.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                await client.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too big", CancellationToken.None);
                return;
            }

            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            try
            {
                await MessageReceived(client, text);
            }
            catch (Exception e)
            {
                Log.Error($"Unhandled error processing message: {e}");
            }
        }
    }
}
