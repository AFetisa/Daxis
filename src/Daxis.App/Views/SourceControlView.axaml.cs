using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Daxis.App.Views;

public sealed partial class SourceControlView : UserControl
{
    public SourceControlView()
    {
        InitializeComponent();

        // Rows and recovery banners carry their model in Tag.
        AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (DataContext is not SourceControlTab vm) return;
            if (e.Source is Button { Tag: GitRow row } b && b.Classes.Contains("row")) vm.Select(row);
            else if (e.Source is Button { Tag: Recovery r } rb && rb.Classes.Contains("recover")) vm.RecoverCommand.Execute(r);
        });

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not SourceControlTab vm) return;
            OpenWeb.Click += (_, _) => Web.Open(this, vm.WebUrl);
            ChooseFolder.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
                var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Folder for offloads and repoint backups",
                    AllowMultiple = false,
                });
                if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) vm.SetOffloadFolder(path);
            };
            OpenFolder.Click += async (_, _) =>
            {
                var dir = Directory.Exists(vm.OffloadTarget) ? vm.OffloadTarget : vm.OffloadFolder;
                if (dir is not null && Directory.Exists(dir) && TopLevel.GetTopLevel(this) is { } top)
                    await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(dir));
            };
        };
    }
}
