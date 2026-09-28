using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Daxis.Core;

namespace Daxis.App.Views;

public sealed partial class LakehouseView : UserControl
{
    public LakehouseView()
    {
        InitializeComponent();

        FilesGrid.DoubleTapped += async (_, _) =>
        {
            if (DataContext is LakehouseTab vm && FilesGrid.SelectedItem is OneLakeEntry e) await vm.OpenEntry(e);
        };

        // Every "copy" button carries its value in Tag.
        AddHandler(Button.ClickEvent, async (_, e) =>
        {
            if (e.Source is Button { Tag: string text } b && b.Classes.Contains("copy") && text.Length > 0 &&
                TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        });

        DataContextChanged += (_, _) =>
        {
            if (DataContext is LakehouseTab vm)
                OpenWeb.Click += (_, _) => Web.Open(this, vm.WebUrl);
        };
    }
}
