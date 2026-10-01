using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daxis.Core;

namespace Daxis.App;

/// <summary>Quality results shared across tabs, and saving them to disk.</summary>
public static class QualityFiles
{
    /// <summary>Latest report per model id, from any tab that scored it. Lives for the session.</summary>
    public static readonly ConcurrentDictionary<string, QualityReport> Reports = new(StringComparer.OrdinalIgnoreCase);

    public static async Task SaveAsync(Visual from, string format, IReadOnlyList<ScoredModel> models, string name)
    {
        if (models.Count == 0 || TopLevel.GetTopLevel(from)?.StorageProvider is not { } sp) return;
        var (ext, label, write) = format switch
        {
            "md" => ("md", "Markdown", (Func<string>)(() => QualityExport.Markdown(models))),
            "json" => ("json", "JSON", () => QualityExport.Json(models)),
            "findings" => ("csv", "CSV", () => QualityExport.FindingsCsv(models)),
            "scores" => ("csv", "CSV", () => QualityExport.ScoresCsv(models)),
            _ => ("html", "HTML report", () => QualityExport.Html(models)),
        };
        var suffix = format is "findings" or "scores" ? "-" + format : "";
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export quality",
            SuggestedFileName = $"{safe} quality{suffix}.{ext}",
            DefaultExtension = ext,
            FileTypeChoices = [new FilePickerFileType(label) { Patterns = [$"*.{ext}"] }],
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is { } path) await File.WriteAllTextAsync(path, write(), new UTF8Encoding(true)); // BOM: Excel reads CSV as UTF-8
    }
}

public sealed record EstateModel(Workspace Workspace, FabricItem Item, QualityReport? Report, string Status)
{
    public string Name => Item.DisplayName;
    public string WorkspaceName => Workspace.DisplayName;
    public string Grade => Report?.Grade ?? "–";
    public double Score => Report?.Score ?? -1;
    public string ScoreText => Report is null ? "" : $"{Report.Score:0}";
    public string Complexity => Report?.Complexity.Band ?? "";
    public int Errors => Report?.Errors ?? 0;
    public int Warnings => Report?.Warnings ?? 0;
    public string Note => Report is null ? Status : Report.CapReason ?? TopIssue;
    string TopIssue => Report!.Findings.Where(f => !f.Suppressed).GroupBy(f => f.Rule).OrderByDescending(g => g.First().Severity)
        .ThenByDescending(g => g.Count()).Select(g => $"{g.Key} ({g.Count()})").FirstOrDefault() ?? "No findings";
}

public sealed partial class EstateWorkspace(Workspace ws) : ObservableObject
{
    public Workspace Workspace { get; } = ws;
    public string Name => Workspace.DisplayName;
    /// <summary>XMLA needs Premium, Premium Per User or Fabric capacity.</summary>
    public bool Scorable => Workspace.CapacityId is not null;
    [ObservableProperty] private bool _isChecked = ws.CapacityId is not null;
    [ObservableProperty] private string _status = ws.CapacityId is null ? "Not scorable: no capacity, so no XMLA" : "";
    [ObservableProperty] private QualityRollup? _rollup;
    public string Grade => Rollup is { Models: > 0 } r ? r.Grade : "";
    partial void OnRollupChanged(QualityRollup? value) => OnPropertyChanged(nameof(Grade));
}

/// <summary>
/// Scores every semantic model in the chosen workspaces over XMLA and rolls them up: estate → workspace → model.
/// Each model's connection is closed as soon as it's scored, so the scan's memory stays flat however big the estate.
/// </summary>
public sealed partial class EstateQualityTab(FabricItem item, Workspace ws, Auth auth, FabricClient fabric,
    Func<IReadOnlyList<Workspace>> workspaces, Action<FabricItem> openModel) : TabBase(item, ws)
{
    public const string ItemType = "EstateQuality";

    public override Geometry Icon => Icons.Gauge;
    public override string WebUrl => "https://app.fabric.microsoft.com/home";

    public ObservableCollection<EstateWorkspace> Workspaces { get; } = [];
    public ObservableCollection<EstateModel> Models { get; } = [];
    [ObservableProperty] private EstateWorkspace? _selected; // null: every workspace
    [ObservableProperty] private QualityRollup? _estate;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanText = "Pick workspaces, then scan.";
    [ObservableProperty] private double _scanPercent;
    [ObservableProperty] private string _search = "";
    public event Action<string>? ExportRequested;

    readonly List<EstateModel> _results = [];
    CancellationTokenSource? _scan;

    public string GradeSpread => Estate is { Models: > 0 } r ? string.Join("   ", r.Grades.Select(g => $"{g.Key} {g.Value}")) : "–";
    public string ScopeText => Selected is { } w ? w.Name : "All workspaces";
    partial void OnEstateChanged(QualityRollup? value) => OnPropertyChanged(nameof(GradeSpread));
    partial void OnSelectedChanged(EstateWorkspace? value)
    {
        OnPropertyChanged(nameof(ScopeText));
        Refill();
    }
    partial void OnSearchChanged(string value) => Refill();

    public override Task LoadAsync()
    {
        Workspaces.Clear();
        foreach (var w in workspaces().Where(w => w.Type != "Personal").OrderBy(w => w.CapacityId is null).ThenBy(w => w.DisplayName, StringComparer.OrdinalIgnoreCase))
            Workspaces.Add(new EstateWorkspace(w));
        ScanText = $"{Workspaces.Count(w => w.Scorable)} of {Workspaces.Count} workspaces are on capacity and can be scored over XMLA.";
        return Task.CompletedTask;
    }

    [RelayCommand] private void CheckAll() { foreach (var w in Workspaces.Where(w => w.Scorable)) w.IsChecked = true; }
    [RelayCommand] private void CheckNone() { foreach (var w in Workspaces) w.IsChecked = false; }
    [RelayCommand] private void ShowAll() => Selected = null;
    [RelayCommand] private void Cancel() => _scan?.Cancel();
    [RelayCommand] private void Export(string format) => ExportRequested?.Invoke(format);

    [RelayCommand]
    private async Task Scan()
    {
        _scan?.Cancel();
        var cts = _scan = new CancellationTokenSource();
        var ct = cts.Token;
        var picked = Workspaces.Where(w => w.IsChecked && w.Scorable).ToList();
        if (picked.Count == 0) { ScanText = "Tick at least one workspace on capacity."; return; }
        _results.RemoveAll(r => picked.Any(p => p.Workspace.Id == r.Workspace.Id));
        IsScanning = true;
        ScanPercent = 0;
        try
        {
            // 1. List the models in each workspace.
            ScanText = $"Listing models in {picked.Count} workspace(s)…";
            var targets = new ConcurrentBag<(EstateWorkspace W, FabricItem M)>();
            using (var gate = new SemaphoreSlim(6))
                await Task.WhenAll(picked.Select(async w =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var models = (await fabric.ItemsAsync(w.Workspace.Id)).Where(i => i.Type == MainViewModel.SemanticModel).ToList();
                        foreach (var m in models) targets.Add((w, m));
                        w.Status = models.Count == 0 ? "No semantic models" : $"{models.Count} model(s), waiting…";
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { w.Status = e.Message; }
                    finally { gate.Release(); }
                }));

            // 2. Score each model: connect, read metadata and storage, score, disconnect.
            var all = targets.ToList();
            var done = 0;
            ScanText = $"Scoring 0 of {all.Count} models…";
            using (var gate = new SemaphoreSlim(4)) // ponytail: fixed fan-out, like the other scans; XMLA throttles well before this
                await Task.WhenAll(all.Select(async t =>
                {
                    await gate.WaitAsync(ct);
                    EstateModel row;
                    try
                    {
                        var report = await Task.Run(() =>
                        {
                            using var session = ModelSession.Connect(t.W.Workspace.DisplayName, t.M.Id, t.M.DisplayName, Token);
                            return session.Quality();
                        }, ct);
                        QualityFiles.Reports[t.M.Id] = report;
                        row = new EstateModel(t.W.Workspace, t.M, report, "");
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        row = new EstateModel(t.W.Workspace, t.M, null, "Couldn't read over XMLA: " + (e.InnerException?.Message ?? e.Message));
                    }
                    finally { gate.Release(); }
                    if (ct.IsCancellationRequested) return;
                    _results.Add(row);
                    var n = ++done;
                    ScanText = $"Scoring {n} of {all.Count} models…";
                    ScanPercent = 100.0 * n / Math.Max(1, all.Count);
                    Summarise(t.W);
                    if (n % 4 == 0 || n == all.Count) Refill();
                }));
            ScanText = $"Scored {_results.Count(r => r.Report is not null)} of {_results.Count} models at {DateTime.Now:HH:mm}";
        }
        catch (OperationCanceledException) { ScanText = "Scan cancelled. Results so far are kept."; }
        finally
        {
            if (_scan == cts) IsScanning = false;
            foreach (var w in picked) Summarise(w);
            Refill();
        }
    }

    (string, DateTimeOffset) Token()
    {
        var r = auth.AcquireAsync().GetAwaiter().GetResult();
        return (r.AccessToken, r.ExpiresOn);
    }

    void Summarise(EstateWorkspace w)
    {
        var rows = _results.Where(r => r.Workspace.Id == w.Workspace.Id).ToList();
        w.Rollup = ModelQuality.Rollup(rows.Select(r => r.Report).OfType<QualityReport>().ToList());
        var failed = rows.Count(r => r.Report is null);
        if (rows.Count > 0)
            w.Status = $"{w.Rollup.Score:0} mean over {w.Rollup.Models} model(s)" + (failed > 0 ? $" · {failed} unreadable" : "");
    }

    void Refill()
    {
        Estate = ModelQuality.Rollup(_results.Select(r => r.Report).OfType<QualityReport>().ToList());
        var q = Search.Trim();
        Models.Clear();
        foreach (var r in _results
                     .Where(r => Selected is null || r.Workspace.Id == Selected.Workspace.Id)
                     .Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.WorkspaceName.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(r => r.Report is null).ThenBy(r => r.Score).ThenBy(r => r.Name))
            Models.Add(r);
    }

    public IReadOnlyList<ScoredModel> ExportModels() => Models.Where(m => m.Report is not null)
        .Select(m => new ScoredModel(m.WorkspaceName, m.Name, m.Report!)).ToList();

    public void Open(EstateModel row) => openModel(row.Item);

    public override void Dispose() => _scan?.Cancel();
}
