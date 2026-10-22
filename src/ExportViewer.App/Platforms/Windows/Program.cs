using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ExportViewer.App.WinUI;

/// <summary>
/// Replaces the generated WinUI entry point (DISABLE_XAML_GENERATED_MAIN in the project file) so the installer's
/// start-up work runs before anything else. Otherwise the same as the generated one.
/// </summary>
public static class Program
{
    [System.Runtime.InteropServices.DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static void Main()
    {
        ExportViewer.Download.AppUpdater.RunStartupHooks();

        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}
