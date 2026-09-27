using System.Globalization;
using Evidgate.Api;
using Evidgate.BuildingBlocks.Persistence.Isolation;
using Evidgate.Worker;
using Npgsql;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC5 / area plan 02 §7: the API and the Worker read the catalogue once at startup
/// as the application role and refuse to start when a table the exception list does
/// not excuse is not under database-enforced isolation. Real processes against a
/// real migrated database, so the observed thing is the process's own exit code and
/// its own boot line — the two facts an operator has after a deploy.
/// </summary>
[Collection(PostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class IsolationSelfCheckBootTests
{
    /// <summary>The line the passing check writes (EventId 1211).</summary>
    private const string PassedLine = "Isolation self-check passed";

    /// <summary>The named refusal (02.6.1-T1.4 AC5).</summary>
    private const string RefusalCode = "isolation_self_check_failed";

    private readonly PostgresFixture _postgres;

    public IsolationSelfCheckBootTests(PostgresFixture postgres) => _postgres = postgres;

    /// <summary>
    /// A dropped tenant policy — the state a hand-run <c>DROP POLICY</c>, a failed
    /// expand/contract step or a rollback leaves behind — must stop the boot. The
    /// table keeps its ENABLE/FORCE flags and its unrelated owner policy, so this also
    /// proves the check judges the policy <em>shape</em> and not merely that some
    /// policy row exists.
    /// </summary>
    [Fact]
    public async Task SelfCheck_MissingPolicy_FailsStartup()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        await DropProjectTenantPolicyAsync(connectionString);

        var apiPort = HostLocator.FreePort();
        var api = await RunToExitAsync(
            HostLocator.AppHost("Evidgate.Api"),
            ApiEnvironment(connectionString, apiPort));

        Assert.Equal(ApiHost.IsolationSelfCheckExitCode, api.ExitCode);

        var apiOutput = api.Stdout + api.Stderr;
        Assert.Contains(RefusalCode, apiOutput, StringComparison.Ordinal);
        Assert.Contains("public.project", apiOutput, StringComparison.Ordinal);
        Assert.Contains("tenant_policy_missing", apiOutput, StringComparison.Ordinal);

        // 00 §5.7 S3: the refusal names tables and codes, never a credential.
        var password = new NpgsqlConnectionStringBuilder(connectionString).Password;
        Assert.False(string.IsNullOrEmpty(password), "the fixture must hand out a real credential for this assertion");
        Assert.DoesNotContain(password, apiOutput, StringComparison.Ordinal);

        // The Worker claims tenant tasks and opens the same tenant scopes (00 §5.4.2),
        // so the same database must stop it too.
        var workerPort = HostLocator.FreePort();
        var worker = await RunToExitAsync(
            HostLocator.AppHost("Evidgate.Worker"),
            WorkerEnvironment(connectionString, workerPort));

        Assert.Equal(WorkerHost.IsolationSelfCheckExitCode, worker.ExitCode);

        var workerOutput = worker.Stdout + worker.Stderr;
        Assert.Contains(RefusalCode, workerOutput, StringComparison.Ordinal);
        Assert.Contains("public.project", workerOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(password, workerOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other refusal of AC5, and the one the story's failure-path review names: a
    /// catalogue that cannot be read is a refusal too, because "cannot confirm" must
    /// never read as "confirmed". The database port is closed, so there is no policy
    /// to judge and no violation to list — the host must still leave with exit code 3
    /// and the named line, in both entry points, instead of serving on a database
    /// whose isolation it never confirmed.
    /// </summary>
    [Fact]
    public async Task SelfCheck_UnreadableCatalogue_FailsStartup()
    {
        // A closed port — the shape of a database that is down during a deploy or
        // draining. The password is a marker the output must never echo (00 §5.7 S3).
        const string password = "isolation-probe-secret";
        var connectionString =
            $"Host=127.0.0.1;Port=1;Username=evidgate_app;Password={password};Database=evidgate;Timeout=2";

        var apiPort = HostLocator.FreePort();
        var api = await RunToExitAsync(
            HostLocator.AppHost("Evidgate.Api"),
            ApiEnvironment(connectionString, apiPort));

        Assert.Equal(ApiHost.IsolationSelfCheckExitCode, api.ExitCode);

        var apiOutput = api.Stdout + api.Stderr;
        Assert.Contains(RefusalCode, apiOutput, StringComparison.Ordinal);
        Assert.Contains(IsolationSelfCheckCodes.CatalogueUnreadable, apiOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(password, apiOutput, StringComparison.Ordinal);

        var workerPort = HostLocator.FreePort();
        var worker = await RunToExitAsync(
            HostLocator.AppHost("Evidgate.Worker"),
            WorkerEnvironment(connectionString, workerPort));

        Assert.Equal(WorkerHost.IsolationSelfCheckExitCode, worker.ExitCode);

        var workerOutput = worker.Stdout + worker.Stderr;
        Assert.Contains(RefusalCode, workerOutput, StringComparison.Ordinal);
        Assert.Contains(IsolationSelfCheckCodes.CatalogueUnreadable, workerOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(password, workerOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exception list is the single source both hosts judge at boot (02.6.1-T1.4),
    /// so a malformed line must fail loudly and name itself: a list that silently
    /// swallowed a typo would look loaded while excusing nothing — or excuse the wrong
    /// table. Each case is one shape the file format forbids.
    /// </summary>
    [Theory]
    [InlineData("public.role", "the kind is missing")]
    [InlineData("public.role catalogue extra", "a fourth token")]
    [InlineData("role catalogue", "the table is not schema-qualified")]
    [InlineData("Public.role catalogue", "an uppercase schema")]
    [InlineData("public.role platforme", "an unknown kind")]
    [InlineData("public.role catalogue\npublic.role mechanism", "the same table listed twice")]
    public void SelfCheck_ExceptionList_MalformedLine_FailsLoudly(string text, string because)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => RlsExceptionList.Parse(text));

        Assert.True(
            exception.Message.Contains(RlsExceptionList.InvalidCode, StringComparison.Ordinal),
            $"{because}: expected '{RlsExceptionList.InvalidCode}' to be named, got '{exception.Message}'");
    }

    /// <summary>
    /// An entry for a table no migration has created yet is allowed and stays inert:
    /// the committed list names the identity and 03 catalogue tables before their
    /// migrations land, and the boot must not fail because of that.
    /// </summary>
    [Fact]
    public void SelfCheck_ExceptionList_NotYetMigratedEntry_IsInert()
    {
        var list = RlsExceptionList.Parse("public.organization platform # root\npublic.session platform # not migrated yet\n");

        Assert.Equal(RlsExceptionKind.Platform, list.KindOf("public.session"));
        Assert.Null(list.KindOf("public.project"));
    }

    /// <summary>
    /// D3 of the story's failure-path review: the tenant predicate exists, but its policy
    /// names <c>evidgate_operator</c>, so <c>evidgate_app</c> has no policy at all. The
    /// table is fail-closed (zero rows) but the cause is undiagnosed, so the check must say
    /// which role the predicate is stuck on — not report it as a predicate that never existed.
    /// </summary>
    [Fact]
    public async Task SelfCheck_TenantPolicyForAnotherRole_Fails()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        Assert.True((await RunSelfCheckAsync(connectionString)).Passed, "the migrated database must pass before the mutation");

        await ExecuteAsOwnerAsync(
            connectionString,
            """
            drop policy project_app_tenant on public.project;
            create policy project_operator_read on public.project
              for select
              to evidgate_operator
              using (org_id = evidgate.current_org_id());
            """);

        var report = await RunSelfCheckAsync(connectionString);

        AssertViolation(report, "public.project", IsolationSelfCheckCodes.TenantPolicyWrongRole);
        Assert.Contains(report.Violations, violation =>
            violation.Code == IsolationSelfCheckCodes.TenantPolicyWrongRole
            && violation.Detail.Contains("evidgate_operator", StringComparison.Ordinal));
    }

    /// <summary>
    /// D5 of the story's failure-path review: <c>using (true)</c> with the correct WITH CHECK.
    /// The table refuses to write another org's row but happily reads it, which is exactly the
    /// leak AC1 forbids — the check must judge the USING clause, not merely that the predicate
    /// appears somewhere in the policy.
    /// </summary>
    [Fact]
    public async Task SelfCheck_TenantPolicyWithOpenUsing_Fails()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        Assert.True((await RunSelfCheckAsync(connectionString)).Passed, "the migrated database must pass before the mutation");

        await ExecuteAsOwnerAsync(
            connectionString,
            """
            drop policy project_app_tenant on public.project;
            create policy project_app_tenant on public.project
              for all
              to evidgate_app
              using (true)
              with check (org_id = evidgate.current_org_id());
            """);

        var report = await RunSelfCheckAsync(connectionString);

        AssertViolation(report, "public.project", IsolationSelfCheckCodes.TenantPolicyUsingNotScoped);
    }

    /// <summary>
    /// W7-SEC-01: the scoping rule is about the clause's structure, not about its text. Each
    /// case below carries the tenant predicate and still admits every row, because the
    /// top-level operator is <c>OR</c>: PostgreSQL deparses <c>(pred AND x) OR true</c> as
    /// <c>(((org_id = evidgate.current_org_id()) AND (status IS NOT NULL)) OR true)</c>, and a
    /// matcher that strips parentheses before matching reads that as <c>pred AND …</c> and
    /// accepts a policy that exposes every org. The check must refuse the clause and name the
    /// side that failed — USING and WITH CHECK alike.
    /// </summary>
    [Theory]
    [InlineData(
        "((org_id = evidgate.current_org_id() and status is not null) or true)",
        "org_id = evidgate.current_org_id()",
        IsolationSelfCheckCodes.TenantPolicyUsingNotScoped,
        "a USING that OR-combines the predicate with true")]
    [InlineData(
        "true or (status is not null and org_id = evidgate.current_org_id())",
        "org_id = evidgate.current_org_id()",
        IsolationSelfCheckCodes.TenantPolicyUsingNotScoped,
        "a USING that OR-combines true with the predicate")]
    [InlineData(
        "org_id = evidgate.current_org_id()",
        "((org_id = evidgate.current_org_id() and status is not null) or true)",
        IsolationSelfCheckCodes.TenantPolicyWithCheckNotScoped,
        "a WITH CHECK that OR-combines the predicate with true")]
    [InlineData(
        "org_id = evidgate.current_org_id()",
        "true or (status is not null and org_id = evidgate.current_org_id())",
        IsolationSelfCheckCodes.TenantPolicyWithCheckNotScoped,
        "a WITH CHECK that OR-combines true with the predicate")]
    public async Task SelfCheck_TenantPolicyOrWidenedClause_Fails(
        string usingClause,
        string withCheckClause,
        string expectedCode,
        string because)
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        Assert.True((await RunSelfCheckAsync(connectionString)).Passed, "the migrated database must pass before the mutation");

        await ExecuteAsOwnerAsync(
            connectionString,
            $"""
            drop policy project_app_tenant on public.project;
            create policy project_app_tenant on public.project
              for all
              to evidgate_app
              using ({usingClause})
              with check ({withCheckClause});
            """);

        var report = await RunSelfCheckAsync(connectionString);

        Assert.False(
            report.Passed,
            $"{because} must be refused as {expectedCode}; violations: {string.Join("; ", report.Violations)}");
        Assert.Contains(
            report.Violations,
            violation => string.Equals(violation.Table, "public.project", StringComparison.Ordinal)
                && string.Equals(violation.Code, expectedCode, StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of W7-SEC-01: the predicate narrowed by a further <c>AND</c>-ed
    /// condition is still a scoped policy, and the structural matcher must keep accepting it
    /// (a rule that only ever refuses would be unusable). PostgreSQL deparses the clause as
    /// <c>((org_id = evidgate.current_org_id()) AND (status IS NOT NULL))</c>.
    /// </summary>
    [Fact]
    public async Task SelfCheck_TenantPolicyWithExtraAndedCondition_Passes()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        Assert.True((await RunSelfCheckAsync(connectionString)).Passed, "the migrated database must pass before the mutation");

        await ExecuteAsOwnerAsync(
            connectionString,
            """
            drop policy project_app_tenant on public.project;
            create policy project_app_tenant on public.project
              for all
              to evidgate_app
              using (org_id = evidgate.current_org_id() and status is not null)
              with check (org_id = evidgate.current_org_id() and status is not null);
            """);

        var report = await RunSelfCheckAsync(connectionString);

        Assert.True(
            report.Passed,
            $"a tenant policy narrowed by another AND-ed condition must pass; violations: {string.Join("; ", report.Violations)}");
    }

    /// <summary>
    /// A <c>for select</c> policy with the right predicate: reads are scoped, every other
    /// command is ungoverned. PostgreSQL then leaves INSERT to the table's own grants, so an
    /// insert either succeeds with no row check or fails with 42501 — the check must name the
    /// missing command coverage rather than call the predicate missing.
    /// </summary>
    [Fact]
    public async Task SelfCheck_TenantPolicyNotForAllCommands_Fails()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        Assert.True((await RunSelfCheckAsync(connectionString)).Passed, "the migrated database must pass before the mutation");

        await ExecuteAsOwnerAsync(
            connectionString,
            """
            drop policy project_app_tenant on public.project;
            create policy project_app_tenant on public.project
              for select
              to evidgate_app
              using (org_id = evidgate.current_org_id());
            """);

        var report = await RunSelfCheckAsync(connectionString);

        AssertViolation(report, "public.project", IsolationSelfCheckCodes.TenantPolicyNotAllCommands);
    }

    /// <summary>
    /// A permissive policy is OR-combined with its siblings, so a correct tenant policy plus a
    /// second <c>using (true)</c> one still exposes every org's rows. The rule that catches it
    /// is separate from the required-policy rule: the violation must name the leaking policy,
    /// not the table's overall policy shape.
    /// </summary>
    [Fact]
    public async Task SelfCheck_ExtraPermissiveUnscopedPolicy_Fails()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        Assert.True((await RunSelfCheckAsync(connectionString)).Passed, "the migrated database must pass before the mutation");

        await ExecuteAsOwnerAsync(
            connectionString,
            """
            create policy project_app_reader on public.project
              for all
              to evidgate_app
              using (true)
              with check (true);
            """);

        var report = await RunSelfCheckAsync(connectionString);

        AssertViolation(report, "public.project", IsolationSelfCheckCodes.PermissivePolicyUnscoped);
        Assert.Contains(report.Violations, violation =>
            violation.Code == IsolationSelfCheckCodes.PermissivePolicyUnscoped
            && violation.Detail.Contains("project_app_reader", StringComparison.Ordinal));
    }

    /// <summary>
    /// D7 of the story's failure-path review: <c>organization</c> carries no <c>org_id</c> column,
    /// so a platform-kind entry that merely requires "a policy" accepts <c>using (true)</c> and
    /// every org then sees every organization. Its predicate is <c>id = evidgate.current_org_id()</c>
    /// (T4, 02 §4), and the check must enforce that per table while <c>permission</c> — genuinely
    /// open platform data — keeps passing.
    /// </summary>
    [Fact]
    public async Task SelfCheck_OrganizationPolicyWithOpenUsing_Fails()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        Assert.True((await RunSelfCheckAsync(connectionString)).Passed, "the migrated database must pass before the mutation");

        await ExecuteAsOwnerAsync(
            connectionString,
            """
            drop policy organization_app_tenant on public.organization;
            create policy organization_app_tenant on public.organization
              for all
              to evidgate_app
              using (true)
              with check (true);
            """);

        var report = await RunSelfCheckAsync(connectionString);

        AssertViolation(report, "public.organization", IsolationSelfCheckCodes.PlatformPolicyUsingNotScoped);
    }

    /// <summary>The positive half: a database at head passes, the boot says so, and the API serves.</summary>
    [Fact]
    public async Task SelfCheck_Passes_OnMigratedDatabase_AndApiServes()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        var port = HostLocator.FreePort();

        using var process = ProcessRunner.Start(
            HostLocator.AppHost("Evidgate.Api"),
            ApiEnvironment(connectionString, port));

        try
        {
            var ready = await PollForStatusAsync(
                $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/internal/health/ready",
                TimeSpan.FromSeconds(30));

            Assert.Equal(200, ready);

            // The boot said it checked: a host that silently skipped the gate would be
            // indistinguishable from one that passed it.
            var line = await WaitForLineAsync(process.StandardOutput, PassedLine, TimeSpan.FromSeconds(30));
            Assert.Contains("table(s) under forced row level security", line, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    /// <summary>
    /// The one escape hatch is explicit and loud: with the opt-out set the host boots
    /// against a database with a dropped policy, and it says so at Warning. A
    /// deployment cannot end up with isolation checking off quietly.
    /// </summary>
    [Fact]
    public async Task SelfCheck_Disabled_IsAnnounced()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        await DropProjectTenantPolicyAsync(connectionString);

        var port = HostLocator.FreePort();
        var environment = ApiEnvironment(connectionString, port);
        environment[IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled;

        using var process = ProcessRunner.Start(HostLocator.AppHost("Evidgate.Api"), environment);

        try
        {
            var ready = await PollForStatusAsync(
                $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/internal/health/ready",
                TimeSpan.FromSeconds(30));

            Assert.Equal(200, ready);

            var line = await WaitForLineAsync(process.StandardOutput, "Isolation self-check is DISABLED", TimeSpan.FromSeconds(30));
            Assert.Contains(IsolationSelfCheckEnvironment.Variable, line, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    /// <summary>The environment the Api needs to boot as a real process (the SkeletonBoot pattern).</summary>
    private static Dictionary<string, string> ApiEnvironment(string connectionString, int port) =>
        new(StringComparer.Ordinal)
        {
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}",
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = connectionString,
        };

    /// <summary>The environment the Worker needs, including its readiness listener.</summary>
    private static Dictionary<string, string> WorkerEnvironment(string connectionString, int port) =>
        new(StringComparer.Ordinal)
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = connectionString,
            ["EVIDGATE__WORKER__HEALTHPORT"] = port.ToString(CultureInfo.InvariantCulture),
            ["EVIDGATE__WORKER__READINESSPOLLMILLISECONDS"] = "100",
        };

    /// <summary>Drops the tenant policy of <c>public.project</c> as the migration role that owns it.</summary>
    private Task DropProjectTenantPolicyAsync(string appConnectionString) =>
        ExecuteAsOwnerAsync(appConnectionString, "drop policy project_app_tenant on public.project");

    /// <summary>Runs the production check over the migrated database, as the application role.</summary>
    private static async Task<IsolationSelfCheckReport> RunSelfCheckAsync(string appConnectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(appConnectionString);
        var check = new IsolationSelfCheck(dataSource, RlsExceptionList.LoadEmbedded());
        return await check.RunAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Runs DDL as <c>evidgate_owner</c>, the role that owns the tables and their policies
    /// (the fixture connects the app role itself, which may not change them). This is the
    /// shape of a hand-run policy change on a live database: the migration lint already
    /// passed, and only the boot sees the new state.
    /// </summary>
    private async Task ExecuteAsOwnerAsync(string appConnectionString, string sql)
    {
        var database = new NpgsqlConnectionStringBuilder(appConnectionString).Database
            ?? throw new InvalidOperationException("the fixture connection string names no database");

        await using var connection = new NpgsqlConnection(_postgres.OwnerConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Asserts the check refused the boot and named exactly this table and reason code.</summary>
    private static void AssertViolation(IsolationSelfCheckReport report, string table, string code)
    {
        Assert.False(
            report.Passed,
            $"expected a refusal naming {table} ({code}); the check passed. Violations: {string.Join("; ", report.Violations)}");

        Assert.Contains(
            report.Violations,
            violation => string.Equals(violation.Table, table, StringComparison.Ordinal)
                && string.Equals(violation.Code, code, StringComparison.Ordinal));
    }

    /// <summary>Runs a host process that is expected to exit on its own (a refused boot).</summary>
    private static async Task<ProcessRunner.Result> RunToExitAsync(
        string fileName,
        IReadOnlyDictionary<string, string> environment)
    {
        using var process = ProcessRunner.Start(fileName, environment);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            return await ProcessRunner.WaitForExitAsync(process, deadline.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            throw new TimeoutException($"{fileName} did not exit within 60 s of a refused boot");
        }
    }

    private static async Task<int> PollForStatusAsync(string url, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        var last = -1;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
                last = (int)response.StatusCode;

                if (last == 200)
                {
                    return last;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        return last;
    }

    private static async Task<string> WaitForLineAsync(StreamReader reader, string contains, TimeSpan timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(timeout);

        while (true)
        {
            var line = await reader.ReadLineAsync(deadline.Token);

            if (line is null)
            {
                throw new InvalidOperationException($"the host exited before '{contains}' appeared on stdout");
            }

            if (line.Contains(contains, StringComparison.Ordinal))
            {
                return line;
            }
        }
    }
}
