namespace ExportViewer.App.Services;

/// <summary>Native "choose a folder" and "choose zip files" dialogs. Return null when the user cancels.</summary>
public sealed class FolderPicker
{
#if WINDOWS
    public async Task<string?> PickAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add("*");
        if (!Attach(picker)) return null;
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public async Task<IReadOnlyList<string>?> PickZipsAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".json");
        if (!Attach(picker)) return null;
        var files = await picker.PickMultipleFilesAsync();
        return files is { Count: > 0 } ? files.Select(f => f.Path).ToList() : null;
    }

    internal static bool Attach(object picker)
    {
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as MauiWinUIWindow;
        if (window is null) return false;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window.WindowHandle);
        return true;
    }
#elif MACCATALYST
    public async Task<string?> PickAsync() => (await Pick([UniformTypeIdentifiers.UTTypes.Folder], false))?.FirstOrDefault();

    public Task<IReadOnlyList<string>?> PickZipsAsync() => Pick([UniformTypeIdentifiers.UTTypes.Zip, UniformTypeIdentifiers.UTTypes.Json], true);

    private static Task<IReadOnlyList<string>?> Pick(UniformTypeIdentifiers.UTType[] types, bool many)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<string>?>();
        var picker = new UIKit.UIDocumentPickerViewController(types, false) { AllowsMultipleSelection = many };
        picker.DidPickDocumentAtUrls += (_, e) =>
        {
            // Sandbox: keep access to what was chosen for this session.
            foreach (var url in e.Urls) url.StartAccessingSecurityScopedResource();
            var paths = e.Urls.Select(u => u.Path).OfType<string>().ToList();
            tcs.TrySetResult(paths.Count > 0 ? paths : null);
        };
        picker.WasCancelled += (_, _) => tcs.TrySetResult(null);
        var vc = Platform.GetCurrentUIViewController();
        if (vc is null) return Task.FromResult<IReadOnlyList<string>?>(null);
        vc.PresentViewController(picker, true, null);
        return tcs.Task;
    }
#else
    public Task<string?> PickAsync() => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>?> PickZipsAsync() => Task.FromResult<IReadOnlyList<string>?>(null);
#endif
}
