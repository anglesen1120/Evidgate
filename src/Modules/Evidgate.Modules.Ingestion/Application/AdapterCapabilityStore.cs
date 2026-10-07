using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Ingestion.Domain;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>One registration: the row's id and whether this call wrote it.</summary>
/// <param name="Id">The capability row's id (the existing row's id when the document was already on file).</param>
/// <param name="Created">True when this call wrote the row.</param>
internal sealed record AdapterCapabilityRegistered(Guid Id, bool Created);

/// <summary>
/// The store of adapter capabilities (04.7.1-T2.2): one <c>insert … on conflict do nothing</c> per
/// registration, and at most one re-read when the natural key was already on file. Capability rows are
/// first-write facts — the app role has no <c>UPDATE</c> or <c>DELETE</c> grant (migration 0026) — so a
/// repeated identical document is a no-op that returns the stored id, and the same natural key with a
/// different <c>engine_family</c>, <c>platforms</c>, <c>supports</c> or <c>mode</c> is a 409
/// <c>capability_conflict</c> (contracts/adapter/README.md §Negotiation rules).
/// <para>
/// The outcome is a <see cref="Result{T}"/> (00 §5.5 C8), the shape the 07.1.1-T1 policy-revision store
/// returns: a value, not an exception, so the handler maps it straight to the 00 §9.2 envelope.
/// </para>
/// </summary>
internal sealed class AdapterCapabilityStore(ITenantDb db, ITenantScopeAccessor scopes)
{
    /// <summary>Registers one capability document, or returns the existing row when it is already on file.</summary>
    /// <param name="adapterName">The adapter identity: the natural key's <c>name</c> (the <c>ext.schema</c> prefix on the first-send path).</param>
    /// <param name="binding">The bound document and its verbatim <c>supports</c> JSON.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    public async ValueTask<Result<AdapterCapabilityRegistered>> RegisterAsync(
        string adapterName,
        CapabilityBinding binding,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Ingestion, nameof(RegisterAsync));

        var document = binding.Document;
        var orgId = scopes.Require().OrgId;

        // The platforms are stored sorted ordinal, so the natural-key re-read compares arrays that are
        // already in one order and a re-registration of the same document is a byte-identical no-op.
        var platforms = document.Platforms.Order(StringComparer.Ordinal).ToArray();

        var id = Guid.CreateVersion7();
        var inserted = await db.ScalarAsync<Guid?>(
            AdapterCapabilitySql.InsertCapability,
            new
            {
                id,
                org_id = orgId,
                name = adapterName,
                protocol_major = document.Protocol.Major,
                protocol_minor = document.Protocol.Minor,
                engine_family = document.EngineFamily,
                engine_version = document.EngineVersion,
                // The tenant Dapper path refuses array parameters (StringArrayTypeHandler), so the list
                // travels as JSON text and is unpacked in SQL.
                platforms = JsonSerializer.Serialize(platforms),
                supports = binding.SupportsJson,
                mode = Mode(document.Mode),
            },
            cancellationToken).ConfigureAwait(false);

        if (inserted is { } newId)
        {
            return Result.Success(new AdapterCapabilityRegistered(newId, true));
        }

        // The natural key was already on file. One re-read decides no-op versus conflict; nothing is written
        // either way, because the app role has no UPDATE grant on this table.
        var stored = await db.QuerySingleOrDefaultAsync<StoredRow>(
            AdapterCapabilitySql.ReadByNaturalKey,
            new
            {
                org_id = orgId,
                name = adapterName,
                protocol_major = document.Protocol.Major,
                protocol_minor = document.Protocol.Minor,
                engine_version = document.EngineVersion,
            },
            cancellationToken).ConfigureAwait(false);

        if (stored is null || !Matches(stored, binding, platforms))
        {
            return Result.Failure<AdapterCapabilityRegistered>(AdapterCapabilityErrors.Conflict);
        }

        return Result.Success(new AdapterCapabilityRegistered(stored.Id, false));
    }

    /// <summary>The wire value of a mode (<c>capability.v1.json</c>).</summary>
    internal static string Mode(AdapterMode mode) => mode switch
    {
        AdapterMode.Full => "full",
        AdapterMode.IngestOnly => "ingest_only",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown adapter mode"),
    };

    private static bool Matches(StoredRow stored, CapabilityBinding binding, string[] platforms)
    {
        var document = binding.Document;

        return string.Equals(stored.EngineFamily, document.EngineFamily, StringComparison.Ordinal)
            && string.Equals(stored.Mode, Mode(document.Mode), StringComparison.Ordinal)
            && stored.Platforms.AsSpan().SequenceEqual(platforms)
            && SupportsMatch(stored.Supports, document.Supports);
    }

    private static bool SupportsMatch(string storedJson, IReadOnlyDictionary<string, bool> declared)
    {
        using var stored = System.Text.Json.JsonDocument.Parse(storedJson);

        if (stored.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
            || stored.RootElement.EnumerateObject().Count() != declared.Count)
        {
            return false;
        }

        foreach (var flag in stored.RootElement.EnumerateObject())
        {
            if (!declared.TryGetValue(flag.Name, out var value)
                || flag.Value.ValueKind != System.Text.Json.JsonValueKind.True
                    && flag.Value.ValueKind != System.Text.Json.JsonValueKind.False
                || flag.Value.GetBoolean() != value)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class StoredRow
    {
        public Guid Id { get; set; }

        public string EngineFamily { get; set; } = string.Empty;

        public string[] Platforms { get; set; } = [];

        public string Supports { get; set; } = "{}";

        public string Mode { get; set; } = string.Empty;
    }
}

/// <summary>
/// Errors of the adapter-capability store (04.7.1-T2.2), the shape the 07.1.1-T1 policy-revision store
/// returns (00 §5.5 C8): a value, not an exception, so the handler maps it straight to the 00 §9.2 envelope.
/// </summary>
internal static class AdapterCapabilityErrors
{
    /// <summary>
    /// 409: the natural key is already on file with a different <c>engine_family</c>, <c>platforms</c>,
    /// <c>supports</c> or <c>mode</c>; capability rows are first-write facts, so the stored row wins.
    /// </summary>
    internal static Error Conflict => Error.Conflict(
        ApiErrorCode.CapabilityConflict,
        "an adapter capability with this name and protocol version is already registered with different engine, platforms, supports or mode");
}

/// <summary>The statements of the adapter-capability store; every one runs in the request's tenant transaction.</summary>
internal static class AdapterCapabilitySql
{
    /// <summary>
    /// One registration (00 §5.7 P2/P3): a natural key already on file is skipped by the unique key and
    /// returns no row, so a repeated identical document never writes a second row.
    /// </summary>
    internal const string InsertCapability = """
        insert into public.adapter_capability
               (id, org_id, name, protocol_major, protocol_minor, engine_family, engine_version, platforms, supports, mode)
        values (@id, @org_id, @name, @protocol_major, @protocol_minor, @engine_family, @engine_version,
                array(select p from jsonb_array_elements_text(@platforms::jsonb) with ordinality as t(p, n) order by n),
                @supports::jsonb, @mode)
        on conflict (org_id, name, protocol_major, protocol_minor, engine_version) do nothing
        returning id
        """;

    /// <summary>The row a skipped insert left behind; the only re-read a registration makes.</summary>
    internal const string ReadByNaturalKey = """
        select id, engine_family, platforms, supports::text as supports, mode
          from public.adapter_capability
         where org_id = @org_id
           and name = @name
           and protocol_major = @protocol_major
           and protocol_minor = @protocol_minor
           and engine_version = @engine_version
        """;
}
