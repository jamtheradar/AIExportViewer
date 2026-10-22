namespace ExportViewer.App.Services;

/// <summary>Native "save as" dialog for reports. Returns where the file was saved, or null when the user cancels.</summary>
public sealed class FileSaver
{
    private readonly AppSettings _settings;

    public FileSaver(AppSettings settings) => _settings = settings;

#if WINDOWS
    public async Task<string?> SaveAsync(string fileName, byte[] content)
    {
        var ext = Path.GetExtension(fileName);
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(fileName),
        };
        picker.FileTypeChoices.Add(ext == ".pdf" ? "PDF document" : "CSV file (opens in Excel)", [ext]);
        if (!FolderPicker.Attach(picker)) return null;
        var file = await picker.PickSaveFileAsync();
        if (file is null) return null;
        await Windows.Storage.FileIO.WriteBytesAsync(file, content);
        return file.Path;
    }
#elif MACCATALYST
    public async Task<string?> SaveAsync(string fileName, byte[] content)
    {
        // The Mac dialog exports an existing file, so the report is written to the app's own folder first and
        // removed as soon as the dialog closes.
        Directory.CreateDirectory(_settings.ExportsTempFolder);
        var temp = Path.Combine(_settings.ExportsTempFolder, fileName);
        await File.WriteAllBytesAsync(temp, content);
        try
        {
            var tcs = new TaskCompletionSource<string?>();
            var picker = new UIKit.UIDocumentPickerViewController([Foundation.NSUrl.FromFilename(temp)], true);
            picker.DidPickDocumentAtUrls += (_, e) => tcs.TrySetResult(e.Urls.FirstOrDefault()?.Path);
            picker.WasCancelled += (_, _) => tcs.TrySetResult(null);
            var vc = Platform.GetCurrentUIViewController();
            if (vc is null) return null;
            vc.PresentViewController(picker, true, null);
            return await tcs.Task;
        }
        finally
        {
            File.Delete(temp);
        }
    }
#else
    public Task<string?> SaveAsync(string fileName, byte[] content)
    {
        return Task.FromResult<string?>(null);
    }
#endif
}
