using Avalonia.Controls;

namespace Daxis.App.Views;

public sealed partial class NotebookView : UserControl
{
    public NotebookView()
    {
        InitializeComponent();
        // ponytail: Python colours for every notebook language; add SQL/Scala definitions if those notebooks become common.
        Editors.Configure(Editor, "Python");

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not NotebookTab vm) return;
            Editors.Bind(Editor, () => vm.Content, v => vm.Content = v, h => vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NotebookTab.Content)) h();
            });
            OpenWeb.Click += (_, _) => Web.Open(this, vm.WebUrl);
        };
    }
}
