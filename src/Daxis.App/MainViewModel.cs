using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daxis.Core;

namespace Daxis.App;

public sealed partial class ItemRow(FabricItem item, Action<ItemRow> open) : ObservableObject
{
    public FabricItem Item { get; } = item;
    public Avalonia.Media.Geometry Icon => Item.Type switch
    {
        MainViewModel.SemanticModel => Icons.Model,
        MainViewModel.Notebook => Icons.Notebook,
        MainViewModel.WorkspaceOverview => Icons.Workspace,
        SourceControlTab.ItemType => Icons.Branch,
        _ => Icons.Lakehouse,
    };
    [ObservableProperty] private bool _isActive;
    [RelayCommand] private void Open() => open(this);
}

public sealed partial class MainViewModel : ObservableObject
{
    public const string SemanticModel = "SemanticModel", Notebook = "Notebook", Lakehouse = "Lakehouse";
    /// <summary>Pseudo item type for the workspace overview tab (its id is the workspace id).</summary>
    public const string WorkspaceOverview = "WorkspaceOverview";

    readonly Settings _settings;
    readonly Auth _auth = new();
    readonly FabricClient _fabric;
    List<ItemRow> _rows = [];

    public ObservableCollection<Workspace> Workspaces { get; } = [];
    public ObservableCollection<ItemRow> Models { get; } = [];
    public ObservableCollection<ItemRow> Notebooks { get; } = [];
    public ObservableCollection<ItemRow> Lakehouses { get; } = [];
    public ObservableCollection<TabBase> Tabs { get; } = [];

    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string? _account;
    [ObservableProperty] private Workspace? _selectedWorkspace;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private TabBase? _selectedTab;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private ItemRow? _workspaceRow;
    [ObservableProperty] private ItemRow? _sourceControlRow;

    public bool ShowEmptyState => IsSignedIn && Tabs.Count == 0;
    partial void OnIsSignedInChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));

    public string Initials => string.IsNullOrEmpty(Account) ? "" : Account[..1].ToUpperInvariant();

    public MainViewModel(Settings settings)
    {
        _settings = settings;
        _isDark = !settings.Light;
        _fabric = new FabricClient(_auth);
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmptyState));
    }

    public async Task InitAsync()
    {
        await Run("Restoring session…", async () =>
        {
            if (await _auth.TryRestoreAsync()) await OnSignedInAsync();
        });
    }

    [RelayCommand]
    private Task SignIn() => Run("Waiting for sign-in in your browser…", async () =>
    {
        await _auth.SignInAsync();
        await OnSignedInAsync();
    });

    bool _signOutArmed;

    [RelayCommand]
    private async Task SignOut()
    {
        // Same two-step guard as closing: never drop unsaved work silently.
        var dirty = Tabs.Where(t => t.IsDirty).Select(t => t.Title).ToList();
        if (dirty.Count > 0 && !_signOutArmed)
        {
            _signOutArmed = true;
            SetStatus($"Unsaved changes in {string.Join(", ", dirty)}. Sign out again to discard them.", error: true);
            return;
        }
        _signOutArmed = false;
        foreach (var t in Tabs.ToList()) Remove(t);
        await _auth.SignOutAsync();
        IsSignedIn = false;
        Account = null;
        Workspaces.Clear();
        SetRows([]);
        WorkspaceRow = null;
        SourceControlRow = null;
        SetStatus("Signed out");
    }

    async Task OnSignedInAsync()
    {
        Account = _auth.Account?.Username;
        OnPropertyChanged(nameof(Initials));
        IsSignedIn = true;
        var list = await _fabric.WorkspacesAsync();
        Workspaces.Clear();
        foreach (var w in list) Workspaces.Add(w);
        SourceControlRow = new ItemRow(new FabricItem("daxis:source-control", "Source control", SourceControlTab.ItemType,
            "Git status of every workspace: sync, commit, offload and branch repointing", ""), Open);
        SelectedWorkspace = list.FirstOrDefault(w => w.Id == _settings.Workspace) ?? list.FirstOrDefault();
    }

    partial void OnSelectedWorkspaceChanged(Workspace? value)
    {
        if (value is null) return;
        _settings.Workspace = value.Id;
        _settings.Save();
        WorkspaceRow = new ItemRow(new FabricItem(value.Id, value.DisplayName, WorkspaceOverview, "Everything in this workspace", value.Id), Open);
        MarkActive();
        _ = LoadItemsAsync();
    }

    [RelayCommand]
    private Task LoadItems() => LoadItemsAsync();

    Task LoadItemsAsync() => Run("Loading items…", async () =>
    {
        if (SelectedWorkspace is not { } ws) return;
        SetRows([]); // never leave the previous workspace's items clickable while switching
        var items = await _fabric.ItemsAsync(ws.Id);
        if (ws != SelectedWorkspace) return; // user switched while loading
        SetRows(items.Where(i => i.Type is SemanticModel or Notebook or Lakehouse).Select(i => new ItemRow(i, Open)).ToList());
    });

    void SetRows(List<ItemRow> rows)
    {
        _rows = rows;
        ApplyFilter();
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    void ApplyFilter()
    {
        var match = _rows.Where(r => r.Item.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase)).ToList();
        Fill(Models, match.Where(r => r.Item.Type == SemanticModel));
        Fill(Notebooks, match.Where(r => r.Item.Type == Notebook));
        Fill(Lakehouses, match.Where(r => r.Item.Type == Lakehouse));
        HasItems = _rows.Count > 0;
        MarkActive();

        static void Fill(ObservableCollection<ItemRow> target, IEnumerable<ItemRow> rows)
        {
            target.Clear();
            foreach (var r in rows) target.Add(r);
        }
    }

    async void Open(ItemRow row)
    {
        if (Tabs.FirstOrDefault(t => t.Item.Id == row.Item.Id) is { } open)
        {
            SelectedTab = open;
            return;
        }
        if (row.Item.Type == SourceControlTab.ItemType)
        {
            if ((SelectedWorkspace ?? Workspaces.FirstOrDefault()) is not { } any) return;
            await Add(new SourceControlTab(row.Item, any, _fabric, _settings, () => Workspaces.ToList()));
            return;
        }
        // The item's own workspace, never "whatever is selected now": a model open must target exactly this item.
        if (Workspaces.FirstOrDefault(w => w.Id == row.Item.WorkspaceId) is not { } ws) return;
        TabBase tab = row.Item.Type switch
        {
            SemanticModel => new ModelTab(row.Item, ws, _auth, _fabric, () => Workspaces.ToList()),
            Notebook => new NotebookTab(row.Item, ws, _fabric),
            WorkspaceOverview => new WorkspaceTab(row.Item, ws, _auth, _fabric, () => Workspaces.ToList(),
                id => { if (_rows.FirstOrDefault(r => r.Item.Id == id) is { } r) Open(r); }),
            _ => new LakehouseTab(row.Item, ws, _fabric),
        };
        await Add(tab);
    }

    async Task Add(TabBase tab)
    {
        tab.CloseRequested += OnCloseRequested;
        Tabs.Add(tab);
        SelectedTab = tab;
        await tab.LoadAsync();
    }

    void OnCloseRequested(TabBase tab)
    {
        // Two-step close for unsaved work: first click warns, second discards.
        if (tab.IsDirty && !tab.CloseArmed)
        {
            tab.CloseArmed = true;
            SetStatus($"'{tab.Title}' has unsaved changes. Close again to discard them.", error: true);
            return;
        }
        Remove(tab);
    }

    void Remove(TabBase tab)
    {
        var i = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        tab.Dispose();
        if (SelectedTab == tab || SelectedTab is null)
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(i, 0, Tabs.Count - 1)];
        TabClosed?.Invoke(tab);
    }

    public event Action<TabBase>? TabClosed;

    partial void OnSelectedTabChanged(TabBase? value) => MarkActive();

    void MarkActive()
    {
        foreach (var r in _rows) r.IsActive = r.Item.Id == SelectedTab?.Item.Id;
        if (WorkspaceRow is { } w) w.IsActive = w.Item.Id == SelectedTab?.Item.Id;
        if (SourceControlRow is { } g) g.IsActive = g.Item.Id == SelectedTab?.Item.Id;
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDark = !IsDark;
        Application.Current!.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        _settings.Light = !IsDark;
        _settings.Save();
    }

    public void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    async Task Run(string message, Func<Task> work)
    {
        IsBusy = true;
        SetStatus(message);
        try
        {
            await work();
            SetStatus("Ready");
        }
        catch (Exception e)
        {
            SetStatus(e.Message, error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
