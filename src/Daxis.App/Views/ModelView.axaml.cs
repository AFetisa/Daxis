using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Daxis.Core;

namespace Daxis.App.Views;

public sealed partial class ModelView : UserControl
{
    public ModelView()
    {
        InitializeComponent();
        Editors.Configure(Editor, "DAX");
        Editors.Configure(QueryEditor, "DAX");

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ModelTab vm) return;
            Editors.Bind(Editor, () => vm.Expression, v => vm.Expression = v, On(vm, nameof(ModelTab.Expression)));
            Editors.Bind(QueryEditor, () => vm.Query, v => vm.Query = v, On(vm, nameof(ModelTab.Query)));
            Editors.EnableDaxCompletion(Editor, () => vm.Symbols);
            Editors.EnableDaxCompletion(QueryEditor, () => vm.Symbols);
            OpenWeb.Click += (_, _) => Launch(vm.WebUrl);

            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ModelTab.Result)) ShowResult(vm.Result);
                if (e.PropertyName == nameof(ModelTab.Language)) Editors.SetLanguage(Editor, vm.Language);
            };
            vm.StepRequested += GoToStep;

            TablesGrid.DoubleTapped += (_, _) =>
            {
                if (TablesGrid.SelectedItem is TableInsight t) vm.OpenObject(t.Name, sharedQuery: false);
            };
            SharedGrid.DoubleTapped += (_, _) =>
            {
                if (SharedGrid.SelectedItem is SharedQueryInsight q) vm.OpenObject(q.Name, sharedQuery: true);
            };
            ReportsGrid.DoubleTapped += (_, _) =>
            {
                if (ReportsGrid.SelectedItem is ReportRow r) Launch(r.Ref.WebUrl);
            };
            QualityGrid.DoubleTapped += (_, _) =>
            {
                if (QualityGrid.SelectedItem is Finding f) vm.OpenFinding(f);
            };
            vm.ExportRequested += format => _ = QualityFiles.SaveAsync(this, format, vm.QualityExportModels(), vm.Title);

            // Graph cards open their object; the page's canvas replays its entrance each time it's shown.
            void Open(GNode n)
            {
                switch (n.Kind)
                {
                    case GKind.Table: vm.OpenObject(n.Title, sharedQuery: false); break;
                    case GKind.Query: vm.OpenObject(n.Title, sharedQuery: true); break;
                    case GKind.Report: Launch(n.Link); break;
                }
            }
            TablesMap.TileClicked += t => vm.MemoryTable = t.Tag as string ?? "";
            DiagramCanvas.NodeOpened += Open;
            LineageCanvas.NodeOpened += Open;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ModelTab.Page)) return;
                if (vm.Page == 2) DiagramCanvas.Replay();
                if (vm.Page == 3) LineageCanvas.Replay();
            };
        };

        DiagramFind.TextChanged += (_, _) => DiagramCanvas.Find(DiagramFind.Text ?? "");
        LineageFind.TextChanged += (_, _) => LineageCanvas.Find(LineageFind.Text ?? "");
        DiagramFit.Click += (_, _) => DiagramCanvas.Fit();
        LineageFit.Click += (_, _) => LineageCanvas.Fit();

        // Report rows carry their URL in Tag.
        AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Tag: string url } b && b.Classes.Contains("open") && url.Length > 0) Launch(url);
        });

        QueryEditor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is ModelTab vm)
            {
                e.Handled = true;
                vm.RunQueryCommand.Execute(null);
            }
        }, RoutingStrategies.Tunnel);
    }

    void Launch(string? url) => Web.Open(this, url);

    // Select the step's "Name =" line and scroll to it.
    void GoToStep(string rawName)
    {
        var text = Editor.Text;
        var i = text.IndexOf(rawName + " =", StringComparison.Ordinal);
        if (i < 0) i = text.IndexOf(rawName, StringComparison.Ordinal);
        if (i < 0) return;
        var line = Editor.Document.GetLineByOffset(i);
        Editor.Select(line.Offset, line.Length);
        Editor.ScrollToLine(line.LineNumber);
        Editor.Focus();
    }

    static Action<Action> On(INotifyPropertyChanged source, string property) =>
        handler => source.PropertyChanged += (_, e) => { if (e.PropertyName == property) handler(); };

    void ShowResult(QueryResult? result)
    {
        Results.Columns.Clear();
        Results.ItemsSource = null;
        if (result is null) return;
        for (var i = 0; i < result.Columns.Count; i++)
            Results.Columns.Add(new DataGridTextColumn
            {
                Header = result.Columns[i],
                Binding = new Binding($"[{i}]"),
                MaxWidth = 420,
            });
        Results.ItemsSource = result.Rows;
    }
}
