using System.Globalization;

namespace Evidgate.Modules.Authoring.Application.Import;

internal sealed partial class AuthoringExporter
{
    /// <summary>
    /// One export's row assembly: the tree walk, the key numbering and the field mapping, with no database work, so the
    /// order the class documents is the order one small state machine produces.
    /// </summary>
    private sealed class RowAssembly
    {
        private readonly IReadOnlyList<ExportSql.NodeRow> _nodes;
        private readonly IReadOnlyList<ExportSql.ItemRow> _items;
        private readonly IReadOnlyList<ExportSql.CaseRow> _cases;
        private readonly bool _wantsNodes;
        private readonly bool _wantsItems;
        private readonly bool _wantsCases;

        private readonly List<ImportRow> _rows;
        private readonly Dictionary<Guid, string> _nodeKeys;
        private readonly Dictionary<Guid, List<ExportSql.NodeRow>> _children = [];
        private readonly Dictionary<Guid, List<ExportSql.ItemRow>> _itemsByNode = [];
        private readonly Dictionary<Guid, List<ExportSql.CaseRow>> _casesByItem = [];
        private readonly Dictionary<Guid, ExportSql.ItemRow> _itemsById = [];
        private readonly List<ExportSql.NodeRow> _roots = [];

        private int _index;
        private int _nodeNumber;
        private int _itemNumber;
        private int _caseNumber;
        private long _textChars;

        /// <summary>One assembly over the three read sets.</summary>
        /// <param name="nodes">The live nodes in scope, in <c>(position, id)</c> order.</param>
        /// <param name="items">The live items in scope, in <c>(position, id)</c> order.</param>
        /// <param name="cases">The live cases in scope, in <c>(code, id)</c> order.</param>
        /// <param name="include">The kinds to emit.</param>
        /// <param name="capacity">The number of rows the run will hold, already checked against the cap.</param>
        internal RowAssembly(
            IReadOnlyList<ExportSql.NodeRow> nodes,
            IReadOnlyList<ExportSql.ItemRow> items,
            IReadOnlyList<ExportSql.CaseRow> cases,
            ImportInclude include,
            int capacity)
        {
            _nodes = nodes;
            _items = items;
            _cases = cases;
            _wantsNodes = (include & ImportInclude.Nodes) != 0;
            _wantsItems = (include & ImportInclude.Items) != 0;
            _wantsCases = (include & ImportInclude.Cases) != 0;
            _rows = new List<ImportRow>(capacity);
            _nodeKeys = new Dictionary<Guid, string>(nodes.Count);
        }

        /// <summary>
        /// True when the walk stopped because the file's text passed <see cref="ImportLimits.MaxBodyBytes"/>: the rows
        /// collected so far are then a prefix and the caller answers 413 instead of serving a file no import accepts.
        /// </summary>
        internal bool OverBudget { get; private set; }

        /// <summary>Walks the tree and returns the rows in the documented emission order.</summary>
        internal List<ImportRow> Build()
        {
            var inScope = new HashSet<Guid>(_nodes.Count);
            foreach (var node in _nodes)
            {
                inScope.Add(node.Id);
            }

            foreach (var node in _nodes)
            {
                if (node.ParentId is { } parentId && inScope.Contains(parentId))
                {
                    Group(_children, parentId).Add(node);
                }
                else
                {
                    // A parent outside the export — the subtree root, or a parent that is a tombstone — makes the node a
                    // root of the file, so its row carries no parent_key and the file re-imports.
                    _roots.Add(node);
                }
            }

            foreach (var item in _items)
            {
                _itemsById[item.Id] = item;

                // Only an item of a node that is part of this export can carry a parent key.
                if (inScope.Contains(item.FeatureNodeId))
                {
                    Group(_itemsByNode, item.FeatureNodeId).Add(item);
                }
            }

            foreach (var testCase in _cases)
            {
                if (_itemsById.TryGetValue(testCase.ChecklistItemId, out var item) && inScope.Contains(item.FeatureNodeId))
                {
                    Group(_casesByItem, testCase.ChecklistItemId).Add(testCase);
                }
            }

            // Depth-first pre-order with an explicit stack: the ladder bounds a tree the API built at four levels, but a
            // walk that cannot recurse cannot overflow on one a manual INSERT nested deeper.
            var stack = new Stack<ExportSql.NodeRow>(_roots.Count);
            for (var index = _roots.Count - 1; index >= 0; index--)
            {
                stack.Push(_roots[index]);
            }

            Drain(stack);

            // A node whose parent chain loops (only a manual INSERT can build one: every API path refuses a parent that
            // is not a lower level) is reachable from no root, and dropping it would silently truncate the file. It is
            // emitted as one more root instead, in its own (position, id) order, so every live row of the scope lands in
            // the file whatever the table holds.
            foreach (var node in _nodes)
            {
                if (OverBudget)
                {
                    break;
                }

                if (_nodeKeys.ContainsKey(node.Id))
                {
                    continue;
                }

                stack.Push(node);
                Drain(stack);
            }

            return _rows;
        }

        /// <summary>Walks a stack of nodes to exhaustion, each node's block in pre-order.</summary>
        /// <param name="stack">The nodes to visit; children are pushed as their parent is visited.</param>
        private void Drain(Stack<ExportSql.NodeRow> stack)
        {
            while (stack.Count > 0 && !OverBudget)
            {
                var node = stack.Pop();

                // A node is visited once: the key map is the visited set, which also keeps a looped parent chain from
                // walking for ever (it is emitted as its own root, never twice).
                if (!_nodeKeys.ContainsKey(node.Id))
                {
                    Visit(node);
                }

                if (_children.TryGetValue(node.Id, out var children))
                {
                    for (var index = children.Count - 1; index >= 0; index--)
                    {
                        stack.Push(children[index]);
                    }
                }
            }
        }

        /// <summary>
        /// Records one row and charges its text against the export's body budget: a file carries at most
        /// <see cref="ImportLimits.MaxBodyBytes"/> of text — the import's own cap — so every export this class answers
        /// with is one the import accepts (AC14). The walk stops at the first row past it instead of building the rest
        /// of a file that would be refused.
        /// </summary>
        /// <param name="row">The row to record.</param>
        private void Emit(ImportRow row)
        {
            if (OverBudget)
            {
                return;
            }

            _textChars += TextChars(row);

            if (_textChars > ImportLimits.MaxBodyBytes)
            {
                OverBudget = true;

                return;
            }

            _rows.Add(row);
        }

        /// <summary>The characters of text one row puts in the file: every string member, keys included.</summary>
        /// <param name="row">The row.</param>
        private static long TextChars(ImportRow row) =>
            (long)(row.Key.Length
                + (row.ParentKey?.Length ?? 0)
                + (row.ItemKey?.Length ?? 0)
                + (row.NodeKind?.Length ?? 0)
                + (row.Title?.Length ?? 0)
                + (row.Description?.Length ?? 0)
                + (row.Precondition?.Length ?? 0)
                + (row.StepsJson?.Length ?? 0)
                + (row.Priority?.Length ?? 0)
                + (row.State?.Length ?? 0)
                + (row.Code?.Length ?? 0));

        /// <summary>Emits one node's block: the node row, its items with their cases, then nothing (children follow it).</summary>
        /// <param name="node">The node being visited.</param>
        private void Visit(ExportSql.NodeRow node)
        {
            _nodeNumber++;
            var key = string.Create(CultureInfo.InvariantCulture, $"n{_nodeNumber}");
            _nodeKeys[node.Id] = key;

            if (_wantsNodes)
            {
                // A child is always visited after its parent in pre-order, so a parent inside the export has its key; a
                // root has none, which is what "no parent_key" means in the file.
                var parentKey = node.ParentId is { } parentId && _nodeKeys.TryGetValue(parentId, out var known)
                    ? known
                    : null;

                Emit(new ImportRow(
                    ++_index,
                    ImportRowKind.Node,
                    key,
                    parentKey,
                    null,
                    node.Kind,
                    node.Title,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    node.Position));
            }

            if (!_itemsByNode.TryGetValue(node.Id, out var items))
            {
                return;
            }

            foreach (var item in items)
            {
                if (OverBudget)
                {
                    return;
                }

                _itemNumber++;
                var itemKey = string.Create(CultureInfo.InvariantCulture, $"i{_itemNumber}");

                if (_wantsItems)
                {
                    Emit(new ImportRow(
                        ++_index,
                        ImportRowKind.Item,
                        itemKey,
                        key,
                        null,
                        null,
                        item.Title,
                        item.Description,
                        null,
                        null,
                        item.Priority,
                        item.State,
                        null,
                        item.Position));
                }

                if (!_casesByItem.TryGetValue(item.Id, out var cases))
                {
                    continue;
                }

                foreach (var testCase in cases)
                {
                    if (OverBudget)
                    {
                        return;
                    }

                    _caseNumber++;

                    if (_wantsCases)
                    {
                        Emit(new ImportRow(
                            ++_index,
                            ImportRowKind.Case,
                            string.Create(CultureInfo.InvariantCulture, $"c{_caseNumber}"),
                            null,
                            itemKey,
                            null,
                            testCase.Title,
                            testCase.Description,
                            testCase.Precondition,
                            CompactJson(testCase.Steps),
                            testCase.Priority,
                            testCase.State,
                            testCase.Code,
                            null));
                    }
                }
            }
        }

        /// <summary>The list one owner's rows are collected into, created on first use (the read sets arrive ordered, so a
        /// list preserves the order the statement sorted by).</summary>
        /// <typeparam name="TRow">The row type.</typeparam>
        /// <param name="groups">The groups built so far.</param>
        /// <param name="ownerId">The owning row's id.</param>
        private static List<TRow> Group<TRow>(Dictionary<Guid, List<TRow>> groups, Guid ownerId)
        {
            if (!groups.TryGetValue(ownerId, out var rows))
            {
                rows = [];
                groups[ownerId] = rows;
            }

            return rows;
        }
    }
}
