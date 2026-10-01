using Microsoft.AnalysisServices;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Daxis.Core;

/// <summary>
/// Live read/write connection to a Fabric semantic model over the XMLA endpoint (HTTP, cross-platform).
/// Edits are local to the TOM model until <see cref="Save"/>.
/// </summary>
public sealed class ModelSession : IDisposable
{
    readonly TOM.Server _server = new();
    public TOM.Model Model { get; private set; } = null!;

    public static ModelSession Connect(string workspaceName, string modelId, string modelName, Func<(string Token, DateTimeOffset Expires)> token)
    {
        var s = new ModelSession();
        var t = token();
        s._server.AccessToken = new AccessToken(t.Token, t.Expires, null);
        s._server.OnAccessTokenExpired = _ => { var r = token(); return new AccessToken(r.Token, r.Expires, null); };
        s._server.Connect($"Data Source=powerbi://api.powerbi.com/v1.0/myorg/{Uri.EscapeDataString(workspaceName)};");

        // Match on the model's ID (the XMLA database ID is the dataset GUID); fall back to the name only when
        // exactly one database carries it, so a same-named model can never be picked by accident.
        var db = s._server.Databases.Find(modelId);
        if (db is null)
        {
            var named = s._server.Databases.Cast<TOM.Database>().Where(d => d.Name == modelName).ToList();
            db = named.Count == 1 ? named[0]
                : throw new InvalidOperationException($"Semantic model '{modelName}' ({modelId}) not found on the XMLA endpoint.");
        }
        s.Model = db.Model;
        return s;
    }

    public bool HasChanges => Model.HasLocalChanges;

    /// <summary>Read-only VertiPaq statistics from the engine's storage DMVs.</summary>
    public StorageInfo Storage() => ModelStorage.Compute(Query(StorageColumns), Query(StorageSegments));

    /// <summary>
    /// Scores the model against the best-practice rules. Pass storage the caller already read to skip re-reading it.
    /// Holds the connection throughout, so a save can't change the model mid-score.
    /// </summary>
    public QualityReport Quality(StorageInfo? storage = null)
    {
        lock (_server)
        {
            var stats = StorageStats.Empty;
            try
            {
                var columns = Query(StorageColumns);
                storage ??= ModelStorage.Compute(columns, Query(StorageSegments));
                stats = ModelStorage.Stats(columns, Query(StorageTables));
            }
            catch (Exception) { } // storage DMVs can be denied; the metadata rules still run
            return ModelQuality.Score(Model, storage, stats);
        }
    }

    const string StorageColumns = "SELECT DIMENSION_NAME, ATTRIBUTE_NAME, COLUMN_ID, COLUMN_TYPE, DICTIONARY_SIZE FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMNS";
    const string StorageSegments = "SELECT DIMENSION_NAME, TABLE_ID, COLUMN_ID, USED_SIZE FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS";
    const string StorageTables = "SELECT DIMENSION_NAME, TABLE_ID, ROWS_COUNT FROM $SYSTEM.DISCOVER_STORAGE_TABLES";

    // One call at a time: a TOM Server isn't safe for concurrent use, and tabs read storage, score and save in parallel.
    // Monitor locks are re-entrant, so Quality() can hold the lock across several queries.
    List<IReadOnlyDictionary<string, object?>> Query(string statement)
    {
        lock (_server) return QueryLocked(statement);
    }

    List<IReadOnlyDictionary<string, object?>> QueryLocked(string statement)
    {
        var props = new System.Collections.Hashtable { ["Catalog"] = Model.Database.Name };
        using var reader = _server.ExecuteReader($"<Statement>{System.Security.SecurityElement.Escape(statement)}</Statement>", out var results, props, true)
            ?? throw new InvalidOperationException(string.Join("; ", results.Cast<XmlaResult>()
                .SelectMany(r => r.Messages.Cast<XmlaMessage>()).Select(m => m.Description)) is { Length: > 0 } msg ? msg : "The storage query returned nothing.");
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Commits local edits. On failure they stay local so the user can fix or discard them.</summary>
    public void Save() { lock (_server) Model.SaveChanges(); }

    public void Discard() => Model.UndoLocalChanges();

    public void Refresh() { lock (_server) Model.Sync(new TOM.SyncOptions()); }

    public TOM.Measure AddMeasure(TOM.Table table)
    {
        var name = "New measure";
        for (var i = 2; Model.Tables.Any(t => t.Measures.ContainsName(name)); i++) name = $"New measure {i}";
        var m = new TOM.Measure { Name = name, Expression = "BLANK()" };
        table.Measures.Add(m);
        return m;
    }

    public TOM.CalculatedColumn AddCalculatedColumn(TOM.Table table)
    {
        var name = "New column";
        for (var i = 2; table.Columns.ContainsName(name); i++) name = $"New column {i}";
        var c = new TOM.CalculatedColumn { Name = name, Expression = "BLANK()" };
        table.Columns.Add(c);
        return c;
    }

    /// <summary>Names for IntelliSense: tables, columns per table, measures.</summary>
    public DaxSymbols Symbols() => new(
        Model.Tables.Select(t => t.Name).ToList(),
        Model.Tables.SelectMany(t => t.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber).Select(c => (t.Name, c.Name))).ToList(),
        Model.Tables.SelectMany(t => t.Measures.Select(m => m.Name)).ToList());

    /// <summary>Waits for any call in flight, then disconnects. A network call: keep it off the UI thread.</summary>
    public void Dispose()
    {
        lock (_server)
        {
            if (_server.Connected) _server.Disconnect();
            _server.Dispose();
        }
    }
}
