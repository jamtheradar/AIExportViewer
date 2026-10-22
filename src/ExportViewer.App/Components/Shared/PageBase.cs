using ExportViewer.App.Services;
using Microsoft.AspNetCore.Components;

namespace ExportViewer.App.Components.Shared;

/// <summary>Base for screens: re-renders when the view mode changes and reloads data when the library changes.</summary>
public abstract class PageBase : ComponentBase, IDisposable
{
    [Inject] protected LibrarySession Session { get; set; } = default!;
    [Inject] protected ViewModeService View { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    private int _loadedVersion = -1;

    protected bool Expert => View.IsExpert;
    protected ViewMode Mode => View.Mode;

    protected override void OnInitialized()
    {
        View.Changed += OnViewChanged;
        Session.Changed += OnSessionChanged;
    }

    protected override void OnParametersSet() => Reload();

    /// <summary>Called when the page should query the library again (first render, new parameters, or after a load).</summary>
    protected virtual void LoadData() { }

    protected void Reload()
    {
        if (!Session.IsReady) return;
        _loadedVersion = Session.Version;
        LoadData();
    }

    private void OnViewChanged() => InvokeAsync(StateHasChanged);

    private void OnSessionChanged() => InvokeAsync(() =>
    {
        if (Session.IsReady && Session.Version != _loadedVersion) Reload();
        StateHasChanged();
    });

    protected string When(string? utc) => Fmt.Date(utc, Mode);

    public virtual void Dispose()
    {
        View.Changed -= OnViewChanged;
        Session.Changed -= OnSessionChanged;
        GC.SuppressFinalize(this);
    }
}
