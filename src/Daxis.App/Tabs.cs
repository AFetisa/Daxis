using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daxis.Core;
using Microsoft.AnalysisServices.Tabular;

namespace Daxis.App;

/// <summary>A pending action that changes something in the service; runs only after the user confirms.</summary>
public sealed record Confirmation(string Title, string Body, string Action, Func<Task> Run);

/// <summary>An open Fabric item. Owns its own busy/error state so one failing tab never blocks another.</summary>
public abstract partial class TabBase(FabricItem item, Workspace workspace) : ObservableObject, IDisposable
{
    public FabricItem Item { get; } = item;
    public Workspace Workspace { get; } = workspace;
    public string Title => Item.DisplayName;
    public abstract Geometry Icon { get; }
    public abstract string WebUrl { get; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private Confirmation? _pendingConfirm;

    /// <summary>Every write to the service goes through here (or the model review) so nothing changes on one click.</summary>
    protected void Ask(string title, string body, string action, Func<Task> run) => PendingConfirm = new(title, body, action, run);

    [RelayCommand]
    private async Task ConfirmPending()
    {
        var c = PendingConfirm;
        PendingConfirm = null;
        if (c is not null) await c.Run();
    }

    [RelayCommand] private void CancelPending() => PendingConfirm = null;

    public bool CloseArmed { get; set; }
    public event Action<TabBase>? CloseRequested;

    /// <summary>Raised when the editor shows a different document, so views can drop undo history.</summary>
    public event Action? DocumentReset;
    protected void ResetDocument() => DocumentReset?.Invoke();

    [RelayCommand] private void Close() => CloseRequested?.Invoke(this);
    [RelayCommand] private void DismissError() => Error = null;

    partial void OnIsDirtyChanged(bool value) => CloseArmed = false;

    // Notices are transient confirmations; clear after a few seconds unless replaced.
    async partial void OnNoticeChanged(string? value)
    {
        if (value is null) return;
        await Task.Delay(4000);
        if (Notice == value) Notice = null;
    }

    public abstract Task LoadAsync();
    [RelayCommand]
    private Task Reload()
    {
        if (!IsDirty) return LoadAsync();
        Ask("Reload and discard your changes?",
            $"'{Item.DisplayName}' has unsaved changes. Reloading fetches the version in the service and drops them.",
            "Discard and reload", LoadAsync);
        return Task.CompletedTask;
    }

    protected async Task Busy(string text, Func<Task> work)
    {
        IsBusy = true;
        BusyText = text;
        Error = null;
        try { await work(); }
        catch (Exception e) { Error = e.InnerException?.Message ?? e.Message; }
        finally { IsBusy = false; }
    }

    public virtual void Dispose() { }
}

// ── Notebook ─────────────────────────────────────────────────────────────────

public sealed partial class NotebookTab(FabricItem item, Workspace ws, FabricClient fabric) : TabBase(item, ws)
{
    string _path = "notebook-content.py";
    string _saved = "";
    CancellationTokenSource? _poll;

    public override Geometry Icon => Icons.Notebook;
    public override string WebUrl => $"https://app.fabric.microsoft.com/groups/{Workspace.Id}/synapsenotebooks/{Item.Id}";

    [ObservableProperty] private string _content = "";
    [ObservableProperty] private string _language = "";
    [ObservableProperty] private int _cellCount;
    [ObservableProperty] private string? _runStatus;
    [ObservableProperty] private bool _isRunning;

    public override Task LoadAsync() => Busy("Downloading notebook definition…", async () =>
    {
        var parts = await fabric.GetDefinitionAsync(Item);
        var part = parts.FirstOrDefault(p => p.Path.StartsWith("notebook-content", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Notebook definition has no notebook-content part.");
        _path = part.Path;
        _saved = part.Payload;
        Language = Path.GetExtension(_path).TrimStart('.').ToUpperInvariant();
        Content = part.Payload;
        IsDirty = false;
        ResetDocument();
    });

    partial void OnContentChanged(string value)
    {
        IsDirty = value != _saved;
        CellCount = CountCells(value);
    }

    internal static int CountCells(string source) =>
        source.Split('\n').Count(l => l.StartsWith("# CELL ****") || l.StartsWith("-- CELL ****") || l.StartsWith("// CELL ****"));

    [RelayCommand]
    private void Save()
    {
        if (!IsDirty) return;
        var (added, removed) = LineDelta(_saved, Content);
        Ask("Save notebook to Fabric?",
            $"Overwrites the source of '{Item.DisplayName}' in '{Workspace.DisplayName}' (+{added} / −{removed} lines). " +
            "Anyone opening the notebook sees the new version.",
            "Save to Fabric", SaveNow);
    }

    /// <summary>Lines added / removed, ignoring order: a quick size-of-change signal for the confirmation.</summary>
    internal static (int Added, int Removed) LineDelta(string before, string after)
    {
        var old = Lines(before).GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count());
        var added = 0;
        foreach (var line in Lines(after))
            if (old.TryGetValue(line, out var n) && n > 0) old[line] = n - 1;
            else added++;
        return (added, old.Values.Sum());

        static string[] Lines(string s) => s.Replace("\r\n", "\n").Split('\n');
    }

    Task SaveNow() => Busy("Uploading notebook…", async () =>
    {
        // Don't overwrite work someone else saved in Fabric since this copy was loaded.
        var remote = (await fabric.GetDefinitionAsync(Item)).FirstOrDefault(p => p.Path == _path)?.Payload;
        if (remote is not null && remote != _saved)
            throw new InvalidOperationException("The notebook changed in Fabric after you opened it. Copy your edits, reload, and re-apply them.");
        var text = Content;
        await fabric.UpdateDefinitionAsync(Item, [new DefinitionPart(_path, text, "InlineBase64")]);
        _saved = text;
        IsDirty = Content != _saved;
        Notice = "Saved";
    });

    [RelayCommand]
    private void Run()
    {
        if (IsRunning) return;
        Ask("Run notebook in Fabric?",
            $"Starts a Spark job for '{Item.DisplayName}' on the capacity of '{Workspace.DisplayName}'. It runs the saved version" +
            (IsDirty ? "; your unsaved edits are not included." : "."),
            "Run now", RunNow);
    }

    async Task RunNow()
    {
        if (IsRunning) return;
        IsRunning = true;
        RunStatus = IsDirty ? "Starting (runs the saved version)…" : "Starting…";
        _poll = new CancellationTokenSource();
        try
        {
            var url = await fabric.RunJobAsync(Item, "RunNotebook", _poll.Token);
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _poll.Token);
                var state = await fabric.JobStateAsync(url, _poll.Token);
                RunStatus = state.Failure is null ? state.Status : $"{state.Status}: {state.Failure}";
                if (state.IsFinal) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { RunStatus = e.Message; }
        finally { IsRunning = false; }
    }

    public override void Dispose() => _poll?.Cancel();
}

// ── Lakehouse ────────────────────────────────────────────────────────────────

public sealed partial class LakehouseTab(FabricItem item, Workspace ws, FabricClient fabric) : TabBase(item, ws)
{
    bool _filesLoaded;

    public override Geometry Icon => Icons.Lakehouse;
    public override string WebUrl => $"https://app.fabric.microsoft.com/groups/{Workspace.Id}/lakehouses/{Item.Id}";

    public ObservableCollection<LakehouseTable> Tables { get; } = [];
    public ObservableCollection<OneLakeEntry> Files { get; } = [];

    [ObservableProperty] private LakehouseInfo? _info;
    [ObservableProperty] private string? _tablesMessage;
    [ObservableProperty] private string _path = "Files";
    [ObservableProperty] private string? _filesMessage;
    [ObservableProperty] private int _section;

    public override Task LoadAsync() => Busy("Loading lakehouse…", async () =>
    {
        Info = await fabric.LakehouseAsync(Item);
        Tables.Clear();
        try
        {
            foreach (var t in await fabric.LakehouseTablesAsync(Item)) Tables.Add(t);
            TablesMessage = Tables.Count == 0 ? "No tables yet." : null;
        }
        catch (FabricException e)
        {
            // Schema-enabled lakehouses don't support the List Tables API yet.
            TablesMessage = e.Message;
        }
        if (_filesLoaded) await LoadFilesAsync();
    });

    partial void OnSectionChanged(int value)
    {
        if (value == 1 && !_filesLoaded) _ = LoadFilesAsync();
    }

    async Task LoadFilesAsync()
    {
        _filesLoaded = true;
        FilesMessage = "Loading…";
        var path = Path;
        try
        {
            var list = await fabric.ListFilesAsync(Item, path);
            if (path != Path) return; // user navigated again; a newer listing is on its way
            Files.Clear();
            foreach (var f in list) Files.Add(f);
            FilesMessage = Files.Count == 0 ? "This folder is empty." : null;
        }
        catch (Exception e) { FilesMessage = e.Message; }
    }

    public Task OpenEntry(OneLakeEntry e)
    {
        if (!e.IsDirectory) return Task.CompletedTask;
        Path = e.FullPath;
        return LoadFilesAsync();
    }

    [RelayCommand]
    private Task Up()
    {
        if (Path == "Files") return Task.CompletedTask;
        Path = Path[..Path.LastIndexOf('/')];
        return LoadFilesAsync();
    }

    [RelayCommand]
    private Task ReloadFiles() => LoadFilesAsync();
}
