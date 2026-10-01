using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daxis.Core;

namespace Daxis.App;

/// <summary>One workspace in the scan: its Git connection, status and freshness. Fills in as the service answers.</summary>
public sealed partial class GitRow(Workspace ws) : ObservableObject
{
    public Workspace Workspace { get; } = ws;
    public string Name => Workspace.DisplayName;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private GitConnection? _connection;
    [ObservableProperty] private GitStatus? _status;
    [ObservableProperty] private string? _statusError;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private Freshness _freshness;
    [ObservableProperty] private string _syncText = "";
    [ObservableProperty] private string? _relationText;
    [ObservableProperty] private bool _sharesBranch;

    public bool IsConnected => Connection is { State: not GitState.NotConnected, Provider: not null };
    public bool NoAccess => Error is not null;
    public string Branch => Connection?.Provider?.Branch ?? "";
    public string Repo => Connection?.Provider is { } p ? $"{p.Repo} · {p.Folder}" : "";
    public string StateText => Error is not null ? "No access"
        : Connection is null ? ""
        : Connection.State switch
        {
            GitState.NotConnected => "Not connected",
            GitState.Connected => "Connected, never synced",
            _ => Connection.Selective ? "Synced (selective)" : "Synced",
        };
    public string Counts => Status is { } s
        ? (s.Clean ? "In sync" : string.Join(" · ", new[]
            {
                s.Uncommitted > 0 ? $"{s.Uncommitted} to commit" : null,
                s.Incoming > 0 ? $"{s.Incoming} incoming" : null,
                s.Conflicts > 0 ? SourceControlTab.Plural(s.Conflicts, "conflict") : null,
            }.OfType<string>()))
        : StatusError is not null ? "Status unavailable" : IsConnected && IsLoading ? "…" : "";
    public bool NeedsAttention => Freshness is Freshness.Stale or Freshness.Critical || SharesBranch || Status is { Clean: false } || StatusError is not null;

    public void Refresh(DateTimeOffset now, int staleDays, int criticalDays)
    {
        Freshness = Git.FreshnessOf(Connection, Status, now, staleDays, criticalDays);
        SyncText = IsConnected ? Git.Ago(Connection!.LastSync, now) : "";
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(NoAccess));
        OnPropertyChanged(nameof(Branch));
        OnPropertyChanged(nameof(Repo));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Counts));
        OnPropertyChanged(nameof(NeedsAttention));
    }
}

/// <summary>A heading in the scan list (repo + folder, or "Not connected") and its workspaces.</summary>
public sealed record GitGroup(string Label, string Detail, IReadOnlyList<GitRow> Rows);

public sealed partial class StepRow(string key, string title) : ObservableObject
{
    public string Key { get; } = key;
    [ObservableProperty] private string _title = title;
    [ObservableProperty] private StepState _state;
    [ObservableProperty] private string? _detail;
    [ObservableProperty] private int? _percent;

    public bool IsActive => State == StepState.Active;
    public bool IsDone => State == StepState.Done;
    public bool IsFailed => State == StepState.Failed;
    public bool IsPending => State is StepState.Pending or StepState.Skipped;
    public bool HasPercent => Percent is not null && State == StepState.Active;

    partial void OnStateChanged(StepState value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(HasPercent));
    }

    partial void OnPercentChanged(int? value) => OnPropertyChanged(nameof(HasPercent));
}

public sealed partial class ChangeRow(GitChange change) : ObservableObject
{
    public GitChange Change { get; } = change;
    public string Name => Change.Name;
    public string Type => Change.Type;
    public string Workspace => Change.WorkspaceChange ?? "";
    public string Remote => Change.RemoteChange ?? "";
    public bool IsConflict => Change.IsConflict;
    public bool CanCommit => Change.WorkspaceChange is not null && !Change.IsConflict;
    [ObservableProperty] private bool _selected = change.WorkspaceChange is not null && !change.IsConflict;
}

public sealed record PlanRow(PlannedChange Change)
{
    public string Name => Change.Name;
    public string Type => Change.Type;
    public string Action => Change.Impact switch { Impact.Delete => "Deleted", Impact.Overwrite => "Overwritten", _ => "Added" };
    public string? Note => Change.Block ?? Change.Warning;
    public bool IsBlocked => Change.Block is not null;
}

public sealed record CheckRow(Check Check)
{
    public string Title => Check.Title;
    public string Detail => Check.Detail;
    public bool Ok => Check.Ok;
}

public sealed record Recovery(RepointJournal Journal)
{
    public string Text => $"'{Journal.WorkspaceName}': the switch from {Journal.Original.Branch} to {Journal.Target} was interrupted " + Journal.Step switch
    {
        RepointStep.Updating => "while the workspace was being updated. Restore brings back the original branch and its content.",
        RepointStep.Previewed => "while waiting for you to confirm. No items were changed; the workspace is still connected to the new branch.",
        RepointStep.RollingBack => "while it was being restored. Run the restore again to finish.",
        _ => $"before any item was changed. Its Git connection may not be on {Journal.Original.Branch}.",
    };
}

/// <summary>
/// Tenant-wide source control: which workspaces are on Git and where, how fresh they are, commit / update, offload of
/// items Git can't track, and journalled branch repointing.
/// </summary>
public sealed partial class SourceControlTab : TabBase
{
    public const string ItemType = "SourceControl";

    readonly FabricClient _fabric;
    readonly Settings _settings;
    readonly Func<IReadOnlyList<Workspace>> _workspaces;
    List<GitRow> _rows = [];
    List<FabricItem> _items = [];
    GitCredentials? _credentials;
    RepointPreview? _preview;
    CancellationTokenSource? _scan;

    public SourceControlTab(FabricItem item, Workspace ws, FabricClient fabric, Settings settings, Func<IReadOnlyList<Workspace>> workspaces)
        : base(item, ws)
    {
        _fabric = fabric;
        _settings = settings;
        _workspaces = workspaces;
        _grouping = settings.SourceControlByWorkspace ? 1 : 0;
    }

    public override Geometry Icon => Icons.Branch;
    public override string WebUrl => Selected is { } r
        ? $"https://app.fabric.microsoft.com/groups/{r.Workspace.Id}/list"
        : "https://app.fabric.microsoft.com/home";

    // ── Scan ─────────────────────────────────────────────────────────────────
    public ObservableCollection<GitGroup> Groups { get; } = [];
    public ObservableCollection<Recovery> Recoveries { get; } = [];
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanText = "";
    [ObservableProperty] private double _scanPercent;
    [ObservableProperty] private int _connectedCount;
    [ObservableProperty] private int _notConnectedCount;
    [ObservableProperty] private int _staleCount;
    [ObservableProperty] private int _criticalCount;
    [ObservableProperty] private int _sharedBranchCount;
    [ObservableProperty] private int _noAccessCount;
    [ObservableProperty] private int _filter; // 0 all, 1 needs attention, 2 connected, 3 not connected
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _grouping; // 0 by repo, 1 by workspace
    public string Thresholds => $"Stale after {_settings.StaleDays} days without a sync, critical after {_settings.CriticalDays}.";

    // ── Selected workspace ───────────────────────────────────────────────────
    [ObservableProperty] private GitRow? _selected;
    [ObservableProperty] private int _detailPage; // 0 changes, 1 offload, 2 repoint
    [ObservableProperty] private bool _isLoadingDetail;
    public ObservableCollection<ChangeRow> Changes { get; } = [];
    [ObservableProperty] private string _commitMessage = "";
    [ObservableProperty] private int _conflictPolicy; // 0 stop, 1 keep workspace, 2 take Git
    [ObservableProperty] private string? _detailError;
    public bool HasSelection => Selected is not null;
    public bool SelectedConnected => Selected?.IsConnected == true;
    /// <summary>Fabric refuses an update with unresolved conflicts, so a conflict policy must be picked first.</summary>
    public bool CanUpdate => Selected?.Status is { } s && (s.Incoming > 0 || s.Conflicts > 0) && (s.Conflicts == 0 || ConflictPolicy > 0);
    public bool CanCommit => Changes.Any(c => c.Selected && c.CanCommit) && !string.IsNullOrWhiteSpace(CommitMessage);
    /// <summary>One green button: update when something is incoming, otherwise commit.</summary>
    public bool UpdateIsPrimary => CanUpdate;
    public bool HasConflicts => Selected?.Status is { Conflicts: > 0 };
    public string ChangesSummary => Selected?.Status is { } s
        ? s.Clean ? "The workspace and the branch match." : $"{s.Uncommitted} uncommitted · {s.Incoming} incoming · {Plural(s.Conflicts, "conflict")}"
        : Selected?.StatusError ?? "";

    // ── Offload ──────────────────────────────────────────────────────────────
    public ObservableCollection<FabricItem> OffloadItems { get; } = [];
    [ObservableProperty] private bool _offloadAll;
    [ObservableProperty] private string? _offloadFolder;
    [ObservableProperty] private string _lastOffload = "";
    public string OffloadTarget => Selected is { } r && OffloadFolder is { } f ? Offload.WorkspaceRoot(f, r.Workspace) : "";
    public string OffloadSummary => OffloadItems.Count == 0
        ? OffloadAll ? "This workspace has no items." : "Git can track every item in this workspace. Tick the box to save a full local copy anyway."
        : OffloadAll ? $"All {OffloadItems.Count} items are saved to this computer."
        : $"{OffloadItems.Count} item(s) Git can't track: {string.Join(", ", OffloadItems.GroupBy(i => i.Type).Select(g => $"{g.Count()} {g.Key}"))}. " +
          "They are never committed or updated by Git, so this is their only copy outside Fabric.";

    // ── Repoint ──────────────────────────────────────────────────────────────
    public ObservableCollection<string> KnownBranches { get; } = [];
    public ObservableCollection<CheckRow> Checks { get; } = [];
    public ObservableCollection<PlanRow> Plan { get; } = [];
    public ObservableCollection<string> PlanBlocks { get; } = [];
    public ObservableCollection<string> PlanWarnings { get; } = [];
    [ObservableProperty] private string _targetBranch = "";
    [ObservableProperty] private bool _checksPassed;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private bool _acknowledged;
    [ObservableProperty] private string _previewSummary = "";
    [ObservableProperty] private string _previewTitle = "";
    public bool CanSwitch => HasPreview && PlanBlocks.Count == 0 && (PlanWarnings.Count == 0 || Acknowledged) && !IsOperating;

    // ── Operation (stepper) ──────────────────────────────────────────────────
    public ObservableCollection<StepRow> Steps { get; } = [];
    [ObservableProperty] private bool _isOperating;
    [ObservableProperty] private bool _showOperation;
    [ObservableProperty] private string _operationTitle = "";
    [ObservableProperty] private string? _operationResult;
    [ObservableProperty] private bool _operationFailed;

    public override Task LoadAsync()
    {
        LoadRecoveries();
        return ScanAsync();
    }

    // ── Scan ─────────────────────────────────────────────────────────────────

    [RelayCommand]
    private Task Rescan() => ScanAsync();

    async Task ScanAsync()
    {
        _scan?.Cancel();
        var cts = _scan = new CancellationTokenSource();
        var workspaces = _workspaces().Where(w => w.Type != "Personal").ToList();
        _rows = workspaces.Select(w => new GitRow(w)).ToList();
        var keep = Selected?.Workspace.Id ?? Workspace.Id;
        Selected = null;
        Regroup();
        IsScanning = true;
        var done = 0;
        ScanText = $"Scanning 0 / {_rows.Count} workspaces…";
        using var gate = new SemaphoreSlim(6); // ponytail: fixed fan-out; 429s are retried by the client
        try
        {
            await Task.WhenAll(_rows.Select(async row =>
            {
                await gate.WaitAsync(cts.Token);
                try { await ScanOneAsync(row, cts.Token); }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref done);
                    ScanText = $"Scanning {n} / {_rows.Count} workspaces…";
                    ScanPercent = 100.0 * n / Math.Max(1, _rows.Count);
                    if (n % 8 == 0) Regroup();
                }
            }));
        }
        catch (OperationCanceledException) { return; }
        finally { if (_scan == cts) IsScanning = false; }
        Regroup();
        ScanText = $"Scanned {_rows.Count} workspaces at {DateTime.Now:HH:mm}";
        if (keep is not null && _rows.FirstOrDefault(r => r.Workspace.Id == keep) is { } again) Select(again);
    }

    async Task ScanOneAsync(GitRow row, CancellationToken ct)
    {
        try
        {
            row.Connection = await _fabric.GitConnectionAsync(row.Workspace.Id, ct);
            if (row.IsConnected)
            {
                var relations = _fabric.WorkspaceRelationsAsync(row.Workspace.Id, ct);
                if (row.Connection.State == GitState.Initialized)
                    try { row.Status = await _fabric.GitStatusAsync(row.Workspace.Id, ct); }
                    catch (FabricException e) { row.StatusError = Friendly(e); }
                try { row.RelationText = RelationText(await relations); }
                catch (FabricException) { } // preview API; not every tenant has it
            }
        }
        catch (FabricException e) { row.Error = Friendly(e); }
        finally
        {
            row.IsLoading = false;
            row.Refresh(DateTimeOffset.UtcNow, _settings.StaleDays, _settings.CriticalDays);
        }
    }

    string? RelationText(List<WorkspaceRelation> relations)
    {
        if (relations.Count == 0) return null;
        var names = _workspaces().ToDictionary(w => w.Id, w => w.DisplayName, StringComparer.OrdinalIgnoreCase);
        string N(string id) => names.TryGetValue(id, out var n) ? n : "a workspace you can't see";
        var baseOf = relations.FirstOrDefault(r => r.Type == "Base");
        var branches = relations.Count(r => r.Type == "Branch");
        return baseOf is not null ? $"Branched from {N(baseOf.RelatedWorkspaceId)}"
            : branches > 0 ? $"{branches} branch workspace{(branches > 1 ? "s" : "")}" : null;
    }

    partial void OnFilterChanged(int value) => Regroup();
    partial void OnGroupingChanged(int value)
    {
        _settings.SourceControlByWorkspace = value == 1;
        _settings.Save();
        Regroup();
    }
    partial void OnSearchChanged(string value) => Regroup();

    void Regroup()
    {
        var families = Git.Families(_rows.Where(r => r.IsConnected).Select(r => (r.Workspace.Id, r.Connection!.Provider!)));
        var byId = _rows.ToDictionary(r => r.Workspace.Id);
        foreach (var f in families)
            foreach (var m in f.Members) byId[m.WorkspaceId].SharesBranch = m.SharesBranch;

        bool Show(GitRow r) => r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) && Filter switch
        {
            1 => r.NeedsAttention || r.IsLoading,
            2 => r.IsConnected,
            3 => !r.IsConnected && !r.IsLoading,
            _ => true,
        };

        var groups = Grouping == 1
            // By workspace: every workspace A–Z, connected or not; repo and branch stay on each row.
            ? [new GitGroup("All workspaces A–Z", "", _rows.Where(r => !r.IsLoading && Show(r)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList())]
            : families
                .Select(f => new GitGroup(f.Label, Plural(f.Members.Count, "workspace"),
                    f.Members.Select(m => byId[m.WorkspaceId]).Where(Show).ToList()))
                .ToList();
        groups = groups.Where(g => g.Rows.Count > 0).ToList();
        var pending = _rows.Where(r => r.IsLoading && Show(r)).ToList();
        if (pending.Count > 0) groups.Insert(0, new GitGroup("Checking…", "", pending));
        var rest = _rows.Where(r => !r.IsLoading && !r.IsConnected && Show(r)).OrderBy(r => r.NoAccess).ThenBy(r => r.Name).ToList();
        if (rest.Count > 0 && Grouping == 0) groups.Add(new GitGroup("Not connected to Git", $"{rest.Count} workspace{(rest.Count > 1 ? "s" : "")}", rest));

        Groups.Clear();
        foreach (var g in groups) Groups.Add(g);

        var scanned = _rows.Where(r => !r.IsLoading).ToList();
        ConnectedCount = scanned.Count(r => r.IsConnected);
        NotConnectedCount = scanned.Count(r => !r.IsConnected && !r.NoAccess);
        StaleCount = scanned.Count(r => r.Freshness == Freshness.Stale);
        CriticalCount = scanned.Count(r => r.Freshness == Freshness.Critical);
        SharedBranchCount = scanned.Count(r => r.SharesBranch);
        NoAccessCount = scanned.Count(r => r.NoAccess);
    }

    // ── Selection ────────────────────────────────────────────────────────────

    public void Select(GitRow row)
    {
        if (IsOperating) return;
        Selected = row;
    }

    partial void OnSelectedChanged(GitRow? oldValue, GitRow? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        ResetRepoint();
        ShowOperation = false;
        DetailError = null;
        Changes.Clear();
        OffloadItems.Clear();
        _items = [];
        _credentials = null;
        OffloadFolder = newValue is not null && _settings.OffloadFolders.TryGetValue(newValue.Workspace.Id, out var f) ? f : null;
        CommitMessage = newValue is null ? "" : $"Update from {newValue.Name} (Daxis)";
        if (newValue is not null)
        {
            if (!newValue.IsConnected && DetailPage == 0) DetailPage = 1;
            _ = LoadDetailAsync(newValue);
        }
        RaiseDetail();
    }

    async Task LoadDetailAsync(GitRow row)
    {
        IsLoadingDetail = true;
        try
        {
            var items = _fabric.ItemsAsync(row.Workspace.Id);
            if (row.IsConnected)
            {
                try { _credentials = await _fabric.GitCredentialsAsync(row.Workspace.Id); }
                catch (FabricException) { _credentials = null; }
            }
            _items = await items;
            if (Selected != row) return;
            FillChanges(row);
            FillOffload();
            FillBranches(row);
            LastOffload = OffloadFolder is { } f && Offload.ReadManifest(Offload.WorkspaceRoot(f, row.Workspace)) is { } m
                ? $"Last offload {Git.Ago(m.At, DateTimeOffset.UtcNow)}: {m.Saved} saved" + (m.FailedCount > 0 ? $", {m.FailedCount} failed" : "")
                : "Not offloaded yet.";
        }
        catch (Exception e) { if (Selected == row) DetailError = e.Message; }
        finally { if (Selected == row) IsLoadingDetail = false; }
    }

    void FillChanges(GitRow row)
    {
        Changes.Clear();
        foreach (var c in row.Status?.Changes ?? []) Changes.Add(Track(new ChangeRow(c)));
        RaiseDetail();
    }

    ChangeRow Track(ChangeRow c)
    {
        c.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanCommit));
        return c;
    }

    void FillOffload()
    {
        OffloadItems.Clear();
        foreach (var i in _items.Where(i => OffloadAll ? !Git.SystemOwned.Contains(i.Type) : Git.IsOffloadCandidate(i))) OffloadItems.Add(i);
        OnPropertyChanged(nameof(OffloadSummary));
    }

    void FillBranches(GitRow row)
    {
        KnownBranches.Clear();
        if (row.Connection?.Provider is not { } p) return;
        foreach (var b in _rows.Where(r => r.Connection?.Provider?.FamilyKey == p.FamilyKey).Select(r => r.Branch)
                     .Where(b => b != p.Branch).Distinct(StringComparer.OrdinalIgnoreCase).Order())
            KnownBranches.Add(b);
    }

    partial void OnOffloadAllChanged(bool value) => FillOffload();
    partial void OnOffloadFolderChanged(string? value) => OnPropertyChanged(nameof(OffloadTarget));
    partial void OnConflictPolicyChanged(int value) => RaiseDetail();
    partial void OnCommitMessageChanged(string value) => OnPropertyChanged(nameof(CanCommit));
    partial void OnAcknowledgedChanged(bool value) => OnPropertyChanged(nameof(CanSwitch));
    partial void OnIsOperatingChanged(bool value) => OnPropertyChanged(nameof(CanSwitch));
    partial void OnTargetBranchChanged(string value)
    {
        ChecksPassed = false;
        Checks.Clear();
    }

    void RaiseDetail()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedConnected));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(UpdateIsPrimary));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(ChangesSummary));
        OnPropertyChanged(nameof(WebUrl));
    }

    /// <summary>Re-reads one workspace after an operation so the list and detail show the new state.</summary>
    async Task RefreshRowAsync(GitRow row)
    {
        row.IsLoading = true;
        row.Status = null;
        row.StatusError = null;
        await ScanOneAsync(row, CancellationToken.None);
        Regroup();
        if (Selected == row)
        {
            FillChanges(row);
            FillBranches(row);
        }
    }

    [RelayCommand]
    private void SelectAllChanges()
    {
        var all = Changes.Where(c => c.CanCommit).All(c => c.Selected);
        foreach (var c in Changes.Where(c => c.CanCommit)) c.Selected = !all;
    }

    // ── Update from Git ──────────────────────────────────────────────────────

    string? Policy => ConflictPolicy switch { 1 => "PreferWorkspace", 2 => "PreferRemote", _ => null };

    [RelayCommand]
    private void UpdateFromGit()
    {
        if (Selected is not { Status: { } status } row) return;
        var plan = Git.ClassifyUpdate(status, Policy);
        var blocks = plan.Where(p => p.Block is not null).Select(p => p.Block!).ToList();
        if (blocks.Count > 0)
        {
            DetailError = "Update stopped before it started. " + string.Join(" ", blocks);
            return;
        }
        var warnings = plan.Where(p => p.Warning is not null).Select(p => p.Warning!).ToList();
        Ask($"Update '{row.Name}' from {row.Branch}?",
            $"{plan.Count(p => p.Impact == Impact.Create)} added, {plan.Count(p => p.Impact == Impact.Overwrite)} overwritten, " +
            $"{plan.Count(p => p.Impact == Impact.Delete)} deleted in the workspace to match the branch. " +
            (status.Conflicts > 0 ? Policy == "PreferRemote" ? "Conflicting items take the Git version; their workspace edits are lost. " : "Conflicting items keep the workspace version. " : "") +
            (status.Uncommitted > 0 ? "Uncommitted workspace changes to other items are kept. " : "") +
            string.Join(" ", warnings),
            "Update workspace", () => RunUpdateAsync(row));
    }

    Task RunUpdateAsync(GitRow row) => Operate($"Update '{row.Name}' from Git", row, async p =>
    {
        var status = await Step(p, "status", "Read the latest status", async () =>
        {
            var s = await _fabric.GitStatusAsync(row.Workspace.Id);
            return (s, $"{s.Incoming} incoming · {s.Conflicts} conflicts");
        });
        var blocks = Git.ClassifyUpdate(status, Policy).Where(c => c.Block is not null).ToList();
        if (blocks.Count > 0) throw new InvalidOperationException(string.Join(" ", blocks.Select(b => b.Block)));
        if (status.RemoteCommitHash is not { } remote || (status.Incoming == 0 && status.Conflicts == 0))
            return "Nothing incoming any more; the workspace already matches the branch.";
        await Step(p, "update", $"Update from {row.Branch}", async () =>
        {
            await _fabric.GitUpdateAsync(row.Workspace.Id, remote, status.WorkspaceHead, Policy,
                new Progress<int>(pc => p.Report(new("update", $"Update from {row.Branch}", StepState.Active, $"{pc}%", pc))));
            return (true, "Applied");
        });
        return $"'{row.Name}' now matches {row.Branch}.";
    });

    // ── Commit to Git ────────────────────────────────────────────────────────

    [RelayCommand]
    private void Commit()
    {
        if (Selected is not { } row || !CanCommit) return;
        var chosen = Changes.Where(c => c.Selected && c.CanCommit).Select(c => c.Change).ToList();
        var all = chosen.Count == Changes.Count(c => c.CanCommit);
        Ask($"Commit {chosen.Count} item(s) to {row.Branch}?",
            $"Writes the workspace version of {string.Join(", ", chosen.Take(6).Select(c => c.Name))}{(chosen.Count > 6 ? $" and {chosen.Count - 6} more" : "")} " +
            $"to {row.Connection?.Provider?.Repo} on {row.Branch} with the message \"{CommitMessage.Trim()}\". Anyone updating from that branch receives it.",
            "Commit to Git", () => RunCommitAsync(row, all ? null : chosen, CommitMessage.Trim()));
    }

    Task RunCommitAsync(GitRow row, List<GitChange>? items, string message) => Operate($"Commit '{row.Name}' to Git", row, async p =>
    {
        var status = await Step(p, "status", "Read the latest status", async () =>
        {
            var s = await _fabric.GitStatusAsync(row.Workspace.Id);
            return (s, $"{s.Uncommitted} uncommitted");
        });
        if (status.Uncommitted == 0) return "Nothing to commit any more.";
        await Step(p, "commit", $"Commit to {row.Branch}", async () =>
        {
            await _fabric.GitCommitAsync(row.Workspace.Id, status.WorkspaceHead, message, items,
                new Progress<int>(pc => p.Report(new("commit", $"Commit to {row.Branch}", StepState.Active, $"{pc}%", pc))));
            return (true, "Committed");
        });
        return $"Committed to {row.Branch}.";
    });

    // ── Offload ──────────────────────────────────────────────────────────────

    public void SetOffloadFolder(string path)
    {
        if (Selected is not { } row) return;
        OffloadFolder = path;
        _settings.OffloadFolders[row.Workspace.Id] = path;
        _settings.Save();
    }

    [RelayCommand]
    private Task RunOffload()
    {
        if (Selected is not { } row || OffloadFolder is not { } root || OffloadItems.Count == 0) return Task.CompletedTask;
        var items = OffloadItems.ToList();
        var dir = Offload.WorkspaceRoot(root, row.Workspace);
        return Operate($"Offload {items.Count} item(s) from '{row.Name}'", null, async p =>
        {
            var m = await Step(p, "export", "Export items to this computer", async () =>
            {
                var prog = new Progress<OffloadProgress>(o => p.Report(new("export", "Export items to this computer", StepState.Active,
                    $"{o.Done} / {o.Total} · {o.Current}", o.Total == 0 ? null : o.Done * 100 / o.Total)));
                var r = await Offload.RunAsync(_fabric, row.Workspace, items, dir, OffloadAll ? "Full copy" : "Items Git can't track", prog);
                return (r, $"{r.Saved} saved, {r.FailedCount} failed");
            });
            LastOffload = $"Last offload just now: {m.Saved} saved" + (m.FailedCount > 0 ? $", {m.FailedCount} failed" : "");
            var failed = m.Items.Where(i => !i.Ok).Select(i => $"{i.Name}: {i.Note}").ToList();
            return failed.Count == 0
                ? $"Saved {m.Saved} item(s) to {dir}. Each item has its own folder; _offload.json lists what was saved and how."
                : $"Saved {m.Saved}, {failed.Count} failed (kept any earlier copy): {string.Join("; ", failed.Take(3))}";
        });
    }

    // ── Repoint ──────────────────────────────────────────────────────────────

    void ResetRepoint()
    {
        TargetBranch = "";
        Checks.Clear();
        ChecksPassed = false;
        ClearPreview();
    }

    void ClearPreview()
    {
        _preview = null;
        HasPreview = false;
        Acknowledged = false;
        Plan.Clear();
        PlanBlocks.Clear();
        PlanWarnings.Clear();
        PreviewSummary = "";
        OnPropertyChanged(nameof(CanSwitch));
    }

    [RelayCommand]
    private async Task CheckRepoint()
    {
        if (Selected is not { Connection: { } conn } row) return;
        Checks.Clear();
        ChecksPassed = false;
        GitStatus? status = null;
        string? statusError = null;
        try { status = await _fabric.GitStatusAsync(row.Workspace.Id); }
        catch (FabricException e) { statusError = Friendly(e); }
        try { _credentials = await _fabric.GitCredentialsAsync(row.Workspace.Id); }
        catch (FabricException) { _credentials = null; }
        try { _items = await _fabric.ItemsAsync(row.Workspace.Id); }
        catch (FabricException e) { DetailError = e.Message; return; }
        if (Selected != row) return;
        var target = TargetBranch.Trim();
        foreach (var c in Git.Preflight(conn, status, statusError, _credentials, _items, target, OffloadFolder)) Checks.Add(new CheckRow(c));
        ChecksPassed = Checks.All(c => c.Ok);
    }

    [RelayCommand]
    private void PrepareRepoint()
    {
        if (Selected is not { Connection: { Provider: { } p } conn } row || !ChecksPassed || _credentials is not { } creds || OffloadFolder is not { } root) return;
        var target = TargetBranch.Trim();
        Ask($"Prepare to switch '{row.Name}' to {target}?",
            $"Daxis first saves every item of '{row.Name}' to {Offload.WorkspaceRoot(root, row.Workspace)}\\_backups. " +
            $"Then it disconnects the workspace from {p.Branch} and connects it to {target} to read exactly what would change. " +
            "No workspace item is changed in this step. Until you switch or cancel, Fabric shows the workspace on the new branch. " +
            "Needs the workspace Admin role.",
            "Back up and preview", () => Operate($"Prepare switch to {target}", row, async prog =>
            {
                var preview = await new Repointer(_fabric).PrepareAsync(row.Workspace, _items, conn, creds, target, root, prog);
                ShowPreview(preview);
                return preview.Blocked
                    ? "Blocked: see the reasons under Repoint. Cancel to put the workspace back on its branch."
                    : preview.CommitsIntoEmptyBranch
                        ? $"{target} has nothing in {p.Folder} yet. Switching copies the workspace into it and changes no items."
                        : $"Ready. Review the {preview.Changes.Count} change(s) under Repoint, then switch or cancel.";
            }, refreshAfter: false));
    }

    void ShowPreview(RepointPreview preview)
    {
        _preview = preview;
        Plan.Clear();
        foreach (var c in preview.Changes) Plan.Add(new PlanRow(c));
        PlanBlocks.Clear();
        foreach (var b in preview.Blocks) PlanBlocks.Add(b);
        PlanWarnings.Clear();
        foreach (var w in preview.Warnings) PlanWarnings.Add(w);
        var c2 = preview.Changes;
        PreviewSummary = preview.CommitsIntoEmptyBranch
            ? $"{preview.Journal.Target} is empty: the workspace content is committed into it. No item changes."
            : $"{c2.Count(c => c.Impact == Impact.Delete)} deleted · {c2.Count(c => c.Impact == Impact.Overwrite)} overwritten · " +
              $"{c2.Count(c => c.Impact == Impact.Create)} added. Backup: {preview.Backup.Saved} item(s) in {preview.Journal.BackupPath}";
        PreviewTitle = $"{preview.Journal.Original.Branch}  →  {preview.Journal.Target}";
        HasPreview = true;
        DetailPage = 2;
        OnPropertyChanged(nameof(CanSwitch));
    }

    [RelayCommand]
    private void ApplyRepoint()
    {
        if (_preview is not { } preview || Selected is not { } row || !CanSwitch) return;
        var c = preview.Changes;
        Ask($"Switch '{row.Name}' to {preview.Journal.Target}?",
            preview.CommitsIntoEmptyBranch
                ? $"Commits the workspace content into {preview.Journal.Target}. No workspace item changes."
                : $"{c.Count(x => x.Impact == Impact.Delete)} item(s) are deleted, {c.Count(x => x.Impact == Impact.Overwrite)} overwritten and " +
                  $"{c.Count(x => x.Impact == Impact.Create)} added to match {preview.Journal.Target}. Their current versions stay in Git on " +
                  $"{preview.Journal.Original.Branch} and in the local backup at {preview.Journal.BackupPath}.",
            $"Switch to {preview.Journal.Target}", () => Operate($"Switch '{row.Name}' to {preview.Journal.Target}", row, async p =>
            {
                await new Repointer(_fabric).ApplyAsync(preview, p);
                ClearPreview();
                TargetBranch = "";
                return $"'{row.Name}' is now on {preview.Journal.Target}. The backup stays at {preview.Journal.BackupPath}.";
            }));
    }

    [RelayCommand]
    private void CancelRepoint()
    {
        if (_preview is not { } preview || Selected is not { } row) return;
        Ask($"Put '{row.Name}' back on {preview.Journal.Original.Branch}?",
            $"Disconnects it from {preview.Journal.Target} and reconnects {preview.Journal.Original.Branch}. No workspace item was changed, so none will be.",
            "Restore original branch", () => Operate($"Restore '{row.Name}' to {preview.Journal.Original.Branch}", row, async p =>
            {
                await new Repointer(_fabric).RestoreAsync(preview.Journal, p);
                ClearPreview();
                return $"'{row.Name}' is back on {preview.Journal.Original.Branch}.";
            }));
    }

    // ── Recovery after a crash or closed app ─────────────────────────────────

    void LoadRecoveries()
    {
        Recoveries.Clear();
        foreach (var j in RepointJournal.All())
        {
            if (j.NeedsRecovery) Recoveries.Add(new Recovery(j));
            else j.Delete(); // stopped before touching the connection, or finished: nothing to recover
        }
    }

    [RelayCommand]
    private void Recover(Recovery r)
    {
        var row = _rows.FirstOrDefault(x => x.Workspace.Id == r.Journal.WorkspaceId);
        Ask($"Restore '{r.Journal.WorkspaceName}' to {r.Journal.Original.Branch}?",
            $"Reconnects the workspace to {r.Journal.Original.Branch} ({r.Journal.Original.Repo})" +
            (r.Journal.ContentChanged ? " and updates its content back to that branch, because the switch had started changing items." : ". No items were changed.") +
            (r.Journal.BackupPath is { } b ? $" The backup is at {b}." : ""),
            "Restore original branch", () => Operate($"Restore '{r.Journal.WorkspaceName}'", row, async p =>
            {
                await new Repointer(_fabric).RestoreAsync(r.Journal, p);
                Recoveries.Remove(r);
                if (_preview?.Journal.WorkspaceId == r.Journal.WorkspaceId) ClearPreview();
                return $"'{r.Journal.WorkspaceName}' is back on {r.Journal.Original.Branch}.";
            }));
    }

    // ── Operation runner ─────────────────────────────────────────────────────

    /// <summary>Runs one multi-step operation with the stepper visible. Returns the final message; failures stay on screen.</summary>
    async Task Operate(string title, GitRow? row, Func<IProgress<StepUpdate>, Task<string>> run, bool refreshAfter = true)
    {
        if (IsOperating) return;
        IsOperating = true;
        ShowOperation = true;
        OperationTitle = title;
        OperationResult = null;
        OperationFailed = false;
        DetailError = null;
        Steps.Clear();
        var progress = new Progress<StepUpdate>(u =>
        {
            var step = Steps.FirstOrDefault(s => s.Key == u.Key);
            if (step is null) Steps.Add(step = new StepRow(u.Key, u.Title));
            step.Title = u.Title;
            step.State = u.State;
            step.Percent = u.Percent;
            if (u.Detail is not null || u.State != StepState.Active) step.Detail = u.Detail;
        });
        try { OperationResult = await run(progress); }
        catch (Exception e)
        {
            OperationFailed = true;
            OperationResult = e is FabricException f ? Friendly(f) : e.Message;
            LoadRecoveries(); // a failed restore leaves its journal for the banner
        }
        finally { IsOperating = false; }
        if (row is not null && refreshAfter) await RefreshRowAsync(row);
    }

    static async Task<T> Step<T>(IProgress<StepUpdate> p, string key, string title, Func<Task<(T, string)>> run)
    {
        p.Report(new(key, title, StepState.Active));
        try
        {
            var (r, detail) = await run();
            p.Report(new(key, title, StepState.Done, detail));
            return r;
        }
        catch (Exception e)
        {
            p.Report(new(key, title, StepState.Failed, e.Message));
            throw;
        }
    }

    [RelayCommand]
    private void CloseOperation()
    {
        if (!IsOperating) ShowOperation = false;
    }

    /// <summary>Service error codes → what to do about them.</summary>
    internal static string Friendly(FabricException e) => e.Code switch
    {
        "InsufficientPrivileges" => "You don't have the role this needs in the workspace (connect, disconnect and switching branch need Admin).",
        "WorkspaceNotConnectedToGit" => "The workspace isn't connected to Git.",
        "WorkspaceHasNoCapacityAssigned" => "Git integration needs the workspace on a Fabric or Premium capacity.",
        "WorkspaceHeadMismatch" => "Someone changed the workspace or branch meanwhile. Refresh and try again.",
        "WorkspacePreviousOperationInProgress" => "Another Git operation is still running on this workspace. Wait and retry.",
        "PrincipalTypeNotSupported" => "This operation doesn't support your identity type.",
        "GitCredentialsNotConfigured" or "GitCredentialsConfigurationNotSupported" => "Set up your Git account in the workspace's Source control panel in Fabric first.",
        "MissingDependency" or "DependencyDeletionFailed" => $"Blocked by an item dependency: {e.Message}",
        _ when e.Status == System.Net.HttpStatusCode.Forbidden => $"Access denied. {e.Message}",
        _ => e.Message,
    };

    internal static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    public override void Dispose() => _scan?.Cancel();
}
