using Avalonia.Controls;
using Daxis.Core;

namespace Daxis.App.Views;

public sealed partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
        LineageFind.TextChanged += (_, _) => LineageCanvas.Find(LineageFind.Text ?? "");
        LineageFit.Click += (_, _) => LineageCanvas.Fit();

        // Per-model rows carry their model in Tag.
        AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Tag: FabricItem m } b && b.Classes.Contains("model-open") && DataContext is WorkspaceTab vm) vm.OpenModel(m);
        });

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not WorkspaceTab vm) return;
            OpenWeb.Click += (_, _) => Web.Open(this, vm.WebUrl);
            ModelsGrid.DoubleTapped += (_, _) => { if (ModelsGrid.SelectedItem is ModelRow m) vm.OpenModel(m); };
            UsageReportsGrid.DoubleTapped += (_, _) =>
            {
                if (UsageReportsGrid.SelectedItem is ReportRow r) Web.Open(this, r.Ref.WebUrl);
            };
            ReportsGrid.DoubleTapped += (_, _) =>
            {
                if (ReportsGrid.SelectedItem is WorkspaceReportRow r) Web.Open(this, r.Report.Ref.WebUrl);
            };
            ModelsMap.TileClicked += t => { if (t.Tag is FabricItem m) vm.OpenModel(m); };
            LineageCanvas.NodeOpened += n =>
            {
                if (n.Kind == GKind.Model && vm.Models.Any(m => "m:" + m.Item.Id == n.Id)) vm.Open(n);
                else Web.Open(this, n.Link);
            };
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WorkspaceTab.Page) && vm.Page == 1) LineageCanvas.Replay();
            };
        };
    }
}
