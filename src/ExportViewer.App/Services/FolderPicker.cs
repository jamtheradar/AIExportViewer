namespace ExportViewer.App.Services;

/// <summary>Native "choose a folder" dialog. Returns null when the user cancels.</summary>
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
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as MauiWinUIWindow;
        if (window is null) return null;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window.WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
#elif MACCATALYST
    public Task<string?> PickAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        var picker = new UIKit.UIDocumentPickerViewController([UniformTypeIdentifiers.UTTypes.Folder], false);
        picker.DidPickDocumentAtUrls += (_, e) =>
        {
            var url = e.Urls.FirstOrDefault();
            // Sandbox: keep access to the chosen folder for this session.
            url?.StartAccessingSecurityScopedResource();
            tcs.TrySetResult(url?.Path);
        };
        picker.WasCancelled += (_, _) => tcs.TrySetResult(null);
        var vc = Platform.GetCurrentUIViewController();
        if (vc is null) return Task.FromResult<string?>(null);
        vc.PresentViewController(picker, true, null);
        return tcs.Task;
    }
#else
    public Task<string?> PickAsync() => Task.FromResult<string?>(null);
#endif
}
