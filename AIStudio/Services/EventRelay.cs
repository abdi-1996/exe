using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AIStudio.Services;

public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();

    public async Task AcceptAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        _clients[id] = socket;
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        finally
        {
            _clients.TryRemove(id, out _);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
            socket.Dispose();
        }
    }

    public async Task BroadcastAsync(byte[] payload, WebSocketMessageType type, CancellationToken cancellationToken)
    {
        foreach (var item in _clients.ToArray())
        {
            try
            {
                if (item.Value.State == WebSocketState.Open)
                    await item.Value.SendAsync(payload, type, true, cancellationToken);
            }
            catch
            {
                _clients.TryRemove(item.Key, out _);
            }
        }
    }
}

public sealed class ComfyEventRelay : BackgroundService
{
    private readonly ComfyClient _client;
    private readonly EventHub _hub;
    private readonly JobStore _jobs;
    private readonly ILogger<ComfyEventRelay> _logger;

    public ComfyEventRelay(ComfyClient client, EventHub hub, JobStore jobs, ILogger<ComfyEventRelay> logger)
    {
        _client = client;
        _hub = hub;
        _jobs = jobs;
        _logger = logger;
    }

    public string ClientId { get; } = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var baseUri = await _client.GetBaseUriAsync(stoppingToken);
                var builder = new UriBuilder(baseUri)
                {
                    Scheme = baseUri.Scheme == "https" ? "wss" : "ws",
                    Path = "/ws",
                    Query = $"clientId={ClientId}"
                };
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(builder.Uri, stoppingToken);
                var buffer = new byte[4 * 1024 * 1024];
                while (socket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, stoppingToken);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    var payload = message.ToArray();
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        try { await _jobs.HandleComfyEventAsync(JsonNode.Parse(Encoding.UTF8.GetString(payload)), stoppingToken); }
                        catch (Exception exception) { _logger.LogDebug(exception, "Unable to process ComfyUI event"); }
                    }
                    await _hub.BroadcastAsync(payload, result.MessageType, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "ComfyUI event relay disconnected");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }
}
