using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// The module's EF write model (00 §5.5 C4/C5): the tenant-bound context every ledger write runs on. It declares no
/// entity on purpose — 12.3.1-T1 writes the ledger with one statement over <c>unnest</c> arrays (ruling T50, plan §8 P2),
/// which EF's <c>SaveChanges</c> batching cannot express: <c>ON CONFLICT … DO NOTHING RETURNING billing_key</c> is what
/// splits the inserted keys from the duplicates in a single round trip. The context exists for the connection and the
/// tenant transaction the factory binds to it, and for the <c>TenantCommandInterceptor</c> guard that comes with them.
/// <para>
/// The Dapper path (<see cref="ITenantDb"/>) is read-only by design, so a write declared here would be the only way to
/// reach it — a second mapping of the same columns is exactly the drift the write-model rule avoids.
/// </para>
/// </summary>
internal sealed class UsageDbContext(DbContextOptions<UsageDbContext> options) : EvidgateDbContext(options);
