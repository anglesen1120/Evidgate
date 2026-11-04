using System.Text.Json;

namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>Wire shape of a suite (03.2.1 §5).</summary>
/// <param name="Id">Suite id (UUIDv7).</param>
/// <param name="OrgId">Owning organization.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Name">Display name.</param>
/// <param name="Slug">Slug unique inside the project.</param>
/// <param name="Status"><c>active</c> or <c>archived</c>.</param>
/// <param name="Platform">The platform of the head revision: <c>web</c>, <c>api</c>, <c>unit</c> or <c>mobile</c> (03.1.4).</param>
/// <param name="Level">Its level: <c>unit</c>, <c>integration</c>, <c>e2e</c> or <c>smoke</c>.</param>
/// <param name="Purpose">Its purpose: <c>regression</c>, <c>smoke</c>, <c>release_gate</c> or <c>exploratory</c>.</param>
/// <param name="HeadRevisionId">The newest revision, or null before the first.</param>
/// <param name="HeadRevisionNo">Its number, or null.</param>
/// <param name="CreatedAt">Creation, UTC.</param>
/// <param name="UpdatedAt">Last write, UTC.</param>
/// <param name="Version">Optimistic-concurrency token of the suite row.</param>
public sealed record SuiteDto(
    Guid Id,
    Guid OrgId,
    Guid ProjectId,
    string Name,
    string Slug,
    string Status,
    string Platform,
    string Level,
    string Purpose,
    Guid? HeadRevisionId,
    int? HeadRevisionNo,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version);

/// <summary>Cursor page of <c>GET /v1/projects/{p}/suites</c>.</summary>
/// <param name="Items">Suites in creation order.</param>
/// <param name="NextCursor">Opaque cursor, or null on the last page.</param>
public sealed record SuiteListResponse(IReadOnlyList<SuiteDto> Items, string? NextCursor);

/// <summary>Body of <c>POST /v1/projects/{p}/suites</c>.</summary>
/// <param name="Name">Display name, 1–128 characters.</param>
/// <param name="Slug">Slug, D-A1 grammar, unique in the project.</param>
/// <param name="Platform">Taxonomy of the suite (03.1.4); default <c>web</c>.</param>
/// <param name="Level">Level of the suite; default follows the platform (<c>api</c> → <c>integration</c>, <c>unit</c> → <c>unit</c>, otherwise <c>e2e</c>).</param>
/// <param name="Purpose">Purpose of the suite; default <c>regression</c>.</param>
public sealed record CreateSuiteRequest(string Name, string Slug, string? Platform = null, string? Level = null, string? Purpose = null);

/// <summary>One member of a suite revision as authored: a case revision under a variant.</summary>
/// <param name="Ref">A name unique inside the request that <see cref="SuiteEdgeInput"/> uses; defaults to <c>m&lt;index&gt;</c>.</param>
/// <param name="CaseRevisionId">A <c>test_revision</c> of this project.</param>
/// <param name="Params">The variant parameters (flat, secret-free); null or absent means no parameters.</param>
/// <param name="Required">Whether the member must pass for the suite to pass; default true.</param>
public sealed record SuiteMemberInput(string? Ref, Guid CaseRevisionId, JsonElement? Params, bool? Required);

/// <summary>A dependency: <paramref name="Child"/> runs only after <paramref name="Parent"/> passed.</summary>
/// <param name="Parent">The <c>ref</c> of the parent member.</param>
/// <param name="Child">The <c>ref</c> of the child member.</param>
public sealed record SuiteEdgeInput(string Parent, string Child);

/// <summary>Body of <c>POST /v1/suites/{s}/revisions</c> (03.2.1 §5).</summary>
/// <param name="Members">The members; an empty list is a legal, empty revision.</param>
/// <param name="Edges">Dependencies between members.</param>
/// <param name="ShardStrategy">See <c>contracts/catalog/shard-strategy.v1.json</c>; default <c>{"kind":"none"}</c>.</param>
/// <param name="RetryPolicy">See <c>contracts/catalog/retry-policy.v1.json</c>; default <c>{"max_retries":0}</c>.</param>
/// <param name="EnvironmentRequirements">See <c>contracts/catalog/environment-requirements.v1.json</c>; default <c>{}</c>.</param>
/// <param name="Platform">Taxonomy this revision is authored under; default the suite's current platform.</param>
/// <param name="Level">Its level; default the suite's current level, or the platform's default level when the request names a platform.</param>
/// <param name="Purpose">Its purpose; default the suite's current purpose.</param>
public sealed record CreateSuiteRevisionRequest(
    IReadOnlyList<SuiteMemberInput>? Members,
    IReadOnlyList<SuiteEdgeInput>? Edges,
    JsonElement? ShardStrategy,
    JsonElement? RetryPolicy,
    JsonElement? EnvironmentRequirements,
    string? Platform = null,
    string? Level = null,
    string? Purpose = null);

/// <summary>Result of creating (or previewing) a suite revision.</summary>
/// <param name="SuiteRevisionId">The revision; null in a preview of content that does not exist yet.</param>
/// <param name="RevisionNo">Its number (the one it would get, in a preview).</param>
/// <param name="RevisionHash"><c>sha256:</c> identity of the content.</param>
/// <param name="Status"><c>draft</c> or <c>published</c>; <c>preview</c> for a preview of new content.</param>
/// <param name="Created">True only when this call inserted the revision; identical content returns the existing one.</param>
/// <param name="Warnings">Non-blocking observations (an empty revision, …).</param>
/// <param name="HeadMoved">True when the suite's head now points at this revision because of this call (a new revision, or a revert to older content).</param>
/// <param name="Platform">The platform stored with the revision (03.1.4).</param>
/// <param name="Level">Its level.</param>
/// <param name="Purpose">Its purpose.</param>
public sealed record SuiteRevisionHead(
    Guid? SuiteRevisionId,
    int RevisionNo,
    string RevisionHash,
    string Status,
    bool Created,
    IReadOnlyList<string> Warnings,
    bool HeadMoved = false,
    string Platform = "web",
    string Level = "e2e",
    string Purpose = "regression");

/// <summary>A member of a stored revision.</summary>
/// <param name="Id">Member id (the edge endpoints).</param>
/// <param name="CaseRevisionId">The pinned case revision.</param>
/// <param name="VariantHash">Identity of its variant.</param>
/// <param name="Params">The normalised variant parameters.</param>
/// <param name="Required">Whether it must pass.</param>
/// <param name="Ordinal">Position in the canonical member order.</param>
public sealed record SuiteMemberDto(Guid Id, Guid CaseRevisionId, string VariantHash, JsonElement Params, bool Required, int Ordinal);

/// <summary>A stored dependency edge.</summary>
/// <param name="ParentMemberId">The parent member.</param>
/// <param name="ChildMemberId">The child member.</param>
public sealed record SuiteEdgeDto(Guid ParentMemberId, Guid ChildMemberId);

/// <summary>The full pinned content of a suite revision (<c>GET /v1/suite-revisions/{r}</c>).</summary>
/// <param name="Id">Revision id.</param>
/// <param name="SuiteId">The suite.</param>
/// <param name="RevisionNo">Its number.</param>
/// <param name="RevisionHash">Its content identity.</param>
/// <param name="Status"><c>draft</c> or <c>published</c>.</param>
/// <param name="Platform">The platform this revision was authored under (03.1.4).</param>
/// <param name="Level">Its level.</param>
/// <param name="Purpose">Its purpose.</param>
/// <param name="ShardStrategy">The shard strategy.</param>
/// <param name="RetryPolicy">The retry policy.</param>
/// <param name="EnvironmentRequirements">The secret-free environment requirements.</param>
/// <param name="CreatedAt">Creation, UTC.</param>
/// <param name="Members">The members in canonical order.</param>
/// <param name="Edges">The dependency edges.</param>
public sealed record SuiteRevisionDto(
    Guid Id,
    Guid SuiteId,
    int RevisionNo,
    string RevisionHash,
    string Status,
    string Platform,
    string Level,
    string Purpose,
    JsonElement ShardStrategy,
    JsonElement RetryPolicy,
    JsonElement EnvironmentRequirements,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SuiteMemberDto> Members,
    IReadOnlyList<SuiteEdgeDto> Edges);

/// <summary>A member key in a diff: the pinned case revision under one variant.</summary>
/// <param name="CaseRevisionId">The case revision.</param>
/// <param name="VariantHash">The variant identity.</param>
public sealed record SuiteMemberKey(Guid CaseRevisionId, string VariantHash);

/// <summary>A dependency edge in a diff, by member key.</summary>
/// <param name="Parent">Parent member.</param>
/// <param name="Child">Child member.</param>
public sealed record SuiteEdgeKey(SuiteMemberKey Parent, SuiteMemberKey Child);

/// <summary>A before/after pair of one strategy member.</summary>
/// <param name="From">Value in the base revision.</param>
/// <param name="To">Value in the compared revision.</param>
public sealed record SuiteDelta(JsonElement From, JsonElement To);

/// <summary>
/// <c>GET /v1/suite-revisions/{r}/diff?against={other}</c> (03.2.1 AC7): what changed from <c>against</c> to <c>r</c>.
/// </summary>
/// <param name="RevisionId">The revision being described (the newer one).</param>
/// <param name="AgainstId">The revision it is compared with.</param>
/// <param name="MembersAdded">Members only in <c>r</c>.</param>
/// <param name="MembersRemoved">Members only in <c>against</c>.</param>
/// <param name="RequiredChanged">Members in both whose <c>required</c> flag differs (keyed, with the new value).</param>
/// <param name="EdgesAdded">Edges only in <c>r</c>.</param>
/// <param name="EdgesRemoved">Edges only in <c>against</c>.</param>
/// <param name="ShardStrategy">Set when the strategy changed.</param>
/// <param name="RetryPolicy">Set when the policy changed.</param>
/// <param name="EnvironmentRequirements">Set when the requirements changed.</param>
/// <param name="Taxonomy">Set when <c>platform</c>, <c>level</c> or <c>purpose</c> changed, as <c>{platform, level, purpose}</c>.</param>
public sealed record SuiteRevisionDiff(
    Guid RevisionId,
    Guid AgainstId,
    IReadOnlyList<SuiteMemberKey> MembersAdded,
    IReadOnlyList<SuiteMemberKey> MembersRemoved,
    IReadOnlyList<SuiteMemberKey> RequiredChanged,
    IReadOnlyList<SuiteEdgeKey> EdgesAdded,
    IReadOnlyList<SuiteEdgeKey> EdgesRemoved,
    SuiteDelta? ShardStrategy,
    SuiteDelta? RetryPolicy,
    SuiteDelta? EnvironmentRequirements,
    SuiteDelta? Taxonomy);
