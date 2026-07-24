using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MobileEssControl.Services.Dialogs;

public static class AppFilePickerService
{
    public static async Task<string?> PickExcelFileAsync(string title)
    {
        Window? owner = null;

        if (Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop)
        {
            owner = desktop.MainWindow;
        }

        if (owner is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files =
            await owner.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Excel 파일 (*.xlsx)")
                        {
                            Patterns = new[] { "*.xlsx" }
                        }
                    }
                });

        if (files.Count == 0)
        {
            return null;
        }

        return files[0].TryGetLocalPath();
    }
}
