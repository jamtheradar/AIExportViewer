namespace ExportViewer.App.Services;

/// <summary>App-wide Basic / Expert switch. Every page reads <see cref="IsExpert"/> and re-renders on <see cref="Changed"/>.</summary>
public sealed class ViewModeService
{
    private readonly AppSettings _settings;

    public ViewModeService(AppSettings settings)
    {
        _settings = settings;
        Mode = settings.Data.DefaultViewMode;
    }

    public ViewMode Mode { get; private set; }

    public bool IsExpert => Mode == ViewMode.Expert;

    public event Action? Changed;

    public void Set(ViewMode mode)
    {
        if (mode == Mode) return;
        Mode = mode;
        Changed?.Invoke();
    }
}
