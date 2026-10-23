namespace Evidgate.Modules.Authoring.Contracts;

/// <summary>
/// The three versioned entities of the authoring tree (13.1.1 §4): a feature node, a checklist item and a manual
/// test case. Each one is soft-deleted, never physically deleted by the app role, and every change of one appends an
/// <c>authoring_version</c> row through <see cref="IAuthoringVersionWriter"/>.
/// </summary>
public enum AuthoringEntityKind
{
    /// <summary><c>public.feature_node</c>: module → feature → category → sub-category (13.1.1 AC1).</summary>
    FeatureNode,

    /// <summary><c>public.checklist_item</c>: a prioritised requirement under a category or sub-category (13.1.1 AC3).</summary>
    ChecklistItem,

    /// <summary><c>public.manual_test_case</c>: the steps of a manual test, with a project-prefixed code (13.1.1 AC4).</summary>
    ManualTestCase,
}

/// <summary>The three changes a caller can ask for; the writer owns what each one writes (13.1.1 §5).</summary>
public enum AuthoringChangeKind
{
    /// <summary>A new entity row plus <c>change_kind = create</c>, <c>version_no = 1</c>.</summary>
    Create,

    /// <summary>A new <c>content</c> snapshot of a live entity, at the entity's next version.</summary>
    Update,

    /// <summary>A soft delete: <c>deleted_at</c> set, one <c>change_kind = delete</c> row, after the non-empty guard.</summary>
    SoftDelete,
}

/// <summary>
/// Where a version came from (13.1.1 AC5): a person in the app, the generation pipeline of 13.3.1, or an import.
/// The value is stored on the version row and, for a create, on the entity row's own <c>source</c> column.
/// </summary>
public enum AuthoringSource
{
    /// <summary>An author edited or created the entity in the app.</summary>
    Manual,

    /// <summary>13.3.1's generation pipeline; carries the <c>ai_run_id</c> of the run.</summary>
    Ai,

    /// <summary>An import of 13.1.1 route 25.</summary>
    Import,
}

/// <summary>
/// The database spelling of the contract enums, in one place: the two enum values and the text the schema, the
/// timeline and 13.1.2 use must never drift apart.
/// </summary>
public static class AuthoringWireValues
{
    /// <summary><c>authoring_version.change_kind = create</c>.</summary>
    public const string CreateChangeKind = "create";

    /// <summary><c>authoring_version.change_kind = update</c>.</summary>
    public const string UpdateChangeKind = "update";

    /// <summary><c>authoring_version.change_kind = delete</c>.</summary>
    public const string DeleteChangeKind = "delete";

    /// <summary><c>authoring_version.change_kind = restore</c>, written by the writer's restore member only.</summary>
    public const string RestoreChangeKind = "restore";

    /// <summary><c>authoring_version.source = manual</c>.</summary>
    public const string ManualSource = "manual";

    /// <summary><c>authoring_version.source = ai</c>.</summary>
    public const string AiSource = "ai";

    /// <summary><c>authoring_version.source = import</c>, also the entity rows' <c>source</c> of an imported row.</summary>
    public const string ImportSource = "import";

    /// <summary>The <c>entity_kind</c> of a feature node (13.1.1 §4).</summary>
    public static string EntityKindName(AuthoringEntityKind kind) => kind switch
    {
        AuthoringEntityKind.FeatureNode => "feature_node",
        AuthoringEntityKind.ChecklistItem => "checklist_item",
        AuthoringEntityKind.ManualTestCase => "manual_test_case",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
    };

    /// <summary>The <c>change_kind</c> of the three kinds a caller can request.</summary>
    public static string ChangeKindName(AuthoringChangeKind kind) => kind switch
    {
        AuthoringChangeKind.Create => CreateChangeKind,
        AuthoringChangeKind.Update => UpdateChangeKind,
        AuthoringChangeKind.SoftDelete => DeleteChangeKind,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring change kind"),
    };

    /// <summary>The <c>source</c> text of a version.</summary>
    public static string SourceName(AuthoringSource source) => source switch
    {
        AuthoringSource.Manual => ManualSource,
        AuthoringSource.Ai => AiSource,
        AuthoringSource.Import => ImportSource,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "unknown authoring source"),
    };
}
