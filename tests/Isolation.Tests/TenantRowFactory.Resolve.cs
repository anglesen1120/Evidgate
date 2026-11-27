
namespace Isolation.Tests;

internal sealed partial class TenantRowFactory
{
    /// <summary>
    /// Resolves every value of one row: foreign-key columns from
    /// <paramref name="tenantParentOrg"/>'s parent rows, the row's own
    /// <c>org_id</c> from <paramref name="org"/>, and the remaining NOT NULL,
    /// default-less columns from the store-type rules.
    /// </summary>
    private async Task<Dictionary<string, object>> ResolveAsync(
        string table,
        Guid org,
        Guid tenantParentOrg,
        Guid? forcedId,
        bool createMissingParents,
        CancellationToken cancellationToken,
        string? corruptedForeignKey = null)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);

        // The row is filed under its own org. A table whose org_id is part of its
        // root foreign key gets the same value from the parent below (the root row's
        // id IS the org, TL T4); a table with no foreign key at all (rls_probe) has
        // only this line to give it its tenant — which is also what makes the corrupt
        // row differ from a valid one in exactly one value.
        if (_schema.HasOrgId(table))
        {
            values[TenantFkScan.TenantColumn] = org;
        }

        // The columns of the foreign key the caller corrupts, so the collision guard below never moves
        // a value that is the corruption itself. The key itself is resolved after every other one (see
        // below), because a key that shares a column with another key of the row
        // (authoring_change_target_fk carries project_id) would otherwise have that column resolved a
        // second time from the row's own org and the corruption would be repaired before the insert,
        // making a row the suite expects rejected look valid.
        string[] corruptedColumns = [];
        (ScannedForeignKey Key, GeneratedRow Parent)? corruption = null;

        void Assign(ScannedForeignKey foreignKey, GeneratedRow parent, Guid parentOrg, bool onlyUnset, ref bool assigned)
        {
            for (var index = 0; index < foreignKey.Columns.Length; index++)
            {
                var column = foreignKey.Columns[index];

                if (string.Equals(column, TenantFkScan.TenantColumn, StringComparison.Ordinal))
                {
                    // Already the row's own org (set above); the corruption is that the
                    // rest of the composite key still points at the other org's parent.
                    continue;
                }

                if (onlyUnset && values.ContainsKey(column))
                {
                    continue;
                }

                if (!parent.Values.TryGetValue(foreignKey.PrincipalColumns[index], out var parentValue))
                {
                    throw new InvalidOperationException(
                        $"the generated {foreignKey.PrincipalTable} row for org {parentOrg} has no value for "
                        + $"'{foreignKey.PrincipalColumns[index]}', which {table}.{column} ({foreignKey.Name}) references");
                }

                values[column] = parentValue;
                assigned = true;
            }
        }

        foreach (var foreignKey in _schema.ForeignKeysOf(table))
        {
            // MATCH SIMPLE (the only match type in this schema): a composite key with a NULL member
            // is not enforced, so such a reference is left NULL unless it is the key under test.
            // Following it can also loop forever — a self-reference (feature_node.parent_id,
            // ingest_token.rotated_to_id) or a cycle of tables (project.default_environment_id ->
            // environment -> project). `feature_node_parent_fk` is exactly the trap: its other
            // member `project_id` is NOT NULL, so an `All` test here followed `parent_id` into a
            // second `feature_node`, whose own parent was followed again (CompositeFkTests.cs:141
            // uses the same `Any` rule).
            var corrupted = corruptedForeignKey is null || string.Equals(corruptedForeignKey, foreignKey.Name, StringComparison.Ordinal);
            if (corrupted && corruptedForeignKey is not null)
            {
                corruptedColumns = foreignKey.Columns;
            }
            var ownColumns = foreignKey.Columns
                .Where(column => !string.Equals(column, TenantFkScan.TenantColumn, StringComparison.Ordinal))
                .ToArray();
            // Text intakes require a feature node despite its nullable FK; only images leave it absent.
            var requiredIntakeNode = string.Equals(table, "public.authoring_intake", StringComparison.Ordinal)
                && string.Equals(foreignKey.Name, "authoring_intake_feature_node_fk", StringComparison.Ordinal)
                && !string.Equals(corruptedForeignKey, "authoring_intake_artifact_fk", StringComparison.Ordinal);
            // A requirement link names exactly one target (0138 CHECK) although both target keys are nullable: the
            // item is the generated one unless the case key is the one under test.
            var requiredLinkItem = string.Equals(table, "public.requirement_link", StringComparison.Ordinal)
                && string.Equals(foreignKey.Name, "requirement_link_item_fk", StringComparison.Ordinal)
                && !string.Equals(corruptedForeignKey, "requirement_link_case_fk", StringComparison.Ordinal);
            if (!(corruptedForeignKey is not null && corrupted)
                && !requiredIntakeNode
                && !requiredLinkItem
                && ownColumns.Length > 0
                && ownColumns.Any(column => !_schema.ColumnsOf(table).Single(c => string.Equals(c.Name, column, StringComparison.Ordinal)).NotNull))
            {
                continue;
            }

            // The release pair of a reprocess request is pinned to a seeded catalogue row below: the app role
            // cannot write report_parser, so the factory must not try to mint a parent for it. That seed is the
            // migration's, which the harness reset keeps (RespawnCheckpoint).
            if (string.Equals(table, "public.raw_report_reprocess", StringComparison.Ordinal)
                && string.Equals(foreignKey.PrincipalTable, "public.report_parser", StringComparison.Ordinal))
            {
                continue;
            }

            // A foreign key to another tenant table is the one the corruption moves
            // to the other org; every other parent (the tenant root, a catalogue
            // row) belongs to the row's own org.
            var principalIsTenantTable = _schema.HasOrgId(foreignKey.PrincipalTable);
            var parentOrg = principalIsTenantTable && corrupted ? tenantParentOrg : org;
            var rootReference = !principalIsTenantTable
                && Array.IndexOf(foreignKey.Columns, TenantFkScan.TenantColumn) >= 0;

            var parent = await ResolveParentAsync(
                foreignKey.PrincipalTable,
                parentOrg,
                rootReference ? parentOrg : null,
                createMissingParents,
                cancellationToken);

            if (corrupted && corruptedForeignKey is not null)
            {
                // Its parent is kept and written after the loop; see the comment above.
                corruption = (foreignKey, parent);
                continue;
            }

            var assigned = false;
            Assign(foreignKey, parent, parentOrg, onlyUnset: false, ref assigned);
        }

        if (corruption is { } deferred)
        {
            // The corruption takes only the columns no other key filled, so the keys it shares a column
            // with keep their own org's parent and stay satisfied. When every one of its columns was
            // already filled the corruption would vanish and the row would be inserted, so it then takes
            // all of them and the foreign key is the rejection the caller asserts.
            var assigned = false;
            Assign(deferred.Key, deferred.Parent, tenantParentOrg, onlyUnset: true, ref assigned);
            if (!assigned)
            {
                Assign(deferred.Key, deferred.Parent, tenantParentOrg, onlyUnset: false, ref assigned);
            }
        }

        // policy_activation's unique key is (org_id, project_id): one activation per project. A row that shares a
        // project with another row of the same org trips that key (23505) before the composite foreign key the
        // suite checks, so each generated row gets a project of its own. When the project foreign key is the one
        // under test, the project id IS the corruption and must keep pointing at the other org's project.
        if (string.Equals(table, "public.policy_activation", StringComparison.Ordinal)
            && !string.Equals(corruptedForeignKey, "policy_activation_project_fk", StringComparison.Ordinal))
        {
            var ownProject = await CreateAsync("public.project", org, forcedId: null, cancellationToken);
            values["project_id"] = ownProject.Values[TenantFkScan.TenantKeyColumn];
        }

        // quarantine_entry's partial unique index (org_id, project_id, case_id) WHERE state = 'active'
        // is not a composite-FK target, so the schema scan above never sees it — but it would reject the
        // corrupt row with 23505 before the composite FK under test is reached: the corrupt row differs
        // from the valid row inserted just before it only in `owner`, and the app role cannot write
        // `state` to move it out of the index (its INSERT grant omits that column). The corrupt row
        // therefore takes a case of its own org, so the composite FK is the only rejection.
        if (corruptedForeignKey is not null
            && string.Equals(table, "public.quarantine_entry", StringComparison.Ordinal)
            && !string.Equals(corruptedForeignKey, "quarantine_entry_case_fk", StringComparison.Ordinal))
        {
            var ownCase = await CreateAsync("public.test_case", org, forcedId: null, cancellationToken);
            values["case_id"] = ownCase.Values[TenantFkScan.TenantKeyColumn];
        }

        // raw_report (0078) names a parser release in the append-only report_parser catalogue, which the app
        // role may read but never write (0075), so a generated pair would have to be a release the migration
        // seeded. `unsupported` is the outcome whose row needs no parser at all: the pair stays null and the
        // refusal carries the empty counters and discrepancy list the factory already produces.
        if (string.Equals(table, "public.raw_report", StringComparison.Ordinal))
        {
            values["outcome"] = "unsupported";
            values["reason"] = "no_parser";
            values["producer_kind"] = "pytest-junitxml";
        }

        // raw_report's (org_id, artifact_id, parser_id, parser_version) key is a partial unique index, so the
        // duplicate repair below (non-partial keys only) cannot see it: a corrupt row reusing the valid row's
        // artifact would be answered with 23505 before the composite key under test. A second artifact of the
        // row's own org keeps that key distinct; when the artifact foreign key IS the corruption, its parent is
        // the value the suite asserts on and stays.
        if (corruptedForeignKey is not null
            && string.Equals(table, "public.raw_report", StringComparison.Ordinal)
            && !string.Equals(corruptedForeignKey, "raw_report_artifact_fk", StringComparison.Ordinal))
        {
            var ownArtifact = await CreateBesideCacheAsync("public.artifact", org, cancellationToken);
            values["artifact_id"] = ownArtifact.Values[TenantFkScan.TenantKeyColumn];
        }

        // raw_report_reprocess (0081) pins the release it will parse with, so a generated row must name one the
        // migration seeded: the catalogue is owner-written, so the factory cannot mint a pair. The harness reset
        // keeps that seed (RespawnCheckpoint), and the constants pinned here name it. Its natural key is a
        // plain unique (no partial index), so a corrupt row reusing the valid row's artifact needs a second artifact
        // of its own org for the same reason as raw_report's, and a source report of its own too.
        if (string.Equals(table, "public.raw_report_reprocess", StringComparison.Ordinal))
        {
            values["parser_id"] = BuiltInCatalogue.ReportParserId;
            values["parser_version"] = BuiltInCatalogue.ReportParserVersion;
        }

        if (corruptedForeignKey is not null
            && string.Equals(table, "public.raw_report_reprocess", StringComparison.Ordinal)
            && !string.Equals(corruptedForeignKey, "raw_report_reprocess_artifact_fk", StringComparison.Ordinal))
        {
            var ownArtifact = await CreateBesideCacheAsync("public.artifact", org, cancellationToken);
            values["artifact_id"] = ownArtifact.Values[TenantFkScan.TenantKeyColumn];
        }

        foreach (var column in _schema.ColumnsOf(table))
        {
            if (values.ContainsKey(column.Name) || !column.NotNull || column.HasDefault)
            {
                continue;
            }

            var isOwnId = string.Equals(column.Name, TenantFkScan.TenantKeyColumn, StringComparison.Ordinal);
            values[column.Name] = Value(table, column, isOwnId ? forcedId : null);
        }

        if (string.Equals(table, "public.authoring_intake", StringComparison.Ordinal))
        {
            if (string.Equals(corruptedForeignKey, "authoring_intake_artifact_fk", StringComparison.Ordinal))
            {
                values["kind"] = "image";
                values["media_type"] = "image/png";
            }
            else
            {
                values["input_text"] = "probe";
            }
        }

        // The OAuth state's primary key is its digest, and a support grant's id has a database default, so
        // neither id is generated above, yet the suite addresses every tenant row by its `id`.
        if ((string.Equals(table, "public.github_install_state", StringComparison.Ordinal)
                || string.Equals(table, "public.support_grant", StringComparison.Ordinal))
            && !values.ContainsKey(TenantFkScan.TenantKeyColumn))
        {
            values[TenantFkScan.TenantKeyColumn] = forcedId ?? Guid.CreateVersion7();
        }

        // artifact.object_key is a function of the row's own identity (0028's shape CHECK): the
        // server-chosen key embeds org, project, run, execution key, attempt and id, so the generic
        // per-column rule cannot produce it — derive it from the values just resolved.
        if (string.Equals(table, "public.artifact", StringComparison.Ordinal))
        {
            values["object_key"] = $"org/{values[TenantFkScan.TenantColumn]}/project/{values["project_id"]}"
                + $"/run/{values["run_id"]}/attempt/{values["execution_key"]}/{values["attempt_no"]}"
                + $"/{values[TenantFkScan.TenantKeyColumn]}";
        }

        // usage_ledger_entry's adjustment CHECK (0046) demands a reason exactly when adjustment_of is
        // set. The corrupt row of the composite-FK suite fills adjustment_of from the other org's row,
        // so the reason has to travel with it — otherwise the CHECK (23514) would answer before the
        // foreign key (23503) this suite asserts.
        if (string.Equals(table, "public.usage_ledger_entry", StringComparison.Ordinal)
            && values.ContainsKey("adjustment_of"))
        {
            values["reason"] = "generated adjustment";
        }

        // A credit entry that names a call must be a call-bound kind (0135 CHECK); the corrupt row of the composite-FK
        // suite fills call_id, so its kind travels with it or the CHECK (23514) would answer before the foreign key.
        if (string.Equals(table, "public.ai_credit_entry", StringComparison.Ordinal)
            && values.TryGetValue("call_id", out var creditCall)
            && creditCall is not null)
        {
            values["kind"] = "reserve";
        }

        // The linkage guard (0122) refuses an incident whose execution key is not its attempt's, and an
        // incident_attempt whose number or result is not its attempt's. Only a valid row can repeat them: a corrupt
        // row is refused by its foreign key first and keeps its random key, which keeps it off the valid row's
        // unique (org_id, run_id, execution_key).
        if (corruptedForeignKey is null
            && org == tenantParentOrg
            && (string.Equals(table, "public.incident", StringComparison.Ordinal)
                || string.Equals(table, "public.incident_attempt", StringComparison.Ordinal))
            && values.TryGetValue("attempt_id", out var attemptId))
        {
            var attempt = _created
                .Where(entry => string.Equals(entry.Key.Table, "public.case_attempt", StringComparison.Ordinal))
                .Select(entry => entry.Value)
                .FirstOrDefault(row => Equals(row.Values[TenantFkScan.TenantKeyColumn], attemptId));
            if (attempt is not null && string.Equals(table, "public.incident", StringComparison.Ordinal))
            {
                values["execution_key"] = attempt.Values["execution_key"];
            }
            else if (attempt is not null)
            {
                values["attempt_no"] = attempt.Values["attempt_no"];
                values["result"] = attempt.Values["normalized_status"];
            }
        }

        // A corrupt row is the valid row with one parent moved to the other org. When that parent is not
        // part of a unique key of the row's table, the corrupt row duplicates the valid row under that
        // key, and PostgreSQL answers 23505 before the composite foreign key under test: manual_case_link
        // moves only linked_by_membership_id, so `manual_case_link_uq` still sees the valid row's project,
        // manual case and test case. Such a key gets one of the row's other tenant parents regenerated for
        // the row's own org, so the key differs from the valid row's while the row stays valid everywhere
        // else (a same-org parent is what the valid row names) and the corrupted key remains the only
        // rejection. Only a tenant parent can be regenerated here: a catalogue parent is not org-scoped and
        // a generated scalar already differs from the valid row's.
        if (corruptedForeignKey is not null && _created.TryGetValue((table, org), out var validRow))
        {
            foreach (var uniqueKey in _schema.UniqueKeysOf(table))
            {
                var duplicatesValidRow = uniqueKey.Columns.All(column =>
                    values.TryGetValue(column, out var value)
                    && validRow.Values.TryGetValue(column, out var validValue)
                    && Equals(value, validValue));
                if (!duplicatesValidRow)
                {
                    continue;
                }

                // One parent is enough to move the key, and the narrowest key is the least it can disturb.
                // A candidate must share a non-tenant column with the key: `org_id` is in every composite key
                // and in every tenant foreign key, so an overlap on it alone would leave the key exactly as
                // it was. A foreign key that shares a column with the corruption is never touched.
                var candidates = _schema.ForeignKeysOf(table)
                    .Where(foreignKey => foreignKey.Columns.All(values.ContainsKey))
                    .Where(foreignKey => foreignKey.Columns.Any(column =>
                        !string.Equals(column, TenantFkScan.TenantColumn, StringComparison.Ordinal)
                        && uniqueKey.Columns.Contains(column)))
                    .Where(foreignKey => _schema.HasOrgId(foreignKey.PrincipalTable))
                    .Where(foreignKey => !foreignKey.Columns
                        .Where(column => !string.Equals(column, TenantFkScan.TenantColumn, StringComparison.Ordinal))
                        .Intersect(corruptedColumns, StringComparer.Ordinal)
                        .Any())
                    .OrderBy(foreignKey => foreignKey.Columns.Length)
                    .ThenBy(foreignKey => foreignKey.Name, StringComparer.Ordinal)
                    .ToArray();

                var moved = false;
                foreach (var replacement in candidates)
                {
                    // The valid row's own parent cannot be reused: its key is exactly what duplicates, so a
                    // second parent of the same org is created beside the cached one.
                    var parent = await CreateBesideCacheAsync(replacement.PrincipalTable, org, cancellationToken);

                    for (var index = 0; index < replacement.Columns.Length; index++)
                    {
                        if (!string.Equals(replacement.Columns[index], TenantFkScan.TenantColumn, StringComparison.Ordinal))
                        {
                            values[replacement.Columns[index]] = parent.Values[replacement.PrincipalColumns[index]];
                        }
                    }

                    // The replacement must actually move the key: one that left every key column equal to
                    // the valid row's would still answer 23505, so the next candidate gets its turn.
                    moved = !uniqueKey.Columns.All(column =>
                        values.TryGetValue(column, out var value)
                        && validRow.Values.TryGetValue(column, out var validValue)
                        && Equals(value, validValue));
                    if (moved)
                    {
                        break;
                    }
                }

                if (moved)
                {
                    break;
                }
            }
        }

        return values;
    }

    /// <summary>The parent row for <paramref name="org"/>: the generated one, or (when allowed) a freshly created and inserted one.</summary>
    private async Task<GeneratedRow> ResolveParentAsync(
        string table,
        Guid org,
        Guid? forcedId,
        bool createMissingParents,
        CancellationToken cancellationToken)
    {
        if (_created.TryGetValue((table, org), out var existing))
        {
            return existing;
        }

        if (!createMissingParents)
        {
            throw new InvalidOperationException(
                $"no {table} row was generated for org {org}: the corrupt row's parents must already exist "
                + "(create the valid row for both orgs before asking for the corruption)");
        }

        return await CreateAsync(table, org, forcedId, cancellationToken);
    }
}
