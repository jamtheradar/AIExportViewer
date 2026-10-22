namespace ExportViewer.Core.Conversations;

public sealed record TreeNode(string Uuid, string? ParentUuid, string? CreatedAt, int Seq);

/// <summary>
/// The message tree built from <c>parent_message_uuid</c>. The export does not say which branch was current,
/// so the default branch is the path from the root to the newest leaf.
/// </summary>
public sealed class MessageTree
{
    public const string RootSentinel = "00000000-0000-4000-8000-000000000000";

    private readonly Dictionary<string, TreeNode> _nodes;
    private readonly Dictionary<string, List<TreeNode>> _children;
    private readonly Dictionary<string, TreeNode> _newestLeafUnder = new();
    private readonly List<TreeNode> _roots;

    /// <summary>Messages whose parent ID points at a message that is not in the export.</summary>
    public IReadOnlyList<string> OrphanUuids { get; }

    public int ForkCount { get; }

    public int Count => _nodes.Count;

    private MessageTree(IEnumerable<TreeNode> nodes)
    {
        _nodes = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
        foreach (var n in nodes) _nodes.TryAdd(n.Uuid, n);

        _children = new Dictionary<string, List<TreeNode>>(StringComparer.Ordinal);
        _roots = [];
        var orphans = new List<string>();
        foreach (var n in _nodes.Values)
        {
            var parent = n.ParentUuid;
            var isRoot = string.IsNullOrEmpty(parent) || parent == RootSentinel || parent == n.Uuid;
            if (!isRoot && !_nodes.ContainsKey(parent!))
            {
                orphans.Add(n.Uuid);
                isRoot = true;
            }
            if (isRoot) _roots.Add(n);
            else
            {
                if (!_children.TryGetValue(parent!, out var list)) _children[parent!] = list = [];
                list.Add(n);
            }
        }

        // A parent loop leaves nodes unreachable from any root; attach them to the root so nothing is hidden.
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<TreeNode>(_roots);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!reachable.Add(n.Uuid)) continue;
            foreach (var c in ChildrenOf(n.Uuid)) stack.Push(c);
        }
        foreach (var n in _nodes.Values.Where(n => !reachable.Contains(n.Uuid)).OrderBy(n => n.Seq).ToList())
        {
            if (reachable.Contains(n.Uuid)) continue;
            if (n.ParentUuid is { } p && _children.TryGetValue(p, out var siblings)) siblings.Remove(n);
            _roots.Add(n);
            orphans.Add(n.Uuid);
            MarkReachable(n, reachable);
        }

        _roots.Sort(Compare);
        foreach (var list in _children.Values) list.Sort(Compare);
        OrphanUuids = orphans;
        ForkCount = (_roots.Count > 1 ? 1 : 0) + _children.Values.Count(l => l.Count > 1);
        foreach (var r in _roots) ComputeNewestLeaf(r);
    }

    public static MessageTree Build(IEnumerable<TreeNode> nodes) => new(nodes);

    public IReadOnlyList<TreeNode> Roots => _roots;

    public IReadOnlyList<TreeNode> ChildrenOf(string uuid) =>
        _children.TryGetValue(uuid, out var list) ? list : [];

    /// <summary>Siblings including the node itself, oldest first.</summary>
    public IReadOnlyList<TreeNode> SiblingsOf(string uuid)
    {
        var n = _nodes[uuid];
        if (_roots.Contains(n)) return _roots;
        return ChildrenOf(n.ParentUuid!);
    }

    public TreeNode? NewestLeaf => _roots.Count == 0 ? null : _roots.Select(r => _newestLeafUnder[r.Uuid]).MaxBy(n => n, Comparer<TreeNode>.Create(Compare));

    /// <summary>The default branch: root to newest leaf.</summary>
    public IReadOnlyList<TreeNode> LatestPath() => Path(new Dictionary<string, string>());

    /// <summary>
    /// A branch through the tree. <paramref name="choices"/> maps a parent uuid (or <see cref="RootSentinel"/>) to the child
    /// to follow; anywhere without a choice follows the child whose subtree holds the newest leaf.
    /// </summary>
    public IReadOnlyList<TreeNode> Path(IReadOnlyDictionary<string, string> choices)
    {
        var path = new List<TreeNode>();
        var options = (IReadOnlyList<TreeNode>)_roots;
        var key = RootSentinel;
        var guard = new HashSet<string>(StringComparer.Ordinal);
        while (options.Count > 0)
        {
            TreeNode next;
            if (choices.TryGetValue(key, out var chosen) && options.FirstOrDefault(o => o.Uuid == chosen) is { } c) next = c;
            else next = options.MaxBy(o => _newestLeafUnder[o.Uuid], Comparer<TreeNode>.Create(Compare))!;
            if (!guard.Add(next.Uuid)) break;
            path.Add(next);
            key = next.Uuid;
            options = ChildrenOf(next.Uuid);
        }
        return path;
    }

    private void MarkReachable(TreeNode n, HashSet<string> reachable)
    {
        var stack = new Stack<TreeNode>([n]);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            if (!reachable.Add(x.Uuid)) continue;
            foreach (var c in ChildrenOf(x.Uuid)) stack.Push(c);
        }
    }

    private void ComputeNewestLeaf(TreeNode root)
    {
        // Iterative post-order so very long conversations cannot overflow the stack.
        var stack = new Stack<(TreeNode Node, bool Expanded)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            var (n, expanded) = stack.Pop();
            if (_newestLeafUnder.ContainsKey(n.Uuid)) continue;
            var kids = ChildrenOf(n.Uuid);
            if (kids.Count == 0) { _newestLeafUnder[n.Uuid] = n; continue; }
            if (!expanded)
            {
                stack.Push((n, true));
                foreach (var k in kids) if (!_newestLeafUnder.ContainsKey(k.Uuid)) stack.Push((k, false));
                continue;
            }
            var best = kids.Where(k => _newestLeafUnder.ContainsKey(k.Uuid)).Select(k => _newestLeafUnder[k.Uuid])
                .DefaultIfEmpty(n).MaxBy(x => x, Comparer<TreeNode>.Create(Compare))!;
            _newestLeafUnder[n.Uuid] = best;
        }
    }

    private static int Compare(TreeNode a, TreeNode b)
    {
        var c = string.CompareOrdinal(a.CreatedAt, b.CreatedAt);
        return c != 0 ? c : a.Seq.CompareTo(b.Seq);
    }
}
