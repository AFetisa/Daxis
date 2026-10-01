using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daxis.Core;
using Microsoft.AnalysisServices.Tabular;

namespace Daxis.App;

public enum ObjKind { Measure, CalculatedColumn, Column, MQuery, DaxTable, DirectLake, NativeQuery, SharedQuery, Parameter }

public abstract partial class Node : ObservableObject
{
    [ObservableProperty] private bool _isExpanded;
}

/// <summary>A table, or the "Shared queries" group (Table is null).</summary>
public sealed class GroupNode(string name, Geometry icon, Table? table, List<ObjNode> children, string count) : Node
{
    public string Name { get; } = name;
    public Geometry Icon { get; } = icon;
    public Table? Table { get; } = table;
    public List<ObjNode> Children { get; } = children;
    public string Count { get; } = count;
}

public sealed partial class ObjNode(NamedMetadataObject obj, Table? table, ObjKind kind) : Node
{
    public NamedMetadataObject Obj { get; } = obj;
    public Table? Table { get; } = table;
    public ObjKind Kind { get; } = kind;
    public string Name => Obj.Name;

    public string Tag => Kind switch
    {
        ObjKind.Measure => "measure",
        ObjKind.CalculatedColumn => "calc column",
        ObjKind.Column => "column",
        ObjKind.MQuery => "Power Query",
        ObjKind.DaxTable => "DAX table",
        ObjKind.DirectLake => "Direct Lake",
        ObjKind.NativeQuery => "native query",
        ObjKind.SharedQuery => "M query",
        _ => "parameter",
    };

    public Geometry Icon => Kind switch
    {
        ObjKind.Measure => Icons.Measure,
        ObjKind.CalculatedColumn => Icons.CalcColumn,
        ObjKind.Column => Icons.Column,
        ObjKind.DirectLake => Icons.Lakehouse,
        ObjKind.Parameter => Icons.Param,
        ObjKind.DaxTable => Icons.Table,
        _ => Icons.Query,
    };

    public bool IsDax => Kind is ObjKind.Measure or ObjKind.CalculatedColumn or ObjKind.DaxTable;
    public bool IsSource => Kind is ObjKind.MQuery or ObjKind.DaxTable or ObjKind.DirectLake or ObjKind.NativeQuery;

    public void Renamed() => OnPropertyChanged(nameof(Name));
}

/// <summary>One analysed report: counts from its definition, or why it couldn't be read.</summary>
public sealed record ReportRow(ReportRef Ref, ReportStats? Stats, string Status, string Model = "")
{
    public string Name => Ref.Name;
    public string WorkspaceName => Ref.WorkspaceName;
    public string Pages => Stats?.Pages.ToString() ?? "";
    public string Visuals => Stats?.Visuals.ToString() ?? "";
    public string Slicers => Stats?.Slicers.ToString() ?? "";
    public string Filters => Stats?.Filters.ToString() ?? "";
    public string Fields => Stats?.Fields.Count.ToString() ?? "";
}

public sealed record TypeShare(string Name, int Count, double Share);

/// <summary>A column on the Memory page: where its bytes go, its share of the model, and whether reports use it.</summary>
public sealed record ColumnMemoryRow(string Table, string Column, ColumnStorage Storage, double Share, string Status)
{
    public long Bytes => Storage.Total;
    public string Size => ModelStorage.Format(Storage.Total);
    public string Dictionary => ModelStorage.Format(Storage.Dictionary);
    public string Data => ModelStorage.Format(Storage.Data);
    public string Hierarchy => ModelStorage.Format(Storage.Hierarchy);
    public string Percent => $"{Share:P1}";
}

public sealed partial class ModelTab(FabricItem item, Workspace ws, Auth auth, FabricClient fabric, Func<IReadOnlyList<Workspace>> workspaces)
    : TabBase(item, ws)
{
    ModelSession? _session;
    readonly ChangeTracker _tracker = new();
    ObjNode? _current;
    bool _loading;
    Table? _selectedTable;

    public override Geometry Icon => Icons.Model;
    public override string WebUrl => $"https://app.fabric.microsoft.com/groups/{Workspace.Id}/datasets/{Item.Id}";

    public ObservableCollection<GroupNode> Groups { get; } = [];
    public DaxSymbols Symbols { get; private set; } = DaxSymbols.Empty;

    [ObservableProperty] private object? _selectedNode;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private int _page; // 0 overview, 1 editor, 2 diagram, 3 lineage, 4 report usage, 5 memory, 6 quality

    // ── Selected object ──────────────────────────────────────────────────────
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _expression = "";
    [ObservableProperty] private string _formatString = "";
    [ObservableProperty] private string _displayFolder = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _isHidden;
    [ObservableProperty] private string _kind = "";
    [ObservableProperty] private string _tableName = "";
    [ObservableProperty] private string? _objectError;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _canEditExpression;
    [ObservableProperty] private bool _canRename;
    [ObservableProperty] private bool _canDelete;
    [ObservableProperty] private bool _hasProperties;
    [ObservableProperty] private bool _isDaxObject;
    [ObservableProperty] private string _language = "DAX";
    [ObservableProperty] private string? _sourceInfo;
    [ObservableProperty] private string _editorHint = "Select an object in the explorer.";
    public ObservableCollection<MStep> Steps { get; } = [];
    public event Action<string>? StepRequested;

    public string FormatLabel => Language == "M" ? "Format M" : "Format DAX";
    partial void OnLanguageChanged(string value) => OnPropertyChanged(nameof(FormatLabel));

    // ── Overview ─────────────────────────────────────────────────────────────
    [ObservableProperty] private ModelSummary? _summary;
    public ObservableCollection<ReportRef> Reports { get; } = [];
    [ObservableProperty] private string _reportsInfo = "Loading…";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _scannedAll;

    // ── Diagram, lineage, report usage ───────────────────────────────────────
    [ObservableProperty] private Graph? _diagram;
    [ObservableProperty] private Graph? _lineage;
    public ObservableCollection<ReportRow> ReportRows { get; } = [];
    public ObservableCollection<TypeShare> VisualTypes { get; } = [];
    public ObservableCollection<FieldUsage> Fields { get; } = [];
    [ObservableProperty] private UsageCoverage? _coverage;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private int _totalVisuals;
    [ObservableProperty] private int _totalSlicers;
    [ObservableProperty] private int _totalFilters;
    [ObservableProperty] private int _analysedReports;
    [ObservableProperty] private bool _isAnalysing;
    [ObservableProperty] private string _usageInfo = "";
    [ObservableProperty] private int _fieldFilter; // 0 all, 1 used in reports, 2 model only, 3 unused
    [ObservableProperty] private string _fieldSearch = "";
    // ── Storage and refresh health ───────────────────────────────────────────
    [ObservableProperty] private StorageInfo? _storage;
    [ObservableProperty] private string _storageInfo = "Reading storage statistics…";
    [ObservableProperty] private RefreshHealth? _refresh;
    [ObservableProperty] private string _refreshInfo = "Reading refresh history…";
    public string ModelSize => Storage is { } s ? ModelStorage.Format(s.Total) : "–";
    public string ReclaimText => Coverage is { HasSizes: true, TotalBytes: > 0 } c
        ? $"Unused columns hold {ModelStorage.Format(c.UnusedBytes)} of {ModelStorage.Format(c.TotalBytes)} in memory ({(double)c.UnusedBytes / c.TotalBytes:P0}). That memory is reclaimable by removing them."
        : StorageInfo;
    partial void OnStorageChanged(StorageInfo? value) => OnPropertyChanged(nameof(ModelSize));

    // ── Memory page ──────────────────────────────────────────────────────────
    public ObservableCollection<ColumnMemoryRow> MemoryColumns { get; } = [];
    [ObservableProperty] private IReadOnlyList<TreeTile>? _memoryTiles;
    [ObservableProperty] private string _memoryTable = ""; // treemap selection filters the column list
    [ObservableProperty] private string _memorySearch = "";
    [ObservableProperty] private double _dictionaryShare;
    [ObservableProperty] private double _dataShare;
    [ObservableProperty] private string _compositionText = "";
    [ObservableProperty] private string _largestText = "–";
    [ObservableProperty] private string _memoryReclaim = "–";
    [ObservableProperty] private string _memoryReclaimNote = "";
    [ObservableProperty] private int _storedColumns;
    List<ColumnMemoryRow> _memoryAll = [];

    void BuildMemory()
    {
        if (Storage is not { Total: > 0 } s)
        {
            MemoryTiles = null;
            _memoryAll = [];
            ApplyMemoryFilter();
            return;
        }
        var total = (double)s.Total;
        MemoryTiles = s.Tables.Where(t => t.Value > 0)
            .Select(t => new TreeTile(t.Key, t.Value, $"{ModelStorage.Format(t.Value)} · {t.Value / total:P1} of the model", t.Key)).ToList();
        DictionaryShare = s.Dictionaries / total;
        DataShare = s.Data / total;
        CompositionText = $"Dictionaries {ModelStorage.Format(s.Dictionaries)} · column data {ModelStorage.Format(s.Data)} · " +
            $"hierarchies {ModelStorage.Format(s.Hierarchies)} · relationships and other {ModelStorage.Format(s.Other)}";
        LargestText = s.Largest.Table is { } lt ? $"{lt} · {ModelStorage.Format(s.Largest.Bytes)} ({s.Largest.Bytes / total:P0})" : "–";

        // Usage status only means something once reports were read.
        var usage = AnalysedReports > 0 && Coverage is { } c
            // Columns only: a measure can share a column's name within a table, and measures take no storage.
            ? c.Fields.Where(f => f.Kind != "Measure").GroupBy(f => (f.Table, f.Name)).ToDictionary(g => g.Key, g => g.First().Status)
            : new Dictionary<(string, string), string>();
        _memoryAll = s.Columns.Select(kv => new ColumnMemoryRow(kv.Key.Table, kv.Key.Column, kv.Value, kv.Value.Total / total,
                usage.GetValueOrDefault(kv.Key, "")))
            .OrderByDescending(r => r.Bytes).ToList();
        StoredColumns = _memoryAll.Count;
        if (AnalysedReports > 0 && Coverage is { HasSizes: true } cv)
        {
            MemoryReclaim = ModelStorage.Format(cv.UnusedBytes);
            MemoryReclaimNote = $"{cv.UnusedBytes / total:P0} of the model sits in columns no analysed report or model object uses.";
        }
        else
        {
            MemoryReclaim = "–";
            MemoryReclaimNote = "Open Report usage to see how much of this is in unused columns.";
        }
        ApplyMemoryFilter();
    }

    partial void OnMemoryTableChanged(string value) => ApplyMemoryFilter();
    partial void OnMemorySearchChanged(string value) => ApplyMemoryFilter();

    void ApplyMemoryFilter()
    {
        MemoryColumns.Clear();
        var q = MemorySearch.Trim();
        foreach (var r in _memoryAll
                     .Where(r => MemoryTable.Length == 0 || r.Table == MemoryTable)
                     .Where(r => q.Length == 0 || r.Column.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Table.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .Take(500)) // ponytail: the biggest 500 is what anyone reads; search reaches the rest
            MemoryColumns.Add(r);
    }

    [RelayCommand] private void ClearMemoryTable() => MemoryTable = "";

    // ── Quality page ─────────────────────────────────────────────────────────
    public ObservableCollection<Finding> QualityFindings { get; } = [];
    [ObservableProperty] private QualityReport? _quality;
    [ObservableProperty] private string _qualityInfo = "";
    [ObservableProperty] private bool _isScoring;
    [ObservableProperty] private int _qualitySeverity; // 0 all, 1 errors, 2 warnings, 3 info, 4 suppressed
    [ObservableProperty] private int _qualityArea;     // 0 all, then QualityArea + 1
    [ObservableProperty] private string _qualitySearch = "";
    public event Action<string>? ExportRequested;

    partial void OnQualityChanged(QualityReport? value) => ApplyQualityFilter();
    partial void OnQualitySeverityChanged(int value) => ApplyQualityFilter();
    partial void OnQualityAreaChanged(int value) => ApplyQualityFilter();
    partial void OnQualitySearchChanged(string value) => ApplyQualityFilter();

    void ApplyQualityFilter()
    {
        QualityFindings.Clear();
        if (Quality is null) return;
        var q = QualitySearch.Trim();
        foreach (var f in Quality.Findings
                     .Where(f => QualitySeverity == 4 ? f.Suppressed : !f.Suppressed && (QualitySeverity == 0 || (int)f.Severity == 3 - QualitySeverity))
                     .Where(f => QualityArea == 0 || (int)f.Area == QualityArea - 1)
                     .Where(f => q.Length == 0 || f.Object.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Table.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || f.Rule.Contains(q, StringComparison.OrdinalIgnoreCase) || f.RuleId.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .Take(1000)) // ponytail: the grid stays responsive; search and filters reach the rest
            QualityFindings.Add(f);
    }

    /// <summary>Scores the model as it is now, including unsaved local edits.</summary>
    [RelayCommand]
    private async Task ScoreQuality()
    {
        if (IsScoring || _session is not { } session) return;
        IsScoring = true;
        QualityInfo = "Reading storage statistics and checking rules…";
        try
        {
            Commit();
            var storage = Storage;
            var report = await Task.Run(() => session.Quality(storage));
            if (session != _session) return; // reconnected meanwhile
            Quality = report;
            QualityFiles.Reports[Item.Id] = report;
            QualityInfo = (report.StorageRead ? "" : "Storage statistics unavailable, so size and cardinality rules were skipped. ") +
                $"{report.Rules.Count} rules applied at {DateTime.Now:HH:mm}" + (IsDirty ? " · includes unsaved changes" : "");
        }
        catch (Exception e) { QualityInfo = "Couldn't score the model: " + (e.InnerException?.Message ?? e.Message); }
        finally { IsScoring = false; }
    }

    [RelayCommand] private void ExportQuality(string format) => ExportRequested?.Invoke(format);

    public IReadOnlyList<ScoredModel> QualityExportModels() =>
        Quality is null ? [] : [new ScoredModel(Workspace.DisplayName, Item.DisplayName, Quality)];

    /// <summary>Quality → editor: select the object a finding names (relationships open the diagram).</summary>
    public void OpenFinding(Finding f)
    {
        if (f.Area == Daxis.Core.QualityArea.Relationships) { Page = 2; return; }
        if (f.Table.Length == 0) return;
        Filter = "";
        var group = Groups.FirstOrDefault(g => g.Table?.Name == f.Table);
        var node = group?.Children.FirstOrDefault(c => c.Name == f.Object) ?? group?.Children.FirstOrDefault(c => c.IsSource) ?? group?.Children.FirstOrDefault();
        if (group is null || node is null) return;
        group.IsExpanded = true;
        SelectedNode = node;
        Page = 1;
    }
    readonly Dictionary<string, ReportStats?> _stats = [];
    readonly Dictionary<string, string> _statErrors = [];
    Task _reportsLoad = Task.CompletedTask;
    bool _analysed;

    public double ReportShare => Coverage is { Total: > 0 } c ? (double)c.InReports / c.Total : 0;
    public double ModelShare => Coverage is { Total: > 0 } c ? (double)c.ModelOnly / c.Total : 0;
    public string CoveragePercent => $"{ReportShare:P0}";
    public string CoverageText => Coverage is { } c
        ? $"{c.InReports:N0} of {c.Total:N0} columns and measures are bound to a visual or filter. {c.ModelOnly:N0} more are needed by the model " +
          $"(measure dependencies, relationships, sort-by, RLS). {c.Unused:N0} look unused." +
          (c.Missing > 0 ? $" {c.Missing} report field(s) no longer exist in the model." : "")
        : "";

    partial void OnCoverageChanged(UsageCoverage? value)
    {
        OnPropertyChanged(nameof(ReportShare));
        OnPropertyChanged(nameof(ModelShare));
        OnPropertyChanged(nameof(CoveragePercent));
        OnPropertyChanged(nameof(CoverageText));
        OnPropertyChanged(nameof(ReclaimText));
    }

    // ── Review before save ───────────────────────────────────────────────────
    public ObservableCollection<ModelChange> PendingChanges { get; } = [];
    [ObservableProperty] private bool _isReviewing;
    [ObservableProperty] private string _reviewSummary = "";

    // ── Query panel ──────────────────────────────────────────────────────────
    [ObservableProperty] private string _query = "EVALUATE\n    INFO.VIEW.MEASURES()";
    [ObservableProperty] private QueryResult? _result;
    [ObservableProperty] private string _queryInfo = "Runs against the published model. Ctrl+Enter to run.";
    [ObservableProperty] private bool _queryIsError;
    [ObservableProperty] private bool _isQuerying;

    public override Task LoadAsync() => Busy("Connecting to the XMLA endpoint…", async () =>
    {
        if (_session is { } old) _ = Task.Run(old.Dispose); // waits for any query in flight
        var session = await Task.Run(() => ModelSession.Connect(Workspace.DisplayName, Item.Id, Item.DisplayName, () =>
        {
            var r = auth.AcquireAsync().GetAwaiter().GetResult();
            return (r.AccessToken, r.ExpiresOn);
        }));
        Attach(session);
        _reportsLoad = LoadReportsAsync();
        _ = LoadStorageAsync(session);
        _ = LoadRefreshAsync();
    });

    async Task LoadStorageAsync(ModelSession session)
    {
        try
        {
            var s = await Task.Run(session.Storage);
            if (session != _session) return; // reloaded meanwhile
            Storage = s;
            StorageInfo = "";
            RefreshInsights();
        }
        catch (Exception e) { StorageInfo = "Storage statistics unavailable: " + (e.InnerException?.Message ?? e.Message); }
        OnPropertyChanged(nameof(ReclaimText));
    }

    async Task LoadRefreshAsync()
    {
        try
        {
            Refresh = await fabric.RefreshHealthAsync(Item);
            RefreshInfo = Refresh.Runs.Count == 0 ? "No refreshes recorded yet." : "";
        }
        catch (Exception e) { RefreshInfo = "Refresh history unavailable: " + e.Message; }
    }

    void RefreshInsights()
    {
        if (_session is null) return;
        Summary = ModelInsight.Summarize(_session.Model, Storage);
        Diagram = ModelGraph.Diagram(_session.Model);
        RefreshUsage();
    }

    internal void Attach(ModelSession session)
    {
        // Forget objects from any previous connection so edits can't land in a disposed model.
        _current = null;
        _selectedTable = null;
        SelectedNode = null;
        Show(null);
        _session = session;
        _tracker.Capture(session.Model);
        _stats.Clear();
        _statErrors.Clear();
        _analysed = false;
        Quality = null;
        QualityInfo = "";
        Storage = null;
        StorageInfo = "Reading storage statistics…";
        RefreshInsights();
        IsDirty = false;
        Page = 0;
        Rebuild();
    }

    // ── Tree ─────────────────────────────────────────────────────────────────

    partial void OnFilterChanged(string value) => Rebuild();

    void Rebuild(NamedMetadataObject? select = null)
    {
        if (_session is null) return;
        select ??= _current?.Obj;
        var model = _session.Model;
        Symbols = _session.Symbols();
        var f = Filter.Trim();
        bool Match(string s) => f.Length == 0 || s.Contains(f, StringComparison.OrdinalIgnoreCase);

        Groups.Clear();
        ObjNode? toSelect = null;
        void Add(GroupNode g)
        {
            if (select is not null && g.Children.FirstOrDefault(c => c.Obj == select) is { } hit)
            {
                g.IsExpanded = true;
                toSelect = hit;
            }
            Groups.Add(g);
        }

        var shared = model.Expressions.OrderBy(e => e.Name)
            .Select(e => new ObjNode(e, null, MQuery.IsParameter(e.Expression) ? ObjKind.Parameter : ObjKind.SharedQuery))
            .Where(n => Match(n.Name)).ToList();
        if (shared.Count > 0)
            Add(new GroupNode("Shared queries & parameters", Icons.Query, null, shared, $"{shared.Count}") { IsExpanded = f.Length > 0 });

        foreach (var t in model.Tables.Where(t => !ModelInsight.IsAutoDateTable(t)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var children = t.Partitions.Select(p => new ObjNode(p, t, p.Source switch
                {
                    MPartitionSource => ObjKind.MQuery,
                    CalculatedPartitionSource => ObjKind.DaxTable,
                    EntityPartitionSource => ObjKind.DirectLake,
                    _ => ObjKind.NativeQuery,
                }))
                .Concat(t.Measures.OrderBy(m => m.Name).Select(m => new ObjNode(m, t, ObjKind.Measure)))
                .Concat(t.Columns.OfType<CalculatedColumn>().OrderBy(c => c.Name).Select(c => new ObjNode(c, t, ObjKind.CalculatedColumn)))
                .Concat(t.Columns.Where(c => c.Type != ColumnType.RowNumber && c is not CalculatedColumn).OrderBy(c => c.Name)
                    .Select(c => new ObjNode(c, t, ObjKind.Column)))
                .Where(n => Match(n.Name) || Match(t.Name))
                .ToList();
            if (children.Count == 0 && !Match(t.Name)) continue;
            var count = t.Measures.Count > 0 ? $"{t.Measures.Count} Σ" : "";
            Add(new GroupNode(t.Name, Icons.Table, t, children, count) { IsExpanded = f.Length > 0 });
        }
        if (toSelect is not null) SelectedNode = toSelect;
    }

    partial void OnSelectedNodeChanged(object? value)
    {
        Commit();
        _current = value as ObjNode;
        _selectedTable = (value as GroupNode)?.Table ?? _current?.Table ?? _selectedTable;
        Show(_current);
        if (_current is not null) Page = 1;
    }

    /// <summary>Overview → editor: jump to a table's source query.</summary>
    public void OpenObject(string name, bool sharedQuery)
    {
        var group = sharedQuery ? Groups.FirstOrDefault(g => g.Table is null) : Groups.FirstOrDefault(g => g.Table?.Name == name);
        var node = sharedQuery ? group?.Children.FirstOrDefault(c => c.Name == name) : group?.Children.FirstOrDefault(c => c.IsSource) ?? group?.Children.FirstOrDefault();
        if (group is null || node is null) return;
        group.IsExpanded = true;
        SelectedNode = node;
        Page = 1;
    }

    void Show(ObjNode? n)
    {
        _loading = true;
        HasSelection = n is not null;
        Kind = n?.Tag ?? "";
        TableName = n?.Table?.Name ?? (n is null ? "" : "Shared");
        Name = n?.Name ?? "";
        FormatString = DisplayFolder = Description = "";
        IsHidden = false;
        ObjectError = null;
        SourceInfo = null;
        EditorHint = n is null ? "Select an object in the explorer." : "";
        Language = n?.Kind is ObjKind.MQuery or ObjKind.SharedQuery or ObjKind.Parameter ? "M" : "DAX";

        switch (n?.Obj)
        {
            case Measure m:
                Expression = m.Expression ?? ""; FormatString = m.FormatString ?? ""; DisplayFolder = m.DisplayFolder ?? "";
                Description = m.Description ?? ""; IsHidden = m.IsHidden; ObjectError = Clean(m.ErrorMessage);
                break;
            case Column c:
                Expression = (c as CalculatedColumn)?.Expression ?? ""; FormatString = c.FormatString ?? "";
                DisplayFolder = c.DisplayFolder ?? ""; Description = c.Description ?? ""; IsHidden = c.IsHidden;
                ObjectError = Clean(c.ErrorMessage);
                if (c is DataColumn d) EditorHint = $"Data column, loaded from source column '{d.SourceColumn}'. Its data comes from the table's Power Query.";
                break;
            case Partition p:
                Expression = p.Source switch
                {
                    MPartitionSource ms => ms.Expression ?? "",
                    CalculatedPartitionSource cs => cs.Expression ?? "",
                    QueryPartitionSource qs => qs.Query ?? "",
                    _ => "",
                };
                ObjectError = Clean(p.ErrorMessage);
                var mode = p.Mode == ModeType.Default ? p.Table.Model.DefaultMode : p.Mode;
                SourceInfo = p.Source switch
                {
                    EntityPartitionSource es => $"{mode} · reads entity '{es.EntityName}' via {es.ExpressionSource?.Name ?? "?"}",
                    CalculatedPartitionSource => $"{mode} · calculated table (DAX), computed at refresh",
                    _ => $"{mode} · {Describe(Expression, p.Table.Model)}",
                };
                if (p.Source is EntityPartitionSource e2)
                    EditorHint = $"Direct Lake reads the Delta table '{e2.EntityName}' directly. There is no Power Query to edit.";
                break;
            case NamedExpression e:
                Expression = e.Expression ?? "";
                SourceInfo = n!.Kind == ObjKind.Parameter ? "Parameter · referenced by queries by name" : $"Staging query (not loaded) · {Describe(Expression, e.Model)}";
                break;
            default:
                Expression = "";
                break;
        }
        CanEditExpression = n?.Kind is ObjKind.Measure or ObjKind.CalculatedColumn or ObjKind.MQuery or ObjKind.DaxTable
            or ObjKind.SharedQuery or ObjKind.Parameter;
        CanRename = n is not null && n.Kind is not (ObjKind.SharedQuery or ObjKind.Parameter); // renaming breaks M references by name
        CanDelete = n?.Kind is ObjKind.Measure or ObjKind.CalculatedColumn;
        HasProperties = n?.Kind is ObjKind.Measure or ObjKind.CalculatedColumn or ObjKind.Column;
        IsDaxObject = n?.Kind is ObjKind.Measure or ObjKind.CalculatedColumn or ObjKind.Column;
        UpdateSteps();
        _loading = false;
        ResetDocument();
    }

    static string Describe(string m, Model model)
    {
        var steps = MQuery.Steps(m).Count;
        var sources = MQuery.Sources(m);
        var via = MQuery.References(m, model.Expressions.Select(e => e.Name));
        var parts = new List<string> { $"{steps} applied step{(steps == 1 ? "" : "s")}" };
        if (sources.Count > 0) parts.Add(string.Join(", ", sources.Select(s => s.Target.Length > 0 ? $"{s.Connector}({s.Target})" : s.Connector)));
        if (via.Count > 0) parts.Add("reads from " + string.Join(", ", via));
        return string.Join(" · ", parts);
    }

    void UpdateSteps()
    {
        Steps.Clear();
        if (Language != "M") return;
        foreach (var s in MQuery.Steps(Expression)) Steps.Add(s);
    }

    [RelayCommand] private void GoToStep(MStep step) => StepRequested?.Invoke(step.RawName);

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    partial void OnNameChanged(string value) => Touch();
    partial void OnExpressionChanged(string value)
    {
        Touch();
        if (!_loading) UpdateSteps();
    }
    partial void OnFormatStringChanged(string value) => Touch();
    partial void OnDisplayFolderChanged(string value) => Touch();
    partial void OnDescriptionChanged(string value) => Touch();
    partial void OnIsHiddenChanged(bool value) => Touch();

    void Touch()
    {
        if (!_loading && _current is not null) IsDirty = true;
    }

    /// <summary>Pushes the edit fields into the local TOM object. Nothing reaches the service until a reviewed save.</summary>
    void Commit()
    {
        if (_current is null || _session is null || _loading) return;
        try
        {
            switch (_current.Obj)
            {
                case Measure m:
                    if (m.Expression != Expression) m.Expression = Expression;
                    Apply(m.FormatString, FormatString, v => m.FormatString = v);
                    Apply(m.DisplayFolder, DisplayFolder, v => m.DisplayFolder = v);
                    Apply(m.Description, Description, v => m.Description = v);
                    if (m.IsHidden != IsHidden) m.IsHidden = IsHidden;
                    break;
                case Column c:
                    if (c is CalculatedColumn cc && cc.Expression != Expression) cc.Expression = Expression;
                    Apply(c.FormatString, FormatString, v => c.FormatString = v);
                    Apply(c.DisplayFolder, DisplayFolder, v => c.DisplayFolder = v);
                    Apply(c.Description, Description, v => c.Description = v);
                    if (c.IsHidden != IsHidden) c.IsHidden = IsHidden;
                    break;
                case Partition { Source: MPartitionSource ms } when ms.Expression != Expression:
                    ms.Expression = Expression;
                    break;
                case Partition { Source: CalculatedPartitionSource cs } when cs.Expression != Expression:
                    cs.Expression = Expression;
                    break;
                case NamedExpression e when e.Expression != Expression:
                    e.Expression = Expression;
                    break;
            }
            var name = Name.Trim();
            if (CanRename && name.Length > 0 && name != _current.Obj.Name)
            {
                _current.Obj.Name = name;
                _current.Renamed();
            }
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        IsDirty = _session.HasChanges;

        static void Apply(string? current, string value, Action<string> set)
        {
            if ((current ?? "") != value) set(value);
        }
    }

    // ── Save: review first, then write ───────────────────────────────────────

    [RelayCommand]
    private void Save()
    {
        Commit();
        if (_session is null) return;
        var changes = _tracker.Diff(_session.Model);
        if (changes.Count == 0)
        {
            if (_session.HasChanges) _session.Discard(); // e.g. added then deleted: nothing real to send
            IsDirty = false;
            Notice = "Nothing to save";
            return;
        }
        PendingChanges.Clear();
        foreach (var c in changes) PendingChanges.Add(c);
        var objects = changes.Select(c => c.Path).Distinct().Count();
        ReviewSummary = $"{changes.Count} change{(changes.Count == 1 ? "" : "s")} to {objects} object{(objects == 1 ? "" : "s")} will be written to " +
            $"'{Item.DisplayName}' in '{Workspace.DisplayName}'. Reports on this model see them immediately.";
        IsReviewing = true;
    }

    [RelayCommand] private void CancelReview() => IsReviewing = false;

    [RelayCommand]
    private Task ConfirmSave() => Busy("Saving to the service…", async () =>
    {
        IsReviewing = false;
        if (_session is null) return;
        var keep = _current?.Obj;
        await Task.Run(_session.Save);
        _tracker.Capture(_session.Model);
        RefreshInsights();
        IsDirty = false;
        Rebuild(keep);
        Show(_current);
        Notice = ObjectError is null ? "Saved to service" : "Saved, but the service reports an error on this object";
        Quality = null;
        if (Page == 6) _ = ScoreQuality();
    });

    [RelayCommand]
    private void Discard()
    {
        if (_session is null) return;
        IsReviewing = false;
        _session.Discard();
        IsDirty = false;
        var keep = _current?.Obj;
        _current = null;
        Rebuild(keep);
        Show(_current);
        Notice = "Changes discarded";
    }

    // ── Authoring ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void NewMeasure() => AddTo(t => _session!.AddMeasure(t));

    [RelayCommand]
    private void NewCalculatedColumn() => AddTo(t => _session!.AddCalculatedColumn(t));

    void AddTo(Func<Table, NamedMetadataObject> create)
    {
        if (_session is null) return;
        var table = _selectedTable ?? _session.Model.Tables.FirstOrDefault(t => !ModelInsight.IsAutoDateTable(t));
        if (table is null) return;
        Commit();
        var obj = create(table);
        Filter = "";
        Rebuild(obj);
        IsDirty = true;
        Page = 1;
    }

    [RelayCommand]
    private void Delete()
    {
        if (_current is null || _session is null || !CanDelete) return;
        switch (_current.Obj)
        {
            case Measure m: m.Table.Measures.Remove(m); break;
            case CalculatedColumn c: c.Table.Columns.Remove(c); break;
        }
        _current = null;
        SelectedNode = null;
        Rebuild();
        IsDirty = true;
        Notice = "Deleted locally. It's listed in the review before saving.";
    }

    [RelayCommand]
    private void Format()
    {
        if (!CanEditExpression || string.IsNullOrWhiteSpace(Expression)) return;
        Expression = Language == "M" ? MQuery.Format(Expression) : DaxFormatter.Format(Expression);
    }

    [RelayCommand]
    private void RefreshData() => Ask("Refresh data in the service?",
        $"Starts a refresh of '{Item.DisplayName}' in '{Workspace.DisplayName}'. It reloads data from the sources, " +
        "uses capacity, and reports see the new data when it completes.",
        "Start refresh",
        () => Busy("Requesting refresh…", async () =>
        {
            await fabric.RefreshModelAsync(Item);
            Notice = "Refresh requested. Track progress in Fabric.";
        }));

    // ── Reports ──────────────────────────────────────────────────────────────

    async Task LoadReportsAsync()
    {
        try
        {
            var list = await fabric.ReportsForModelAsync(Workspace, Item.Id);
            Reports.Clear();
            foreach (var r in list) Reports.Add(r);
            ReportsInfo = $"{list.Count} in this workspace";
        }
        catch (Exception e) { ReportsInfo = e.Message; }
    }

    [RelayCommand]
    private async Task ScanAllWorkspaces()
    {
        if (IsScanning) return;
        IsScanning = true;
        ReportsInfo = "Scanning workspaces…";
        var others = workspaces().Where(w => w.Id != Workspace.Id).ToList();
        using var gate = new SemaphoreSlim(6); // ponytail: fixed fan-out; the API throttles long before this matters
        var results = await Task.WhenAll(others.Select(async w =>
        {
            await gate.WaitAsync();
            try { return await fabric.ReportsForModelAsync(w, Item.Id); }
            catch { return []; } // no access to a workspace is normal
            finally { gate.Release(); }
        }));
        foreach (var r in results.SelectMany(x => x).Where(r => Reports.All(e => e.Id != r.Id))) Reports.Add(r);
        ReportsInfo = $"{Reports.Count} across {others.Count + 1} workspaces you can access";
        ScannedAll = true;
        IsScanning = false;
        if (_analysed) await AnalyseReports();
    }

    // ── Report usage ─────────────────────────────────────────────────────────

    partial void OnPageChanged(int value)
    {
        if (value is 3 or 4 && !_analysed) _ = AnalyseReports();
        if (value == 6 && Quality is null) _ = ScoreQuality();
    }

    /// <summary>Reads each connected report's definition once and folds it into usage and lineage as it arrives.</summary>
    [RelayCommand]
    private async Task AnalyseReports()
    {
        if (IsAnalysing || _session is null) return;
        _analysed = true;
        IsAnalysing = true;
        try
        {
            await _reportsLoad;
            var todo = Reports.Where(r => !_stats.ContainsKey(r.Id)).ToList();
            var done = 0;
            UsageInfo = $"Reading {todo.Count} report definition{(todo.Count == 1 ? "" : "s")}…";
            RefreshUsage();
            using var gate = new SemaphoreSlim(4); // ponytail: fixed fan-out, like the workspace scan
            await Task.WhenAll(todo.Select(async r =>
            {
                await gate.WaitAsync();
                try
                {
                    var parts = await fabric.GetDefinitionAsync(new FabricItem(r.Id, r.Name, "Report", null, r.WorkspaceId));
                    _stats[r.Id] = await Task.Run(() => ReportAnalysis.Analyse(parts));
                }
                catch (Exception e)
                {
                    _stats[r.Id] = null;
                    _statErrors[r.Id] = ReadError(e);
                }
                finally
                {
                    gate.Release();
                    UsageInfo = $"Reading report definitions… {++done} of {todo.Count}";
                    RefreshUsage();
                }
            }));
            var failed = Reports.Count(r => _stats.GetValueOrDefault(r.Id) is null);
            UsageInfo = $"{AnalysedReports} of {Reports.Count} report{(Reports.Count == 1 ? "" : "s")} analysed" +
                (failed > 0 ? $" · {failed} could not be read" : "") + (ScannedAll ? " · all workspaces" : " · this workspace");
        }
        finally { IsAnalysing = false; }
    }

    void RefreshUsage()
    {
        if (_session is null) return;
        var ok = Reports.Where(r => _stats.GetValueOrDefault(r.Id) is not null).Select(r => (r, s: _stats[r.Id]!)).ToList();

        ReportRows.Clear();
        foreach (var r in Reports)
            ReportRows.Add(new ReportRow(r, _stats.GetValueOrDefault(r.Id),
                !_stats.TryGetValue(r.Id, out var st) ? _analysed ? "Waiting…" : ""
                : st is null ? _statErrors.GetValueOrDefault(r.Id, "Failed") : "Analysed"));

        AnalysedReports = ok.Count;
        TotalPages = ok.Sum(x => x.s.Pages);
        TotalVisuals = ok.Sum(x => x.s.Visuals);
        TotalSlicers = ok.Sum(x => x.s.Slicers);
        TotalFilters = ok.Sum(x => x.s.Filters);

        VisualTypes.Clear();
        foreach (var t in TypeShares(ok.Select(x => x.s))) VisualTypes.Add(t);

        Coverage = ReportAnalysis.Coverage(_session.Model, ok.Select(x => x.s.Fields).ToList(), Storage);
        ApplyFieldFilter();
        Lineage = ModelGraph.Lineage(_session.Model, ok.Select(x => (x.r.Name, (string?)x.r.WebUrl, x.s.Fields)));
        BuildMemory();
    }

    /// <summary>Visual-type mix across reports, largest first, with each share relative to the largest.</summary>
    internal static List<TypeShare> TypeShares(IEnumerable<ReportStats> stats)
    {
        var types = stats.SelectMany(s => s.VisualTypes).GroupBy(kv => VisualName(kv.Key))
            .Select(g => (Name: g.Key, Count: g.Sum(kv => kv.Value))).OrderByDescending(t => t.Count).ThenBy(t => t.Name).ToList();
        return types.Select(t => new TypeShare(t.Name, t.Count, (double)t.Count / types[0].Count)).ToList();
    }

    /// <summary>Report definitions need edit rights; say so plainly instead of a raw 403.</summary>
    internal static string ReadError(Exception e) =>
        e is FabricException { Status: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized }
            ? "No access: reading a definition needs edit rights" : e.Message;

    static string VisualName(string type) => type switch
    {
        "" => "Unknown",
        "tableEx" => "Table",
        "pivotTable" => "Matrix",
        "multiRowCard" => "Multi-row card",
        "kpi" => "KPI",
        "textbox" => "Text box",
        _ => char.ToUpperInvariant(type[0]) + Regex.Replace(type[1..], "(?<=[a-z])([A-Z])", " $1").ToLowerInvariant(),
    };

    partial void OnFieldFilterChanged(int value) => ApplyFieldFilter();
    partial void OnFieldSearchChanged(string value) => ApplyFieldFilter();

    void ApplyFieldFilter()
    {
        Fields.Clear();
        if (Coverage is null) return;
        var q = FieldSearch.Trim();
        foreach (var f in Coverage.Fields
                     .Where(f => FieldFilter == 0 || (int)f.Use == FieldFilter - 1)
                     .Where(f => q.Length == 0 || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Table.Contains(q, StringComparison.OrdinalIgnoreCase)))
            Fields.Add(f);
    }

    // ── Query panel ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void QuerySelected()
    {
        if (_current is null || !IsDaxObject) return;
        var table = DaxCompletion.Quote(_current.Table!.Name);
        Query = _current.Kind == ObjKind.Measure
            ? $"EVALUATE\n    ROW(\"{_current.Name.Replace("\"", "\"\"")}\", [{_current.Name.Replace("]", "]]")}])"
            : $"EVALUATE\n    TOPN(100, VALUES({table}[{_current.Name.Replace("]", "]]")}]))";
        _ = RunQuery();
    }

    [RelayCommand]
    private async Task RunQuery()
    {
        if (IsQuerying || string.IsNullOrWhiteSpace(Query)) return;
        IsQuerying = true;
        QueryIsError = false;
        QueryInfo = "Running…";
        var sw = Stopwatch.StartNew();
        try
        {
            Result = await fabric.ExecuteQueryAsync(Item, Query);
            QueryInfo = $"{Result.Rows.Count:N0} rows · {sw.ElapsedMilliseconds:N0} ms";
        }
        catch (Exception e)
        {
            Result = null;
            QueryIsError = true;
            QueryInfo = e.Message;
        }
        finally { IsQuerying = false; }
    }

    // Disconnect is a network call; keep it off the UI thread.
    public override void Dispose()
    {
        var s = _session;
        _session = null;
        if (s is not null) _ = Task.Run(s.Dispose);
    }
}
