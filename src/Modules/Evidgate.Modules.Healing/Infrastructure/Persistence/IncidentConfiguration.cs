using Evidgate.Modules.Healing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Evidgate.Modules.Healing.Infrastructure.Persistence;

/// <summary>Maps <see cref="Incident"/> onto <c>public.incident</c>; key and index names are the migration's constraint names.</summary>
internal sealed class IncidentConfiguration : IEntityTypeConfiguration<Incident>
{
    public void Configure(EntityTypeBuilder<Incident> builder)
    {
        builder.ToTable("incident", "public");
        builder.HasKey(row => row.Id).HasName("incident_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("incident_org_id_key");
        builder.HasAlternateKey(row => new { row.OrgId, row.RunId, row.ExecutionKey }).HasName("incident_run_execution_key");
        builder.HasIndex(row => new { row.OrgId, row.ProjectId, row.CreatedAt, row.Id })
            .HasDatabaseName("incident_project_list")
            .IsDescending(false, false, true, false);
        builder.HasIndex(row => new { row.OrgId, row.ProjectId, row.State }).HasDatabaseName("incident_project_state");

        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(row => row.RunId).HasColumnName("run_id").IsRequired();
        builder.Property(row => row.AttemptId).HasColumnName("attempt_id").IsRequired();
        builder.Property(row => row.CaseId).HasColumnName("case_id").IsRequired();
        builder.Property(row => row.CaseRevisionId).HasColumnName("case_revision_id").IsRequired();
        builder.Property(row => row.ExecutionKey).HasColumnName("execution_key").IsRequired();
        builder.Property(row => row.Engine).HasColumnName("engine").IsRequired();
        builder.Property(row => row.State).HasColumnName("state").HasConversion(new IncidentStateConverter()).IsRequired();
        builder.Property(row => row.ContextState).HasColumnName("context_state").HasConversion(new IncidentContextStateConverter()).IsRequired();

        // The database stamps both on insert and update (the set_updated_at_and_version trigger), so EF reads the
        // new values back instead of writing its own.
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(row => row.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").ValueGeneratedOnAddOrUpdate();
        builder.Property(row => row.Version).HasColumnName("version").HasDefaultValue(1).IsConcurrencyToken();

        // The linkage columns are fixed at insert: EF refuses a modified one before the database trigger would.
        foreach (var property in new[] { nameof(Incident.OrgId), nameof(Incident.ProjectId), nameof(Incident.RunId), nameof(Incident.AttemptId), nameof(Incident.CaseId), nameof(Incident.CaseRevisionId), nameof(Incident.ExecutionKey), nameof(Incident.Engine) })
        {
            builder.Property(property).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }
    }

    private sealed class IncidentStateConverter() : ValueConverter<IncidentState, string>(
        state => IncidentStateText.ToText(state),
        text => IncidentStateText.FromText(text));

    private sealed class IncidentContextStateConverter() : ValueConverter<IncidentContextState, string>(
        state => IncidentStateText.ToText(state),
        text => IncidentStateText.ContextFromText(text));
}

/// <summary>The SQL spellings of the two state enums; an unknown value throws instead of mapping to a neighbour.</summary>
internal static class IncidentStateText
{
    internal static string ToText(IncidentState state) => state switch
    {
        IncidentState.Open => "open",
        IncidentState.Triaged => "triaged",
        IncidentState.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "unknown incident state"),
    };

    internal static IncidentState FromText(string text) => text switch
    {
        "open" => IncidentState.Open,
        "triaged" => IncidentState.Triaged,
        "closed" => IncidentState.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, "unknown incident state"),
    };

    internal static string ToText(IncidentContextState state) => state switch
    {
        IncidentContextState.Pending => "pending",
        IncidentContextState.Complete => "complete",
        IncidentContextState.Partial => "partial",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "unknown incident context state"),
    };

    internal static IncidentContextState ContextFromText(string text) => text switch
    {
        "pending" => IncidentContextState.Pending,
        "complete" => IncidentContextState.Complete,
        "partial" => IncidentContextState.Partial,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, "unknown incident context state"),
    };
}
