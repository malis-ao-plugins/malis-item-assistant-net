using AOSharp.Common.GameData;
using AOSharp.Core.UI;
using Newtonsoft.Json;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

public class WebSocketServer
{
    private readonly HttpListener _listener;
    private readonly List<WebSocket> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread _serverThread;
    private InventoryDb _db;

    public event Action OnClientConnected;
    public event Action OnStart;
    public event Action OnStop;
    public event Action<string> OnSaveLayout;

    private int _port;

    public WebSocketServer(int port = 7068, InventoryDb db = null)
    {
        _port = port;
        _db = db;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        _listener.Prefixes.Clear();
        _listener.Prefixes.Add($"http://localhost:{_port}/");

        _serverThread = new Thread(() =>
        {
            try
            {
                _listener.Start();
                while (_listener.IsListening)
                {
                    try
                    {
                        var ctx = _listener.GetContext();
                        if (ctx.Request.IsWebSocketRequest)
                            _ = HandleClientAsync(ctx);
                        else
                            ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                    }
                    catch (HttpListenerException) { break; }
                    catch (Exception ex) { Chat.WriteLine($"[WsServer] Accept error: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                Chat.WriteLine($"[WsServer] Failed to start: {ex.Message}");
            }
        });

        _serverThread.IsBackground = true;
        _serverThread.Start();
    }

    // ── HTTP route handler ───────────────────────────────────────────────────

    private void HandleRequest(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;

        try
        {
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Access-Control-Allow-Methods", "GET, OPTIONS");
            res.AddHeader("Access-Control-Allow-Headers", "*");
            res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                return;
            }

            var path = req.Url?.AbsolutePath ?? "";
            var query = req.QueryString;

            object? result = null;
            int statusCode = 200;
            string? error = null;

            if (path == "/characters")
            {
                result = _db.GetCharacters();
            }
            else if (path == "/character/items")
            {
                if (!int.TryParse(query["characterId"], out var characterId)) { statusCode = 400; error = "characterId required"; }
                else result = _db.GetCharacterItems(characterId);
            }
            else if (path == "/character/containers")
            {
                if (!int.TryParse(query["characterId"], out var characterId)) { statusCode = 400; error = "characterId required"; }
                else result = _db.GetCharacterContainers(characterId);
            }
            else if (path == "/container/items")
            {
                if (!int.TryParse(query["characterId"], out var characterId)) { statusCode = 400; error = "characterId required"; }
                else if (!int.TryParse(query["instance"], out var instance)) { statusCode = 400; error = "instance required"; }
                else if (!int.TryParse(query["type"], out var type)) { statusCode = 400; error = "type required"; }
                else result = _db.GetContainerItems(characterId, instance, (IdentityType)type);
            }
            else
            {
                statusCode = 404;
                error = "not found";
            }

            res.StatusCode = statusCode;
            res.ContentType = "application/json";
            var body = error != null
                ? JsonConvert.SerializeObject(new { error })
                : JsonConvert.SerializeObject(result);
            var bytes = Encoding.UTF8.GetBytes(body);
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            Chat.WriteLine($"HandleRequest error: {ex.Message}");
            res.StatusCode = 500;
        }
        finally
        {
            res.OutputStream.Close();
        }
    }

    // ── WebSocket broadcast ──────────────────────────────────────────────────

    public void Broadcast(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var client in _clients.ToList())
        {
            if (client.State == WebSocketState.Open)
                client.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
        }
    }

    public void Stop()
    {
        OnClientConnected = null;
        OnStart = null;
        OnStop = null;

        _cts.Cancel();

        foreach (var c in _clients)
        {
            try
            {
                if (c.State == WebSocketState.Open)
                    c.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", CancellationToken.None).Wait(500);
            }
            catch { }
            finally { c.Dispose(); }
        }

        _clients.Clear();
        _listener?.Stop();
        _listener?.Close();
    }

    private async Task HandleClientAsync(HttpListenerContext ctx)
    {
        var wsCtx = await ctx.AcceptWebSocketAsync(null);
        _clients.Add(wsCtx.WebSocket);
        OnClientConnected?.Invoke();
        var ws = wsCtx.WebSocket;
        var buffer = new byte[4096];

        while (ws.State == WebSocketState.Open)
        {
            try
            {
                var result = await ws.ReceiveAsync(buffer, _cts.Token);

                if (result.Count == 0 || result.MessageType == WebSocketMessageType.Close)
                    break;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                switch (root.GetProperty("type").GetString())
                {
                    // websocket message handlers go here
                }
            }
            catch (Exception ex)
            {
                Chat.WriteLine($"HandleClient error: {ex.Message}");
            }
        }

        _clients.Remove(ws);
    }
}