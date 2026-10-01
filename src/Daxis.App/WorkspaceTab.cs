using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daxis.Core;

namespace Daxis.App;

public sealed record TypeCount(string Name, int Count)
{
    public string Label => Name.ToUpperInvariant();
}

public sealed record ModelRow(FabricItem Item, string Sources, int Reports, RefreshHealth? Refresh = null, QualityReport? Quality = null)
{
    public string Grade => Quality?.Grade ?? "";
    public string QualityText => Quality is null ? "" : $"{Quality.Score:0}";
    public string Name => Item.DisplayName;
    public string ReportsText => Reports == 0 ? "No reports" : Reports.ToString();
    public string LastRefresh => Refresh?.LastText ?? "…";
    public string Typical => Refresh?.TypicalText ?? "";
    public string Success => Refresh?.SuccessText ?? "";
    public string Schedule => Refresh is null ? "" : Refresh.Schedule?.Text ?? "No import schedule";
}

public sealed record WorkspaceReportRow(WorkspaceReport Report, string Model, string ModelWorkspace)
{
    public string Name => Report.Ref.Name;
}

/// <summary>One model's consumption: how much of it the analysed reports use.</summary>
public sealed record ModelUsageRow(FabricItem Item, int Reports, UsageCoverage? Coverage, string Status)
{
    public string Name => Item.DisplayName;
    public int Total => Coverage?.Total ?? 0;
    public double Share => Total > 0 ? (double)Coverage!.InReports / Total : 0;
    public double ModelShare => Total > 0 ? (double)Coverage!.ModelOnly / Total : 0;
    public string Percent => Coverage is null ? "" : $"{Share:P0}";
    public string Breakdown => Coverage is { } c
        ? $"{c.InReports} used · {c.ModelOnly} model only · {c.Unused} unused" + (c.HasSizes ? $" · {ModelStorage.Format(c.UnusedBytes)} unused" : "")
        : Status;
}

/// <summary>A field row in the workspace-wide list: the model it belongs to plus its usage.</summary>
public sealed record ConsumptionField(string Model, FieldUsage Usage)
{
    public string Table => Usage.Table;
    public string Name => Usage.Name;
    public string Kind => Usage.Kind;
    public string Status => Usage.Status;
    public int Reports => Usage.Reports;
    public bool IsHidden => Usage.IsHidden;
    public string Size => Usage.Size;
    public long? Bytes => Usage.Bytes;
}

/// <summary>One model on the Refreshes page.</summary>
public sealed record ModelRefreshRow(FabricItem Item, RefreshHealth Health)
{
    public string Name => Item.DisplayName;
    public bool Failing => Health.Last?.Failed == true;
    public string Status => Health.Last is not { } l ? "Never" : l.Status == "Unknown" ? "Running" : l.Status;
    public string LastText => Health.LastText;
    public string Schedule => Health.Schedule?.Text ?? "No import schedule";
    public string Stats => Health.Runs.Count == 0 ? "" : $"typical {Health.TypicalText} · slowest {Health.SlowestText} · {Health.SuccessText} succeeded";
    public IReadOnlyList<RefreshRun> Runs => Health.Runs;
}

public sealed record FailureRow(DateTimeOffset When, string Model, string Error)
{
    public string WhenText => When.ToLocalTime().ToString("ddd d MMM HH:mm");
}

/// <summary>A model on the workspace Memory page, measured against the capacity's per-model limit when known.</summary>
public sealed record ModelMemoryRow(FabricItem Item, StorageInfo? Storage, string Status, double? LimitGb, long? UnusedBytes, double ShareOfLargest)
{
    public string Name => Item.DisplayName;
    public long Bytes => Storage?.Total ?? 0;
    public string Size => Storage is null ? "" : ModelStorage.Format(Bytes);
    public double LimitShare => LimitGb is { } l && Storage is not null ? Math.Min(1, Bytes / (l * 1024 * 1024 * 1024)) : ShareOfLargest;
    public double RefreshShare => LimitGb is { } l && Storage is not null ? Math.Min(1 - LimitShare, Bytes / (l * 1024 * 1024 * 1024)) : 0;
    public string Headroom => Storage is null ? Status
        : LimitGb is { } l ? $"{Bytes / (l * 1024 * 1024 * 1024):P0} of the {l:0} GB per-model limit · a full refresh needs about {ModelStorage.Format(Bytes * 2)}"
        : "Capacity limit unknown";
    public string Largest => Storage?.Largest.Table is { } t ? $"Largest table: {t} ({ModelStorage.Format(Storage.Largest.Bytes)})" : "";
    public string Unused => UnusedBytes is { } u ? $"{ModelStorage.Format(u)} in unused columns" : "";
    public bool AtRisk => LimitGb is { } l && Bytes * 2 > l * 1024 * 1024 * 1024;
}

public sealed record TableMemoryRow(string Model, string Table, long Bytes, double Share)
{
    public string Size => ModelStorage.Format(Bytes);
    public string Percent => $"{Share:P1}";
}

/// <summary>Whole-workspace overview: item inventory, models and their reports, and sources → models → reports lineage.</summary>
public sealed partial class WorkspaceTab(FabricItem item, Workspace ws, Auth auth, FabricClient fabric,
    Func<IReadOnlyList<Workspace>> workspaces, Action<string> openItem) : TabBase(item, ws)
{
    public override Geometry Icon => Icons.Workspace;
    public override string WebUrl => $"https://app.fabric.microsoft.com/groups/{Workspace.Id}/list";

    public ObservableCollection<TypeCount> Types { get; } = [];
    public ObservableCollection<ModelRow> Models { get; } = [];
    public ObservableCollection<WorkspaceReportRow> Reports { get; } = [];

    [ObservableProperty] private int _page; // 0 overview, 1 lineage, 2 consumption, 3 refreshes, 4 memory
    [ObservableProperty] private int _itemCount;
    [ObservableProperty] private int _orphanModels;
    [ObservableProperty] private int _externalReports;
    [ObservableProperty] private Graph? _lineage;
    [ObservableProperty] private string _sourcesInfo = "";

    List<FabricItem> _models = [];
    CapacityInfo? _capacity;

    // ── Memory ───────────────────────────────────────────────────────────────
    public ObservableCollection<ModelMemoryRow> MemoryModels { get; } = [];
    public ObservableCollection<TableMemoryRow> TopTables { get; } = [];
    [ObservableProperty] private IReadOnlyList<TreeTile>? _memoryTiles;
    [ObservableProperty] private string _workspaceMemory = "–";
    [ObservableProperty] private string _largestModel = "–";
    [ObservableProperty] private string _capacityText = "–";
    [ObservableProperty] private string _capacityNote = "";
    [ObservableProperty] private string _memoryReclaim = "–";
    [ObservableProperty] private string _memoryInfo = "";
    [ObservableProperty] private int _modelsAtRisk;
    Dictionary<string, RefreshHealth> _refresh = [];

    // ── Refreshes ────────────────────────────────────────────────────────────
    public ObservableCollection<ModelRefreshRow> RefreshRows { get; } = [];
    public ObservableCollection<FailureRow> Failures { get; } = [];
    [ObservableProperty] private IReadOnlyList<TimelineRow>? _timeline;
    [ObservableProperty] private int _scheduledModels;
    [ObservableProperty] private int _failingModels;
    [ObservableProperty] private int _runningNow;
    [ObservableProperty] private string _overallSuccess = "–";
    [ObservableProperty] private string _peakText = "";
    [ObservableProperty] private string _refreshesInfo = "Reading refresh history…";
    List<WorkspaceReport> _reports = [];

    // ── Consumption ──────────────────────────────────────────────────────────
    public ObservableCollection<ModelUsageRow> ModelUsage { get; } = [];
    public ObservableCollection<ReportRow> ReportUsage { get; } = [];
    public ObservableCollection<TypeShare> VisualTypes { get; } = [];
    public ObservableCollection<ConsumptionField> Fields { get; } = [];
    [ObservableProperty] private int _analysedReports;
    [ObservableProperty] private int _analysedModels;
    [ObservableProperty] private int _totalVisuals;
    [ObservableProperty] private int _totalSlicers;
    [ObservableProperty] private int _totalFilters;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private double _reportShare;
    [ObservableProperty] private double _modelShare;
    [ObservableProperty] private string _coveragePercent = "";
    [ObservableProperty] private string _coverageText = "";
    [ObservableProperty] private string _usageInfo = "";
    [ObservableProperty] private bool _isAnalysing;
    [ObservableProperty] private bool _scannedAll;
    [ObservableProperty] private int _fieldFilter; // 0 all, 1 used in reports, 2 model only, 3 unused
    [ObservableProperty] private string _fieldSearch = "";
    readonly Dictionary<string, ReportStats?> _stats = [];
    readonly Dictionary<string, string> _statErrors = [];
    readonly Dictionary<string, ModelSession> _sessions = [];   // ponytail: kept open so a scan only re-scores; closed with the tab
    readonly Dictionary<string, string> _modelErrors = [];
    readonly Dictionary<string, UsageCoverage> _coverage = [];
    readonly Dictionary<string, StorageInfo?> _storage = [];
    List<WorkspaceReport> _consumers = [];
    bool _analysed;

    public override Task LoadAsync() => Busy("Reading the workspace…", async () =>
    {
        var itemsTask = fabric.ItemsAsync(Workspace.Id);
        var reportsTask = fabric.ReportsAsync(Workspace);
        var items = await itemsTask;
        var reports = await reportsTask;

        ItemCount = items.Count;
        Types.Clear();
        foreach (var g in items.GroupBy(i => i.Type).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
            Types.Add(new TypeCount(TypeName(g.Key), g.Count()));

        _capacity = null;
        if (Workspace.CapacityId is { } capId)
            try { _capacity = (await fabric.CapacitiesAsync()).FirstOrDefault(c => c.Id.Equals(capId, StringComparison.OrdinalIgnoreCase)); }
            catch (FabricException) { } // only capacity admins and contributors can list it
        var models = _models = items.Where(i => i.Type == MainViewModel.SemanticModel).ToList();
        _reports = reports;
        _analysed = false;
        _stats.Clear();
        _coverage.Clear();
        _storage.Clear();
        _modelErrors.Clear();
        _consumers = [];
        ScannedAll = false;
        var old = _sessions.Values.ToList();
        _sessions.Clear();
        _ = Task.Run(() => old.ForEach(s => s.Dispose()));
        var names = models.ToDictionary(m => m.Id, m => m.DisplayName, StringComparer.OrdinalIgnoreCase);
        var wsNames = workspaces().ToDictionary(w => w.Id, w => w.DisplayName, StringComparer.OrdinalIgnoreCase);
        Reports.Clear();
        foreach (var r in reports.OrderBy(r => r.Ref.Name, StringComparer.OrdinalIgnoreCase))
            Reports.Add(new WorkspaceReportRow(r, names.GetValueOrDefault(r.ModelId) ?? "(model in another workspace)",
                r.ModelWorkspaceId == Workspace.Id ? "This workspace" : wsNames.GetValueOrDefault(r.ModelWorkspaceId) ?? "Another workspace"));
        ExternalReports = reports.Count(r => r.ModelWorkspaceId != Workspace.Id);

        // Show the inventory straight away; sources arrive per model and fill in the lineage.
        var sources = new Dictionary<string, IReadOnlyList<MSource>>();
        var refresh = _refresh = new Dictionary<string, RefreshHealth>();
        RefreshesInfo = "Reading refresh history…";
        void Refresh()
        {
            Models.Clear();
            foreach (var m in models)
                Models.Add(new ModelRow(m,
                    sources.TryGetValue(m.Id, out var s) ? string.Join(", ", s.Select(x => x.Connector).Distinct()) : "…",
                    reports.Count(r => string.Equals(r.ModelId, m.Id, StringComparison.OrdinalIgnoreCase)),
                    refresh.GetValueOrDefault(m.Id), QualityFiles.Reports.GetValueOrDefault(m.Id)));
            OrphanModels = Models.Count(m => m.Reports == 0);
            Lineage = ModelGraph.Workspace(models, sources, reports, (w, m) => $"https://app.fabric.microsoft.com/groups/{w}/datasets/{m}");
        }
        Refresh();

        var failed = 0;
        using var gate = new SemaphoreSlim(4); // ponytail: fixed fan-out, like the report scans
        SourcesInfo = models.Count > 0 ? $"Reading data sources for {models.Count} model(s)…" : "";
        await Task.WhenAll(models.Select(async m =>
        {
            await gate.WaitAsync();
            try
            {
                try { sources[m.Id] = await fabric.ModelSourcesAsync(m); }
                catch { sources[m.Id] = []; failed++; } // Direct Lake and some connectors don't expose datasources
                try { refresh[m.Id] = await fabric.RefreshHealthAsync(m, top: 30); }
                catch { refresh[m.Id] = new RefreshHealth(null, []); }
            }
            finally { gate.Release(); }
            Refresh();
        }));
        SourcesInfo = failed > 0 ? $"{failed} model(s) didn't report their data sources" : "";
        BuildRefreshes();
    });

    // ── Quality ──────────────────────────────────────────────────────────────

    [ObservableProperty] private string _qualityInfo = "";

    /// <summary>Scores every model here over XMLA, reusing the connections the Memory and Consumption pages open.</summary>
    [RelayCommand]
    private async Task ScoreModels()
    {
        if (IsAnalysing) return;
        IsAnalysing = true;
        try
        {
            await ConnectModelsAsync(text => QualityInfo = text);
            var ready = _models.Where(m => _sessions.ContainsKey(m.Id)).ToList();
            var done = 0;
            foreach (var m in ready) // ponytail: one model at a time; scoring is quick once connected
            {
                QualityInfo = $"Checking rules… {++done} of {ready.Count}";
                var session = _sessions[m.Id];
                var storage = _storage.GetValueOrDefault(m.Id);
                try
                {
                    QualityFiles.Reports[m.Id] = await Task.Run(() => session.Quality(storage));
                }
                catch (Exception e) { _modelErrors[m.Id] = e.Message; }
            }
            for (var i = 0; i < Models.Count; i++)
                Models[i] = Models[i] with { Quality = QualityFiles.Reports.GetValueOrDefault(Models[i].Item.Id) };
            var r = ModelQuality.Rollup(Models.Select(x => x.Quality).OfType<QualityReport>().ToList());
            QualityInfo = r.Models == 0 ? "No model could be read over XMLA." :
                $"Workspace {r.Grade} ({r.Score:0}, mean of {r.Models} model(s)) · {r.Errors} errors · {r.Warnings} warnings" +
                (_modelErrors.Count > 0 ? $" · {_modelErrors.Count} not reachable over XMLA" : "") + ". Open a model's Quality page for detail.";
        }
        finally { IsAnalysing = false; }
    }

    // ── Refreshes ────────────────────────────────────────────────────────────

    void BuildRefreshes()
    {
        var rows = _models.Where(m => _refresh.ContainsKey(m.Id)).Select(m => new ModelRefreshRow(m, _refresh[m.Id]))
            .OrderByDescending(r => r.Failing).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        RefreshRows.Clear();
        foreach (var r in rows) RefreshRows.Add(r);

        Failures.Clear();
        foreach (var f in rows.SelectMany(r => r.Health.Runs.Where(x => x.Failed).Select(x => new FailureRow(x.End ?? x.Start, r.Name, x.Error ?? "No details")))
                     .OrderByDescending(f => f.When).Take(50))
            Failures.Add(f);

        var scheduled = rows.Where(r => r.Health.Schedule is { Enabled: true }).ToList();
        ScheduledModels = scheduled.Count;
        FailingModels = rows.Count(r => r.Failing);
        RunningNow = rows.Count(r => r.Status == "Running");
        var finished = rows.SelectMany(r => r.Health.Runs).Where(x => x.Status is "Completed" or "Failed").ToList();
        OverallSuccess = finished.Count > 0 ? $"{(double)finished.Count(x => !x.Failed) / finished.Count:P0}" : "–";

        var tz = TimeZoneInfo.Local;
        var timeline = scheduled
            .Select(r => new TimelineRow(r.Name, ScheduleTime.Starts(r.Health.Schedule!, tz), r.Health.Typical ?? TimeSpan.FromMinutes(5), r.Failing))
            .Where(t => t.Starts.Count > 0).OrderBy(t => t.Starts[0]).ToList();
        Timeline = timeline;
        var (peak, at) = ScheduleTime.Peak(timeline.Select(t => (t.Starts, t.Duration)));
        PeakText = peak > 1 ? $"Busiest: {peak} scheduled refreshes overlap around {at.Hours:00}:{at.Minutes:00} (your time). Spreading them out eases capacity pressure."
            : timeline.Count > 0 ? "No scheduled refreshes overlap." : "";
        RefreshesInfo = rows.Count == 0 ? "No semantic models in this workspace." :
            $"Last {rows.Max(r => r.Health.Runs.Count)} refreshes per model · times shown in your time zone ({tz.StandardName})";
    }

    // ── Consumption ──────────────────────────────────────────────────────────

    partial void OnPageChanged(int value)
    {
        if (value == 2 && !_analysed && !IsBusy) _ = AnalyseConsumption();
        if (value == 4 && !IsBusy && _models.Any(m => !_sessions.ContainsKey(m.Id) && !_modelErrors.ContainsKey(m.Id))) _ = LoadMemory();
    }

    /// <summary>
    /// Same rules as a model's Report usage page, for every model here: read each consuming report's definition,
    /// read each model over XMLA, then score its columns and measures.
    /// </summary>
    [RelayCommand]
    private async Task AnalyseConsumption()
    {
        if (IsAnalysing) return;
        _analysed = true;
        IsAnalysing = true;
        try
        {
            var ids = _models.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _consumers = _consumers.Concat(_reports).Where(r => ids.Contains(r.ModelId)).DistinctBy(r => r.Ref.Id).ToList();

            var todo = _consumers.Where(r => !_stats.ContainsKey(r.Ref.Id)).ToList();
            var done = 0;
            using (var gate = new SemaphoreSlim(4)) // ponytail: fixed fan-out, like the other scans
                await Task.WhenAll(todo.Select(async r =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var parts = await fabric.GetDefinitionAsync(new FabricItem(r.Ref.Id, r.Ref.Name, "Report", null, r.Ref.WorkspaceId));
                        _stats[r.Ref.Id] = await Task.Run(() => ReportAnalysis.Analyse(parts));
                    }
                    catch (Exception e)
                    {
                        _stats[r.Ref.Id] = null;
                        _statErrors[r.Ref.Id] = ModelTab.ReadError(e);
                    }
                    finally
                    {
                        gate.Release();
                        UsageInfo = $"Reading report definitions… {++done} of {todo.Count}";
                        RefreshConsumption();
                    }
                }));

            await ConnectModelsAsync(text => UsageInfo = text);

            _coverage.Clear();
            foreach (var m in _models.Where(m => _sessions.ContainsKey(m.Id)))
            {
                var fields = _consumers.Where(r => r.ModelId.Equals(m.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(r => _stats.GetValueOrDefault(r.Ref.Id)?.Fields).OfType<IReadOnlySet<FieldRef>>().ToList();
                var session = _sessions[m.Id];
                var storage = _storage.GetValueOrDefault(m.Id);
                _coverage[m.Id] = await Task.Run(() => ReportAnalysis.Coverage(session.Model, fields, storage));
            }
            RefreshConsumption();
            BuildMemory();

            var failedReports = _consumers.Count(r => _stats.GetValueOrDefault(r.Ref.Id) is null);
            UsageInfo = $"{AnalysedModels} of {_models.Count} model(s) · {AnalysedReports} of {_consumers.Count} report(s) analysed" +
                (failedReports > 0 ? $" · {failedReports} report(s) could not be read" : "") +
                (_modelErrors.Count > 0 ? $" · {_modelErrors.Count} model(s) not reachable over XMLA" : "") +
                (ScannedAll ? " · all workspaces" : " · reports in this workspace");
        }
        finally { IsAnalysing = false; }
    }

    /// <summary>XMLA metadata and storage per model, once; two at a time keeps the endpoint and memory calm.</summary>
    async Task ConnectModelsAsync(Action<string> progress)
    {
        var todo = _models.Where(m => !_sessions.ContainsKey(m.Id) && !_modelErrors.ContainsKey(m.Id)).ToList();
        var done = 0;
        using var gate = new SemaphoreSlim(2);
        await Task.WhenAll(todo.Select(async m =>
        {
            await gate.WaitAsync();
            try
            {
                var session = await Task.Run(() => ModelSession.Connect(Workspace.DisplayName, m.Id, m.DisplayName, () =>
                {
                    var t = auth.AcquireAsync().GetAwaiter().GetResult();
                    return (t.AccessToken, t.ExpiresOn);
                }));
                _sessions[m.Id] = session;
                // Sizes are a bonus: a failed storage query still leaves usage scoring intact.
                try { _storage[m.Id] = await Task.Run(session.Storage); }
                catch { _storage[m.Id] = null; }
            }
            catch (Exception e) { _modelErrors[m.Id] = e.InnerException?.Message ?? e.Message; }
            finally
            {
                gate.Release();
                progress($"Reading model metadata over XMLA… {++done} of {todo.Count}");
                BuildMemory();
            }
        }));
    }

    [RelayCommand]
    private async Task LoadMemory()
    {
        if (IsAnalysing) return;
        IsAnalysing = true;
        try { await ConnectModelsAsync(text => MemoryInfo = text); }
        finally { IsAnalysing = false; }
        BuildMemory();
    }

    void BuildMemory()
    {
        var limit = ModelStorage.ModelLimitGb(_capacity?.Sku);
        var measured = _models.Where(m => _storage.GetValueOrDefault(m.Id) is not null).ToList();
        var largest = measured.Select(m => _storage[m.Id]!.Total).DefaultIfEmpty(1).Max();
        var rows = _models.Select(m =>
            {
                var s = _storage.GetValueOrDefault(m.Id);
                var status = _modelErrors.TryGetValue(m.Id, out var e) ? e
                    : _sessions.ContainsKey(m.Id) && s is null ? "Storage statistics unavailable" : s is null ? "Waiting…" : "";
                return new ModelMemoryRow(m, s, status, limit, _coverage.GetValueOrDefault(m.Id) is { HasSizes: true } c ? c.UnusedBytes : null,
                    s is null ? 0 : (double)s.Total / Math.Max(1, largest));
            })
            .OrderByDescending(r => r.Bytes).ThenBy(r => r.Name).ToList();
        MemoryModels.Clear();
        foreach (var r in rows) MemoryModels.Add(r);

        var total = measured.Sum(m => _storage[m.Id]!.Total);
        WorkspaceMemory = measured.Count > 0 ? ModelStorage.Format(total) : "–";
        LargestModel = rows.FirstOrDefault(r => r.Storage is not null) is { } lm ? $"{lm.Name} · {lm.Size}" : "–";
        ModelsAtRisk = rows.Count(r => r.AtRisk);
        MemoryTiles = measured.Select(m => new TreeTile(m.DisplayName, _storage[m.Id]!.Total,
            $"{ModelStorage.Format(_storage[m.Id]!.Total)} · {(double)_storage[m.Id]!.Total / Math.Max(1, total):P0} of the workspace", m)).ToList();

        TopTables.Clear();
        foreach (var t in measured.SelectMany(m => _storage[m.Id]!.Tables.Select(kv => new TableMemoryRow(m.DisplayName, kv.Key, kv.Value, (double)kv.Value / Math.Max(1, total))))
                     .OrderByDescending(t => t.Bytes).Take(25))
            TopTables.Add(t);

        CapacityText = _capacity is { } cap ? $"{cap.Sku} · {(limit is { } l ? $"{l:0} GB per model" : "limit unknown")}"
            : Workspace.CapacityId is null ? "No capacity" : "Not visible to you";
        CapacityNote = _capacity is { } c2 ? $"{c2.Name} ({c2.Region}). A model has to fit the per-model limit, and a full refresh needs roughly twice its size."
            : "Per-model limits need read access to the capacity; ask a capacity admin if you need the headroom figures.";
        MemoryReclaim = _coverage.Values.Any(c => c.HasSizes) ? ModelStorage.Format(_coverage.Values.Sum(c => c.UnusedBytes)) : "–";
        if (!IsAnalysing || measured.Count == _models.Count)
            MemoryInfo = _models.Count == 0 ? "No semantic models in this workspace." :
                $"{measured.Count} of {_models.Count} model(s) measured" + (_modelErrors.Count > 0 ? $" · {_modelErrors.Count} not reachable over XMLA" : "") +
                (_coverage.Count == 0 ? " · run Consumption to see how much sits in unused columns" : "");
    }

    /// <summary>Reports elsewhere can read these models; find them in every workspace you can access, then re-score.</summary>
    [RelayCommand]
    private async Task ScanAllWorkspaces()
    {
        if (IsAnalysing || ScannedAll) return;
        IsAnalysing = true;
        UsageInfo = "Scanning workspaces for reports on these models…";
        var others = workspaces().Where(w => w.Id != Workspace.Id).ToList();
        using (var gate = new SemaphoreSlim(6))
        {
            var found = await Task.WhenAll(others.Select(async w =>
            {
                await gate.WaitAsync();
                try { return await fabric.ReportsAsync(w); }
                catch { return []; } // no access to a workspace is normal
                finally { gate.Release(); }
            }));
            _consumers = _consumers.Concat(found.SelectMany(x => x)).ToList();
        }
        ScannedAll = true;
        IsAnalysing = false;
        await AnalyseConsumption();
    }

    void RefreshConsumption()
    {
        var names = _models.ToDictionary(m => m.Id, m => m.DisplayName, StringComparer.OrdinalIgnoreCase);
        var ok = _consumers.Select(r => _stats.GetValueOrDefault(r.Ref.Id)).OfType<ReportStats>().ToList();

        ReportUsage.Clear();
        foreach (var r in _consumers.OrderBy(r => r.Ref.Name, StringComparer.OrdinalIgnoreCase))
            ReportUsage.Add(new ReportRow(r.Ref, _stats.GetValueOrDefault(r.Ref.Id),
                !_stats.TryGetValue(r.Ref.Id, out var st) ? "Waiting…" : st is null ? _statErrors.GetValueOrDefault(r.Ref.Id, "Failed") : "Analysed",
                names.GetValueOrDefault(r.ModelId, "")));
        AnalysedReports = ok.Count;
        TotalPages = ok.Sum(s => s.Pages);
        TotalVisuals = ok.Sum(s => s.Visuals);
        TotalSlicers = ok.Sum(s => s.Slicers);
        TotalFilters = ok.Sum(s => s.Filters);
        VisualTypes.Clear();
        foreach (var t in ModelTab.TypeShares(ok)) VisualTypes.Add(t);

        ModelUsage.Clear();
        foreach (var m in _models
                     .Select(m => new ModelUsageRow(m, _consumers.Count(r => r.ModelId.Equals(m.Id, StringComparison.OrdinalIgnoreCase)),
                         _coverage.GetValueOrDefault(m.Id),
                         _modelErrors.TryGetValue(m.Id, out var err) ? err : _coverage.ContainsKey(m.Id) ? "" : "Waiting…"))
                     .OrderByDescending(r => r.Coverage is not null).ThenBy(r => r.Share).ThenBy(r => r.Name))
            ModelUsage.Add(m);
        AnalysedModels = _coverage.Count;

        var all = _coverage.Values.ToList();
        var total = all.Sum(c => c.Total);
        ReportShare = total > 0 ? (double)all.Sum(c => c.InReports) / total : 0;
        ModelShare = total > 0 ? (double)all.Sum(c => c.ModelOnly) / total : 0;
        CoveragePercent = all.Count > 0 ? $"{ReportShare:P0}" : "–";
        CoverageText = all.Count == 0 ? "" :
            $"{all.Sum(c => c.InReports):N0} of {total:N0} columns and measures across {all.Count} model(s) are bound to a visual or filter. " +
            $"{all.Sum(c => c.ModelOnly):N0} more are needed by the models, {all.Sum(c => c.Unused):N0} look unused." +
            (all.Sum(c => c.Missing) is > 0 and var miss ? $" {miss} report field(s) no longer exist in their model." : "") +
            (all.Any(c => c.HasSizes) ? $" Unused columns hold {ModelStorage.Format(all.Sum(c => c.UnusedBytes))} of {ModelStorage.Format(all.Sum(c => c.TotalBytes))} in memory." : "");
        ApplyFieldFilter();
    }

    partial void OnFieldFilterChanged(int value) => ApplyFieldFilter();
    partial void OnFieldSearchChanged(string value) => ApplyFieldFilter();

    void ApplyFieldFilter()
    {
        Fields.Clear();
        var q = FieldSearch.Trim();
        foreach (var m in _models.Where(m => _coverage.ContainsKey(m.Id)).OrderBy(m => m.DisplayName))
            foreach (var f in _coverage[m.Id].Fields
                         .Where(f => FieldFilter == 0 || (int)f.Use == FieldFilter - 1)
                         .Where(f => q.Length == 0 || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                     f.Table.Contains(q, StringComparison.OrdinalIgnoreCase) || m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)))
                Fields.Add(new ConsumptionField(m.DisplayName, f));
    }

    public void OpenModel(FabricItem model) => openItem(model.Id);

    // XMLA disconnects are network calls; keep them off the UI thread.
    public override void Dispose()
    {
        var open = _sessions.Values.ToList();
        _sessions.Clear();
        if (open.Count > 0) _ = Task.Run(() => open.ForEach(s => s.Dispose()));
    }

    public void Open(GNode n)
    {
        if (n.Kind == GKind.Model && Models.FirstOrDefault(m => "m:" + m.Item.Id == n.Id) is { } row) openItem(row.Item.Id);
    }

    public void OpenModel(ModelRow row) => openItem(row.Item.Id);

    static string TypeName(string type) => type switch
    {
        MainViewModel.SemanticModel => "Semantic models",
        "DataPipeline" => "Pipelines",
        "SQLEndpoint" => "SQL endpoints",
        "KQLDatabase" => "KQL databases",
        "MLModel" => "ML models",
        _ => Regex.Replace(type, "(?<=[a-z])([A-Z])", m => " " + m.Value.ToLowerInvariant()) + "s",
    };
}
