using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application.Import;

/// <summary>Which kinds of row an export carries (13.1.1 §5 route 25: <c>include=nodes,items,cases</c>, default all three).</summary>
[Flags]
internal enum ImportInclude
{
    /// <summary>No kind: the export carries no row (a project probe and, with <c>node_id</c>, a node probe still run).</summary>
    None = 0,

    /// <summary><c>feature_node</c> rows.</summary>
    Nodes = 1,

    /// <summary><c>checklist_item</c> rows.</summary>
    Items = 2,

    /// <summary><c>manual_test_case</c> rows.</summary>
    Cases = 4,

    /// <summary>All three kinds — the route's default.</summary>
    All = Nodes | Items | Cases,
}

/// <summary>
/// The read half of 13.1.1's import/export route (AC14): one project's feature tree, its checklist items and its manual
/// test cases as the flat <see cref="ImportRow"/> table that the CSV, XLSX and JSON writers serialise — the same table the
/// three readers parse, so a file this class produced imports back into an empty project as the same tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Row order (the contract of the file).</b> Rows are emitted in <b>depth-first pre-order over the nodes</b>: a node
/// row, then that node's item rows in <c>(position, id)</c> order, each item row immediately followed by that item's case
/// rows in <c>(code, id)</c> order, and only then the node's child nodes in <c>(position, id)</c> order. Roots are the
/// nodes whose parent is not part of the export, in <c>(position, id)</c> order; a node no root reaches (a parent chain
/// that loops, which only a manual <c>INSERT</c> can build) is emitted after them as one more root rather than dropped.
/// <see cref="ImportRow.Index"/> is the 1-based position of the row in exactly that emission order.
/// </para>
/// <para>
/// <b>Keys.</b> Keys are the file-local identity the import resolves its references by, so they must be stable for an
/// unchanged tree and must not depend on the <c>include</c> filter: <c>n1, n2, …</c> for nodes, <c>i1, i2, …</c> for items
/// and <c>c1, c2, …</c> for cases, each numbered over <em>every</em> row of its kind in the walk above — a filtered export
/// therefore carries the same keys as the full one, minus the rows of the kinds it omits. Nothing else about the tree is
/// numbered: no id, no timestamp and no counter taken from the database enters a row, so two exports of an unchanged tree
/// are the same bytes.
/// </para>
/// <para>
/// <b>Fields per kind</b> (the whole <see cref="ImportRow"/> carries them; the ones a kind does not have stay null):
/// node — <c>node_kind</c>, <c>title</c>, <c>position</c> and <c>parent_key</c> (null for a root, and for the root of a
/// <c>node_id</c> subtree, whose parent is not in the file); item — <c>parent_key</c> (the owning node's key),
/// <c>title</c>, <c>description</c>, <c>priority</c>, <c>state</c>, <c>position</c>; case — <c>item_key</c>,
/// <c>title</c>, <c>description</c>, <c>precondition</c>, <c>steps</c> (the stored <c>authoring-steps.v1</c> document as
/// compact JSON text), <c>priority</c>, <c>state</c>, <c>code</c>.
/// </para>
/// <para>
/// <b>Text.</b> Every string is read straight from the database, which stores NFC (<c>AuthoringText.Normalize</c> on
/// every write: 13.1.1 AC14), so the exporter neither re-normalises nor repairs: one spelling is stored, and re-normalising
/// on the way out would only hide a row that broke that rule. The only text the exporter transforms is the <c>steps</c>
/// document, which <c>jsonb</c> renders with its own whitespace and which is re-serialised compactly so JSON, CSV and XLSX
/// carry one spelling of it.
/// </para>
/// <para>
/// <b>Scope, liveness and the row cap.</b> One <b>read-only</b> transaction on the caller's tenant scope (C1/C5), and a
/// constant number of statements: the project probe, one probe for <c>node_id</c> when the export is scoped to a subtree,
/// and one statement per kind that is actually read (nodes are read whenever any row is emitted, because the node walk is
/// what numbers and orders everything else; items are read for a case export because a case row carries its item's key).
/// A whole-project export of all three kinds spends four statements, the same one of a <c>node_id</c> subtree five, and
/// neither grows with the number of rows (P3). Only live rows are exported (<c>deleted_at is null</c>, AC7/AC8); a node or
/// item outside the scope takes its descendants with it, and an item whose node is a tombstone — reachable only by an
/// <c>UPDATE</c> outside the API, which AC7 refuses — is not exported, because it has no parent key to carry.
/// </para>
/// <para>
/// <b>Failures.</b> A project the tenant cannot see is the tree route's own <c>404</c>
/// (<see cref="AuthoringRules.ProjectNotFound"/>); an unknown, soft-deleted or other-project <c>node_id</c> is
/// <c>404 not_found</c> for a feature node (AC10's indistinguishability). A scope that would <em>emit</em> more than
/// <see cref="ImportLimits.MaxDataRows"/> rows is <c>413 payload_too_large</c> with
/// <c>details[].path = /node_id</c>, never a truncated file: the import cap and the export cap are one number, so every
/// export this class answers with re-imports, and the refusal names the cap without echoing a row. The cap is on the rows
/// of <em>this answer</em>: a kind the caller filtered out (<c>include=cases</c>) is never counted against it, and the
/// larger walk it needs for its keys is bounded separately (<see cref="MaxWalkedRows"/>, refused with the same 413)
/// instead of silently dropping rows. Two bounds keep the answer re-importable in bytes as well as in rows: the text of
/// the rows is refused once it passes <see cref="ImportLimits.MaxBodyBytes"/> (the import body cap, checked while the
/// table is assembled, so a huge file is never built), and the serialised file is checked again by the caller for the
/// formats whose bytes exceed their text. Every read is bounded by its cap plus one, so each refusal is reached without
/// materialising an oversized scope.
/// </para>
/// </remarks>
/// <param name="db">The Dapper path over the caller's open tenant scope (the export runs in the caller's read-only transaction).</param>
internal sealed class AuthoringExporter(ITenantDb db)
{
    /// <summary>
    /// The nodes (and, for a <c>cases</c>-only export, the items) a filtered export has to walk to number its keys. A
    /// filtered export emits the rows of the kinds it names, but a key is numbered over <em>every</em> row of its kind in
    /// the full tree walk, so the walk of a kind that is not emitted still has to see the whole scope: the row cap is a
    /// cap on <em>emitted</em> rows, and this is the separate, much larger bound on the walk itself.
    /// </summary>
    private const int MaxWalkedRows = 100_000;

    /// <summary>
    /// The 413 of an export scope larger than the import cap (<c>details[].path = /node_id</c>, reason
    /// <c>body_too_large</c> — the shared size token of <see cref="ContractValidationError.BodyTooLargeReason"/>, never the
    /// error code repeated as a reason).
    /// </summary>
    private static Error TooLarge => new(
        ApiErrorCode.PayloadTooLarge,
        string.Create(CultureInfo.InvariantCulture, $"an export carries at most {ImportLimits.MaxDataRows} rows; scope it with node_id"),
        413,
        Retryable: false,
        Details: [new ErrorDetail("/node_id", ContractValidationError.BodyTooLargeReason)]);

    /// <summary>
    /// The 413 <see cref="ExportQueries"/> answers when the file itself would pass the import body cap: an export that no
    /// import could accept is not a file (AC14 requires every export to re-import), so it is refused with the same
    /// pointer and reason as the row cap rather than served as a download the consumer cannot load.
    /// </summary>
    internal static Error BodyTooLarge => new(
        ApiErrorCode.PayloadTooLarge,
        string.Create(
            CultureInfo.InvariantCulture,
            $"an export carries at most {ImportLimits.MaxBodyBytes} bytes of text; scope it with node_id"),
        413,
        Retryable: false,
        Details: [new ErrorDetail("/node_id", ContractValidationError.BodyTooLargeReason)]);

    /// <summary>
    /// The 413 of a filtered export whose <em>walked</em> scope is over <see cref="MaxWalkedRows"/>: a <c>cases</c>-only
    /// export of a project with more nodes than that cannot number its keys, and a truncated walk would silently drop
    /// rows, so the scope is refused with the same way through.
    /// </summary>
    private static Error ScopeTooLarge => new(
        ApiErrorCode.PayloadTooLarge,
        string.Create(
            CultureInfo.InvariantCulture,
            $"an export walks at most {MaxWalkedRows} rows to number a filtered file; scope it with node_id"),
        413,
        Retryable: false,
        Details: [new ErrorDetail("/node_id", ContractValidationError.BodyTooLargeReason)]);

    /// <summary>
    /// The export of one project — or of one node's subtree — as the flat table of route 25 (§5 row 25).
    /// </summary>
    /// <param name="orgId">The tenant of the caller's scope.</param>
    /// <param name="projectId">The project of the URL; another tenant's or a soft-deleted project is <c>404</c>.</param>
    /// <param name="nodeId">The subtree root, or null for the whole project.</param>
    /// <param name="include">The kinds to emit; keys stay the full tree's keys either way.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The rows in emission order, or the 404/413 the route answers unchanged.</returns>
    public async ValueTask<Result<IReadOnlyList<ImportRow>>> ExportAsync(
        Guid orgId,
        Guid projectId,
        Guid? nodeId,
        ImportInclude include,
        CancellationToken cancellationToken = default)
    {
        var wantsNodes = (include & ImportInclude.Nodes) != 0;
        var wantsItems = (include & ImportInclude.Items) != 0;
        var wantsCases = (include & ImportInclude.Cases) != 0;

        // The node walk numbers every other kind and orders the file, so nodes are read whenever a row is emitted at all;
        // items are read for a case export too, because a case row carries its item's key.
        var readsNodes = wantsNodes || wantsItems || wantsCases;
        var readsItems = wantsItems || wantsCases;

        if (await db.ScalarAsync<int?>(
                AuthoringNodeReadSql.ProjectExists,
                new { org_id = orgId, project_id = projectId },
                cancellationToken).ConfigureAwait(false) is null)
        {
            return AuthoringErrors.Fail<IReadOnlyList<ImportRow>>(AuthoringRules.ProjectNotFound);
        }

        ExportSql.NodeRow? root = null;
        if (nodeId is { } addressed)
        {
            root = await db.QuerySingleOrDefaultAsync<ExportSql.NodeRow>(
                AuthoringNodeReadSql.LiveNode,
                new { org_id = orgId, project_id = projectId, id = addressed },
                cancellationToken).ConfigureAwait(false);

            if (root is null)
            {
                // A tombstone is the same 404 as an id that never existed (AC10), exactly like the tree route.
                return AuthoringErrors.Fail<IReadOnlyList<ImportRow>>(
                    AuthoringErrors.EntityNotFound(AuthoringEntityKind.FeatureNode));
            }
        }

        // The row cap is a cap on the rows of the answer, so a kind that is emitted is read one row past the cap and a
        // kind that is only walked (nodes for a filtered export's keys, items for a cases-only one) is read against the
        // much larger walk bound: `include=cases` on a project of 6 000 nodes and 100 cases answers 100 rows, and its
        // keys are still the full tree's keys because the walk saw the whole tree.
        var nodeTake = wantsNodes ? ImportLimits.MaxDataRows + 1 : MaxWalkedRows + 1;
        var itemTake = wantsItems ? ImportLimits.MaxDataRows + 1 : MaxWalkedRows + 1;

        IReadOnlyList<ExportSql.NodeRow> nodes = [];
        if (readsNodes)
        {
            nodes = await db.QueryAsync<ExportSql.NodeRow>(
                root is null ? ExportSql.Nodes : ExportSql.NodesOfSubtree,
                ExportSql.Parameters(orgId, projectId, root?.Id, nodeTake),
                cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ExportSql.ItemRow> items = [];
        if (readsItems)
        {
            items = await db.QueryAsync<ExportSql.ItemRow>(
                root is null ? ExportSql.Items : ExportSql.ItemsOfSubtree,
                ExportSql.Parameters(orgId, projectId, root?.Id, itemTake),
                cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ExportSql.CaseRow> cases = [];
        if (wantsCases)
        {
            cases = await db.QueryAsync<ExportSql.CaseRow>(
                root is null ? ExportSql.Cases : ExportSql.CasesOfSubtree,
                ExportSql.Parameters(orgId, projectId, root?.Id, ImportLimits.MaxDataRows + 1),
                cancellationToken).ConfigureAwait(false);
        }

        // A walked set that hit its own bound means the numbering walk is incomplete, and an incomplete walk would drop
        // rows of the kinds that are emitted: refuse the scope instead of answering a file with a hole in it.
        if ((!wantsNodes && nodes.Count > MaxWalkedRows) || (!wantsItems && items.Count > MaxWalkedRows))
        {
            return AuthoringErrors.Fail<IReadOnlyList<ImportRow>>(ScopeTooLarge);
        }

        var emitted = (wantsNodes ? nodes.Count : 0) + (wantsItems ? items.Count : 0) + (wantsCases ? cases.Count : 0);
        if (emitted > ImportLimits.MaxDataRows)
        {
            return AuthoringErrors.Fail<IReadOnlyList<ImportRow>>(TooLarge);
        }

        var assembly = new RowAssembly(nodes, items, cases, include, emitted);
        var rows = assembly.Build();

        // The import accepts at most ImportLimits.MaxBodyBytes, so a file of row count under the cap can still be one no
        // import could read back: refuse it here, on the text it would carry, rather than answering a download that
        // contradicts AC14's "every export re-imports".
        if (assembly.OverBudget)
        {
            return AuthoringErrors.Fail<IReadOnlyList<ImportRow>>(BodyTooLarge);
        }

        return Result.Success<IReadOnlyList<ImportRow>>(rows);
    }

    /// <summary>
    /// The compact spelling of one stored <c>steps</c> document: the <c>jsonb</c> text re-serialised through
    /// <see cref="Utf8JsonWriter"/> with no indentation, so all three formats carry one form of it. The document is
    /// written, never evaluated, and it is a stored object (<c>manual_test_case_steps_check</c>), so the parse cannot fail
    /// on caller input.
    /// </summary>
    /// <param name="stored">The stored document as JSON text.</param>
    private static string CompactJson(string stored)
    {
        using var document = JsonDocument.Parse(stored);

        var buffer = new ArrayBufferWriter<byte>(stored.Length);
        using (var writer = new Utf8JsonWriter(buffer, CompactWriterOptions))
        {
            document.RootElement.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// The writer options of the compact spelling: no indentation, and an encoder that carries the stored text as the
    /// UTF-8 it already is. The default encoder escapes every non-ASCII character as <c>\uXXXX</c>, which would put a
    /// decomposed-looking spelling in the file instead of the NFC text the database stores (13.1.1 AC14 asserts the
    /// export is NFC as <em>text</em>). The encoder still escapes the HTML-significant set (<c>&lt;</c>, <c>&gt;</c>,
    /// <c>&amp;</c>, <c>'</c>, <c>+</c>) and every control character, so the document stays safe to embed and valid JSON.
    /// </summary>
    private static readonly JsonWriterOptions CompactWriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

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
