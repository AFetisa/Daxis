using Avalonia.Controls;
using Avalonia.Input;

namespace Daxis.App;

public sealed partial class MainWindow : Window
{
    // One view per tab so editor undo history, caret and scroll survive tab switches.
    readonly Dictionary<TabBase, Control> _views = [];
    string? _closeArmedFor;

    public MainWindow()
    {
        InitializeComponent();
        // Subpixel (ClearType-style) glyphs: Inter at UI sizes reads thin and cramped with greyscale antialiasing.
        Avalonia.Media.RenderOptions.SetTextRenderingMode(this, Avalonia.Media.TextRenderingMode.SubpixelAntialias);
        Opened += async (_, _) =>
        {
            var vm = (MainViewModel)DataContext!;
            vm.TabClosed += tab => _views.Remove(tab);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedTab)) ShowTab(vm.SelectedTab);
                if (e.PropertyName == nameof(MainViewModel.SelectedWorkspace)) ResetWorkspaceBox();
            };
            WireWorkspaceSearch(vm);
            // Footer links carry their URL in Tag.
            AddHandler(Button.ClickEvent, (_, e) =>
            {
                if (e.Source is Button { Tag: string url } b && b.Classes.Contains("link")) Web.Open(this, url);
            });
            await vm.InitAsync();
        };

        // Same two-step guard as closing a tab: never drop unsaved work silently.
        Closing += (_, e) =>
        {
            if (DataContext is not MainViewModel vm) return;
            var dirty = vm.Tabs.Where(t => t.IsDirty).Select(t => t.Title).ToList();
            var key = string.Join("\n", dirty);
            if (dirty.Count == 0 || key == _closeArmedFor) return; // second close for the same unsaved set
            e.Cancel = true;
            _closeArmedFor = key;
            vm.SetStatus($"Unsaved changes in {string.Join(", ", dirty)}. Close again to discard them.", error: true);
        };
    }

    // Workspace search: typing filters, picking switches, leaving without a pick restores the current name.
    void WireWorkspaceSearch(MainViewModel vm)
    {
        WorkspaceBox.SelectionChanged += (_, _) =>
        {
            if (WorkspaceBox.SelectedItem is Core.Workspace w && w != vm.SelectedWorkspace)
            {
                vm.SelectedWorkspace = w;
                // Close after the click finishes: closing the popup inside its own pointer handler crashes Avalonia.
                Avalonia.Threading.Dispatcher.UIThread.Post(LeaveWorkspaceBox);
            }
        };
        WorkspaceBox.GotFocus += (_, _) =>
        {
            // Show the full list on focus; typing narrows it.
            WorkspaceBox.Text = "";
            WorkspaceBox.IsDropDownOpen = true;
        };
        WorkspaceBox.LostFocus += (_, _) => ResetWorkspaceBox();
        // Tunnel: the open dropdown would otherwise swallow Esc and leave the half-typed search behind.
        WorkspaceBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { LeaveWorkspaceBox(); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control) && vm.IsSignedIn)
            {
                WorkspaceBox.Focus();
                e.Handled = true;
            }
        };
    }

    void LeaveWorkspaceBox()
    {
        WorkspaceBox.IsDropDownOpen = false;
        FocusManager?.ClearFocus();
        ResetWorkspaceBox(force: true);
    }

    void ResetWorkspaceBox(bool force = false)
    {
        if (!force && WorkspaceBox.IsKeyboardFocusWithin) return;
        var ws = (DataContext as MainViewModel)?.SelectedWorkspace;
        WorkspaceBox.SelectedItem = ws;
        WorkspaceBox.Text = ws?.DisplayName ?? "";
    }

    void ShowTab(TabBase? tab)
    {
        if (tab is null) { Host.Content = null; return; }
        if (!_views.TryGetValue(tab, out var view))
        {
            view = tab switch
            {
                ModelTab => new Views.ModelView(),
                NotebookTab => new Views.NotebookView(),
                WorkspaceTab => new Views.WorkspaceView(),
                SourceControlTab => new Views.SourceControlView(),
                EstateQualityTab => new Views.EstateQualityView(),
                _ => new Views.LakehouseView(),
            };
            view.DataContext = tab;
            if (view.FindControl<AvaloniaEdit.TextEditor>("Editor") is { } editor)
                tab.DocumentReset += () => editor.Document.UndoStack.ClearAll();
            _views[tab] = view;
        }
        Host.Content = view;
    }
}
