using AOSharp.Common.GameData;
using AOSharp.Core.UI;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

public class InventoryDb : IDisposable
{
    private readonly string _dbPath;

    private SqliteConnection? _batchCon;
    private SqliteTransaction? _batchTx;
    private Timer? _batchTimer;
    private readonly object _batchLock = new();

    private readonly BlockingCollection<(string sql, Dictionary<string, object?>? parameters)> _queue = new();

    public InventoryDb(string dbPath)
    {
        _dbPath = dbPath;
        InitSchema();
        Task.Run(ProcessQueue);
    }

    // ── Schema ───────────────────────────────────────────────────────────────

    private void InitSchema()
    {
        ExecuteInternal("PRAGMA journal_mode=WAL;");

        ExecuteInternal(@"
        CREATE TABLE IF NOT EXISTS character (
            id        INTEGER PRIMARY KEY,
            name      TEXT    NOT NULL,
            last_seen TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS container (
            character_id    INTEGER NOT NULL REFERENCES character(id),
            type            INTEGER NOT NULL,
            instance        INTEGER NOT NULL,
            name            TEXT,
            parent_type     INTEGER,
            parent_instance INTEGER,
            parent_slot     INTEGER,
            PRIMARY KEY (character_id, type, instance)
        );

        CREATE TABLE IF NOT EXISTS item_slot (
            character_id INTEGER NOT NULL REFERENCES character(id),
            slot         INTEGER NOT NULL,
            item_id      INTEGER NOT NULL,
            item_high_id INTEGER NOT NULL,
            ql           INTEGER NOT NULL,
            added_at     TEXT    NOT NULL,
            parent_type  INTEGER NOT NULL,
            parent_instance INTEGER NOT NULL,
            PRIMARY KEY (character_id, parent_type, parent_instance, slot),
            FOREIGN KEY (character_id, parent_type, parent_instance)
                REFERENCES container(character_id, type, instance)
        );

        CREATE INDEX IF NOT EXISTS idx_item_slot_item_id
            ON item_slot(item_id);

        CREATE INDEX IF NOT EXISTS idx_item_slot_character
            ON item_slot(character_id);
    ");
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public void UpsertCharacter(int characterId, string name)
    {
        Execute(
            @"INSERT INTO character(id, name, last_seen)
              VALUES(@id, @name, @now)
              ON CONFLICT(id) DO UPDATE SET name=excluded.name, last_seen=excluded.last_seen",
            new() { ["@id"] = characterId, ["@name"] = name, ["@now"] = UtcNow() });
    }

    public void EnsureContainer(int characterId, Identity containerIdentity, string? name = null)
    {
        Execute(
            @"INSERT INTO container(character_id, instance, type, name)
              VALUES(@cid, @inst, @type, @name)
              ON CONFLICT(character_id, instance, type) DO UPDATE SET
                  name = COALESCE(excluded.name, container.name)",
            new() { ["@cid"] = characterId, ["@inst"] = containerIdentity.Instance, ["@type"] = (int)containerIdentity.Type, ["@name"] = name! });
    }

    public void UpdateContainerParent(int characterId, Identity container, Identity parentContainer, int parentSlot)
    {
        Execute(
            @"UPDATE container
              SET parent_instance=@pi, parent_type=@pt, parent_slot=@ps
              WHERE character_id=@cid AND instance=@inst AND type=@type",
            new()
            {
                ["@cid"] = characterId,
                ["@inst"] = container.Instance,
                ["@type"] = (int)container.Type,
                ["@pi"] = parentContainer.Instance,
                ["@pt"] = (int)parentContainer.Type,
                ["@ps"] = parentSlot
            });
    }

    /// <summary>
    /// Returns the full path from a root container down to the target item slot.
    /// Each entry is (containerInstance, containerType, slotWithinThatContainer).
    /// Walk the list in order to navigate to the item.
    /// </summary>
    public List<(int instance, IdentityType type, int slot)> GetItemPath(int characterId, int containerInstance, IdentityType containerType, int slot)
    {
        var rows = Query(
            @"WITH RECURSIVE path(type, instance, parent_type, parent_instance, parent_slot, depth) AS (
              SELECT type, instance, parent_type, parent_instance, parent_slot, 0
              FROM container
              WHERE character_id=@cid AND type=@ctype AND instance=@inst

              UNION ALL

              SELECT c.type, c.instance, c.parent_type, c.parent_instance, c.parent_slot, p.depth+1
              FROM container c
              JOIN path p ON c.type=p.parent_type AND c.instance=p.parent_instance
              WHERE c.character_id=@cid
          )
          SELECT type, instance, parent_slot, depth FROM path ORDER BY depth DESC",
            new() { ["@cid"] = characterId, ["@ctype"] = (int)containerType, ["@inst"] = containerInstance });

        var path = rows.Select(r => (
            Convert.ToInt32(r["instance"]),
            (IdentityType)Convert.ToInt32(r["type"]),
            Convert.ToInt32(r["parent_slot"])
        )).ToList();

        path.Add((containerInstance, containerType, slot));
        return path;
    }

    public void ItemAdded(int characterId, ItemInfo item)
    {
        EnsureContainer(characterId, item.ParentContainer);

        Execute(
            @"INSERT INTO item_slot(character_id, slot, item_id, item_high_id, ql, added_at, parent_type, parent_instance)
          VALUES(@cid, @slot, @itemId, @itemHighId, @ql, @now, @ptype, @pinst)
          ON CONFLICT(character_id, parent_type, parent_instance, slot) DO UPDATE SET
              item_id      = excluded.item_id,
              item_high_id = excluded.item_high_id,
              ql           = excluded.ql,
              added_at     = excluded.added_at",
            new()
            {
                ["@cid"] = characterId,
                ["@slot"] = item.Slot,
                ["@itemId"] = item.Id,
                ["@itemHighId"] = item.HighId,
                ["@ql"] = item.Quality,
                ["@now"] = UtcNow(),
                ["@ptype"] = (int)item.ParentContainer.Type,
                ["@pinst"] = item.ParentContainer.Instance
            });

        if (item.UniqueIdentity.Type == IdentityType.Container)
        {
            var identity = new Identity(IdentityType.Backpack, item.UniqueIdentity.Instance);
            EnsureContainer(characterId, identity);
            UpdateContainerParent(characterId, identity, item.ParentContainer, item.Slot);
        }
    }

    public void ItemRemoved(int characterId, Identity slotIdentity, Dictionary<int, int> handleToIdentity)
    {
        var (parentInstance, parentType, slot) = ResolveSlotForRemoval(slotIdentity, characterId, handleToIdentity);

        Execute(
            @"DELETE FROM item_slot
          WHERE character_id    = @cid
            AND parent_type     = @ptype
            AND parent_instance = @pinst
            AND slot            = @slot",
            new()
            {
                ["@cid"] = characterId,
                ["@ptype"] = (int)parentType,
                ["@pinst"] = parentInstance,
                ["@slot"] = slot
            });
    }
    public List<Dictionary<string, object?>> GetCharacters()
    {
        return Query("SELECT id, name, last_seen FROM character ORDER BY name");
    }

    public List<Dictionary<string, object?>> GetCharacterItems(int characterId)
    {
        return Query(
            @"SELECT s.parent_type, s.parent_instance, s.slot,
                 s.item_id, s.item_high_id, s.ql,
                 c.name AS container_name
          FROM item_slot s
          LEFT JOIN container c
            ON  c.character_id = s.character_id
            AND c.type         = s.parent_type
            AND c.instance     = s.parent_instance
          WHERE s.character_id = @cid
          ORDER BY s.parent_instance, s.slot",
            new() { ["@cid"] = characterId });
    }

    public List<Dictionary<string, object?>> GetCharacterContainers(int characterId)
    {
        return Query(
            @"SELECT type, instance, name, parent_type, parent_instance, parent_slot
              FROM container
              WHERE character_id = @cid
              ORDER BY instance",
            new() { ["@cid"] = characterId });
    }

    public List<Dictionary<string, object?>> GetContainerItems(int characterId, int containerInstance, IdentityType containerType)
    {
        return Query(
            @"SELECT slot, item_id, item_high_id, ql, added_at
          FROM item_slot
          WHERE character_id    = @cid
            AND parent_type     = @ptype
            AND parent_instance = @pinst
          ORDER BY slot",
            new() { ["@cid"] = characterId, ["@ptype"] = (int)containerType, ["@pinst"] = containerInstance });
    }

    private (int parentInstance, IdentityType parentType, int slot) ResolveSlotForRemoval(Identity slotIdentity, int characterId, Dictionary<int, int> handleToIdentity)
    {
        if (slotIdentity.Type == IdentityType.Backpack)
        {
            int handle = (slotIdentity.Instance >> 16) & 0xFFFF;
            int slot = slotIdentity.Instance & 0xFFFF;

            if (handleToIdentity.TryGetValue(handle, out var realInstance))
                return (realInstance, IdentityType.Backpack, slot);

            var rows = Query(
                @"SELECT parent_instance FROM item_slot
              WHERE character_id  = @cid
                AND parent_type   = @ptype
                AND slot          = @slot
              LIMIT 1",
                new() { ["@cid"] = characterId, ["@ptype"] = (int)IdentityType.Backpack, ["@slot"] = slot });

            int fallback = rows.Count > 0
                ? Convert.ToInt32(rows[0]["parent_instance"])
                : handle;

            return (fallback, IdentityType.Backpack, slot);
        }

        return (characterId, slotIdentity.Type, slotIdentity.Instance);
    }

    // ── Background writer ────────────────────────────────────────────────────

    private void ProcessQueue()
    {
        foreach (var (sql, parameters) in _queue.GetConsumingEnumerable())
        {
            lock (_batchLock)
            {
                var con = BeginOrJoinBatch();
                using var cmd = con.CreateCommand();
                cmd.Transaction = _batchTx;
                cmd.CommandText = sql;
                if (parameters != null)
                    foreach (var kvp in parameters)
                        cmd.Parameters.AddWithValue(kvp.Key, kvp.Value ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        // Queue completed — flush whatever is left
        FlushBatch();
    }

    // ── Batch transaction ────────────────────────────────────────────────────

    private void FlushBatch()
    {
        lock (_batchLock)
        {
            _batchTx?.Commit();
            _batchTx?.Dispose();
            _batchCon?.Dispose();
            _batchTimer?.Dispose();
            _batchTx = null;
            _batchCon = null;
            _batchTimer = null;
        }
    }

    private SqliteConnection BeginOrJoinBatch()
    {
        if (_batchCon == null)
        {
            _batchCon = OpenConnection();
            _batchTx = _batchCon.BeginTransaction();
            _batchTimer = new Timer(_ => FlushBatch(), null, 50, Timeout.Infinite);
        }
        else
        {
            _batchTimer?.Change(50, Timeout.Infinite); // reset window
        }

        return _batchCon;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string UtcNow() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private void Execute(string sql, Dictionary<string, object?>? parameters = null) => _queue.Add((sql, parameters));

    private void ExecuteInternal(string sql, Dictionary<string, object?>? parameters = null)
    {
        using var con = OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        if (parameters != null)
            foreach (var kvp in parameters)
                cmd.Parameters.AddWithValue(kvp.Key, kvp.Value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private List<Dictionary<string, object?>> Query(string sql, Dictionary<string, object?>? parameters = null)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var con = OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        if (parameters != null)
            foreach (var kvp in parameters)
                cmd.Parameters.AddWithValue(kvp.Key, kvp.Value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private SqliteConnection OpenConnection()
    {
        var con = new SqliteConnection($"Data Source={_dbPath};");
        con.Open();
        return con;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        // ProcessQueue will flush on exit; give it a moment to finish
        Thread.Sleep(100);
        SqliteConnection.ClearAllPools();
    }
}