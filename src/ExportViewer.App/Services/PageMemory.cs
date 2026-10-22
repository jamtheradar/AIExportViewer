namespace ExportViewer.App.Services;

/// <summary>
/// Where the reviewer was on each list (page, tab, filters), so going back to it lands in the same place. Held in
/// memory for this session only and forgotten whenever an export is loaded again.
/// </summary>
public sealed class PageMemory
{
    private readonly Dictionary<string, (int Version, object Place)> _places = new(StringComparer.Ordinal);

    public void Remember(string key, int libraryVersion, object place) => _places[key] = (libraryVersion, place);

    public T? Recall<T>(string key, int libraryVersion) where T : class =>
        _places.TryGetValue(key, out var p) && p.Version == libraryVersion ? p.Place as T : null;
}
