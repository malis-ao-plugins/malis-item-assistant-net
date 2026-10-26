using AOSharp.Core;
using AOSharp.Core.UI;
using Newtonsoft.Json;
using Path = System.IO.Path;

public class Main : AOPluginEntry
{
    private static InventoryDb? _db;
    private WebSocketServer _webServer;
    private WebPageServer? _page;
    public override void Run()
    {
        try
        {
            Chat.WriteLine("Mali's Item Assistant - Net");

            Directory.SetCurrentDirectory(PluginDirectory);

            SQLitePCL.Batteries_V2.Init();

            var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AOSharp", "MalisItemAssistant", "items.db");

            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            _db = new InventoryDb(dbPath);
            _db.UpsertCharacter(DynelManager.LocalPlayer.Identity.Instance, DynelManager.LocalPlayer.Name);

            _webServer = new WebSocketServer(7068, _db);

            // The page fetches over HTTP and re-fetches when told. It is told
            // after every committed change, and once on connect so a page that
            // opened before the plugin did catches up.
            var changed = JsonConvert.SerializeObject(new
            {
                type = "inventoryChanged",
                characterId = DynelManager.LocalPlayer.Identity.Instance,
            });
            _db.Changed += () => _webServer.Broadcast(changed);
            _webServer.OnClientConnected += () => _webServer.Broadcast(changed);

            ItemTrack.Start(_db);
            _webServer.Start();
            _page = new WebPageServer();
            _page.Start();
        }
        catch (Exception ex)
        {
            Chat.WriteLine($"[MIA] Failed to start: {ex.Message}");
        }
    }

    public override void Teardown()
    {
        ItemTrack.Stop();
        _db?.Dispose();
        _webServer?.Stop();
        _page?.Stop();
    }
}