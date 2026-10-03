using System.Security.Cryptography;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.BuildingBlocks.Persistence.Audit;

/// <summary>Why a chain failed verification.</summary>
public enum AuditChainFault
{
    /// <summary>A <c>seq</c> is missing (a row was deleted) or out of order.</summary>
    Gap,

    /// <summary>A row's <c>prev_hash</c> is not its predecessor's <c>entry_hash</c> (a row was reordered or replaced).</summary>
    BrokenLink,

    /// <summary>A row's <c>entry_hash</c> does not match its content (a row was modified).</summary>
    HashMismatch,
}

/// <summary>The first fault found in an org's chain.</summary>
/// <param name="Fault">Kind of fault.</param>
/// <param name="Seq">The <c>seq</c> where it was detected.</param>
public sealed record AuditChainFinding(AuditChainFault Fault, long Seq);

/// <summary>
/// Walks one org's chain in <c>seq</c> order and recomputes every hash (AC6, TL T8). It runs
/// inside the org's tenant scope, so RLS limits it to that org.
/// </summary>
/// <param name="db">The scoped data access.</param>
public sealed class AuditChainVerifier(ITenantDb db)
{
    /// <summary>Rows read per statement.</summary>
    public const int PageSize = 1000;

    /// <summary>Verifies the chain of <paramref name="orgId"/>; returns the first fault, or null when it is intact.</summary>
    /// <param name="orgId">The org of the open tenant scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<AuditChainFinding?> VerifyAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var expectedSeq = 1L;
        var prev = AuditHashing.Genesis;
        var after = 0L;

        while (true)
        {
            var page = await db.QueryAsync<StoredRow>(
                AuditSql.ChainPage,
                new { org_id = orgId, after_seq = after, limit = PageSize },
                cancellationToken).ConfigureAwait(false);

            foreach (var stored in page)
            {
                var row = stored.ToRow();

                if (row.Seq != expectedSeq)
                {
                    return new AuditChainFinding(AuditChainFault.Gap, expectedSeq);
                }

                if (!CryptographicOperations.FixedTimeEquals(row.PrevHash, prev))
                {
                    return new AuditChainFinding(AuditChainFault.BrokenLink, row.Seq);
                }

                if (!CryptographicOperations.FixedTimeEquals(AuditHashing.EntryHash(prev, row), row.EntryHash))
                {
                    return new AuditChainFinding(AuditChainFault.HashMismatch, row.Seq);
                }

                prev = row.EntryHash;
                expectedSeq++;
                after = row.Seq;
            }

            if (page.Count < PageSize)
            {
                return null;
            }
        }
    }

    /// <summary>Dapper shape of <c>audit_log</c> with the metadata as text.</summary>
    internal sealed class StoredRow
    {
        public Guid Id { get; set; }

        public Guid OrgId { get; set; }

        public long Seq { get; set; }

        public string ActorType { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string? ObjectType { get; set; }

        public string? ObjectId { get; set; }

        public byte[]? BeforeHash { get; set; }

        public byte[]? AfterHash { get; set; }

        public string? RequestId { get; set; }

        public string? TraceId { get; set; }

        public string? MetadataJson { get; set; }

        public byte[] PrevHash { get; set; } = [];

        public byte[] EntryHash { get; set; } = [];

        public DateTimeOffset CreatedAt { get; set; }

        public AuditRow ToRow() => new(
            Id,
            OrgId,
            Seq,
            ActorType,
            ActorId,
            Action,
            ObjectType,
            ObjectId,
            BeforeHash,
            AfterHash,
            RequestId,
            TraceId,
            MetadataJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson),
            PrevHash,
            EntryHash,
            CreatedAt);
    }
}
