using Avalonia.Controls;

namespace Daxis.App.Views;

public sealed partial class EstateQualityView : UserControl
{
    public EstateQualityView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not EstateQualityTab vm) return;
            ModelsGrid.DoubleTapped += (_, _) =>
            {
                if (ModelsGrid.SelectedItem is EstateModel m) vm.Open(m);
            };
            vm.ExportRequested += format => _ = QualityFiles.SaveAsync(this, format, vm.ExportModels(), "Estate");
        };
    }
}
