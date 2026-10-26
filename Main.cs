using AOSharp.Core;
using AOSharp.Core.UI;
using Newtonsoft.Json;
using Path = System.IO.Path;

public class Main : AOPluginEntry
{
    private static InventoryDb? _db;
    private InventoryHttpServer? _queries;
    private ToolkitApp? _app;
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

            _queries = new InventoryHttpServer(7068, _db);
            _app = ToolkitHub.Register("app_7068");

            // The page fetches over HTTP and re-fetches when told. It is told
            // after every committed change, and once on connect so a page that
            // opened before the plugin did catches up.
            var changed = JsonConvert.SerializeObject(new
            {
                type = "inventoryChanged",
                characterId = DynelManager.LocalPlayer.Identity.Instance,
            });
            var app = _app;
            _db.Changed += () => app.Send(changed);
            app.PageConnected += () => app.Send(changed);

            ItemTrack.Start(_db);
            _queries.Start();
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
        _queries?.Stop();
        _app?.Dispose();
    }
}