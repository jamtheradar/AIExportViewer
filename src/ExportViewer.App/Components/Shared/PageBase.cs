using ExportViewer.App.Services;
using Microsoft.AspNetCore.Components;

namespace ExportViewer.App.Components.Shared;

/// <summary>Base for screens: re-renders when the view mode changes and reloads data when the library changes.</summary>
public abstract class PageBase : ComponentBase, IDisposable
{
    [Inject] protected LibrarySession Session { get; set; } = default!;
    [Inject] protected ViewModeService View { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] private PageMemory Memory { get; set; } = default!;

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

    /// <summary>The place saved by <see cref="Remember"/> for this export, or null.</summary>
    protected T? Recall<T>(string key) where T : class => Memory.Recall<T>(key, Session.Version);

    /// <summary>Saves where the reviewer is on a list (page, tab, filters) so coming back lands in the same place.</summary>
    protected void Remember(string key, object place) => Memory.Remember(key, Session.Version, place);

    /// <summary>Called when the screen closes: save the reviewer's place with <see cref="Remember"/>.</summary>
    protected virtual void SavePlace() { }

    /// <summary>An offset past the end (the data changed since it was saved) moves to the last page.</summary>
    protected static int ClampOffset(int offset, int total, int pageSize) =>
        offset < total || offset == 0 ? offset : Math.Max(0, (total - 1) / pageSize * pageSize);

    public virtual void Dispose()
    {
        if (Session.IsReady) SavePlace();
        View.Changed -= OnViewChanged;
        Session.Changed -= OnSessionChanged;
        GC.SuppressFinalize(this);
    }
}
