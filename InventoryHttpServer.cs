using AOSharp.Common.GameData;
using AOSharp.Core.UI;
using Newtonsoft.Json;
using System.Net;
using System.Text;

/// <summary>
/// The inventory queries the page makes over HTTP on port 7068.
///
/// Every client writes the same database, so any one of them can answer. The
/// first client to take the port does; the others keep trying, so the queries
/// stay up while any client runs the plugin. Change notices go through
/// ToolkitHub, not here.
/// </summary>
public class InventoryHttpServer
{
    private readonly int _port;
    private readonly InventoryDb _db;
    private readonly CancellationTokenSource _cts = new();
    private HttpListener? _listener;
    private Task? _task;

    public InventoryHttpServer(int port, InventoryDb db)
    {
        _port = port;
        _db = db;
    }

    public void Start() => _task = Task.Run(RunAsync);

    public void Stop()
    {
        _cts.Cancel();
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        try { _task?.Wait(1000); } catch { }
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{_port}/");

            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                // Another client has the port. Try again in case it goes away.
                listener.Close();
                try { await Task.Delay(3000, _cts.Token); } catch (OperationCanceledException) { return; }
                continue;
            }

            _listener = listener;
            try
            {
                while (true)
                {
                    var ctx = await listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(ctx));
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
            return;
        }
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
}
