using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Architecture.Tests.Rules;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// AC2 (02.2.1 §2, §9): the permission catalogue of <c>contracts/permissions.json</c>
/// and the code that references it cannot drift, in either direction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Referenced</b> means, for this file: a Mediator handler declares the code via
/// <c>[Permission]</c>, or <c>contracts/openapi.yaml</c> declares it as an
/// operation's <c>x-permission</c>. The two sentinels <c>none</c> and
/// <c>authenticated</c> are not codes and are excluded. That is the AC2 definition
/// the checkers below apply; it is stated here once so a reader never has to guess
/// which side of the drift a failure is on.
/// </para>
/// <para>
/// The checkers are pure functions over explicit inputs (the catalogue rows and the
/// referenced sets), so every rule is first proven on a synthetic violation and then
/// run over the real solution — the same shape <see cref="ArchitectureRules"/> uses.
/// The real-solution tests read the repository files and reflect over the module
/// assemblies, never a copy of either.
/// </para>
/// </remarks>
public sealed class CatalogueTests
{
    /// <summary>Mediator 3.0.2 request-handler interfaces (the set <c>ArchitectureRules</c> scans).</summary>
    private static readonly string[] HandlerInterfaceNames =
    [
        "ICommandHandler",
        "IQueryHandler",
        "IRequestHandler",
        "IStreamCommandHandler",
        "IStreamQueryHandler",
        "IStreamRequestHandler",
    ];

    /// <summary>The <c>x-permission</c> sentinels that are not catalogue codes.</summary>
    private static readonly string[] PermissionSentinels = ["none", "authenticated"];

    /// <summary>
    /// The catalogue codes whose first consumer is a V1 story that has not shipped
    /// yet, so they are currently referenced by no handler and no <c>x-permission</c>
    /// and are not tagged <c>reserved: true</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this list exists instead of a catalogue edit: <c>contracts/permissions.json</c>'s
    /// own comment defines <c>reserved</c> as "a catalogue-only string whose first
    /// consumer is a V2/V3 story (V1 has no endpoint or handler for it)", while the
    /// feature plan §4 (<c>02.2.1-permission-catalogue-and-enforcement.md:65</c>)
    /// defines it as "catalogue-only strings awaiting a consumer". These codes are the
    /// second case but not the first — their consumer is a V1 story that has not
    /// shipped — so flipping the flag would contradict the contract's own wording, and
    /// migration 0010's seed embeds the flags. They are classified here, on the test
    /// side, until the owning story ships; the guards in the checker fail when an
    /// entry rots (a reserved code, a code that is now referenced, or a code the
    /// catalogue does not declare).
    /// </para>
    /// <para>
    /// Every entry names the owning story and the plan line that gives it the code, and
    /// the real-solution test asserts the story file exists, so the list cannot point
    /// at a story that was renamed away.
    /// </para>
    /// </remarks>
    private static readonly PendingConsumer[] PendingV1Consumers =
    [
        new(
            "artifact:read_sensitive",
            "06.2.1",
            "plans/features/06-evidence-artifacts/06.2.1-sensitivity-classification-and-access-control.md:108 — 06.2.1-T2.1 `IssueArtifactDownloadUrl` permission check"),
        new(
            // The Story must be a file that exists under plans/features (the guard below globs "{Story}-*.md"):
            // 13.3.1's own plan file has not landed yet, so the code is classified under the story that ships it and
            // assigns its consumer — 13.1.1 §2 AC17 / §3 name 13.3.1 as the receiver. Move the Story when 13.3.1's
            // plan lands; the receiver itself does not change.
            "authoring:generate",
            "13.1.1",
            "plans/features/13-product-phases/13.1.1-feature-tree-and-manual-test-cases.md:39 — AC17 seeds the four codes and names 13.3.1 as the consumer of `authoring:generate` (route-less here); the T2.1 row at :438 repeats it"),
        new(
            "billing:read",
            "12.4.1",
            "plans/features/12-pilot-commercial/12.4.1-cost-is-known-before-price-is.md:105 — 12.4.1-T2.1 `GetInvoiceWorksheet` (`billing:read`, line 50)"),
        new(
            // 12.3.1-T1.4: `billing:write` left this list — `RecordUsageBatchHandler` declares it (the operator
            // ingress 12.3.1 §5), so the code has its first handler consumer and keeping the entry would trip the
            // "now referenced" guard below. 12.4.1-T2.1 `IssueStatement` is its next consumer, not its first.
            "fixture:write",
            "03.3.1",
            "plans/features/03-catalog-authoring/03.3.1-fixtures-and-per-run-data.md:133 — 03.3.1-T1.2 `CreateFixtureCommand`/`CreateFixtureRevisionCommand`"),
        new(
            // Same rule as `authoring:generate` above: 13.5.1's plan file has not landed, so the Story is the one that
            // ships the code and names the receiver (13.1.1 §2 AC17).
            "script:generate",
            "13.1.1",
            "plans/features/13-product-phases/13.1.1-feature-tree-and-manual-test-cases.md:39 — AC17 seeds `script:generate` and names 13.5.1 as its consumer (route-less here)"),
        new(
            "support:read",
            "02.8.1",
            "plans/features/02-identity-tenancy/02.8.1-time-boxed-operator-access.md:109 — 02.8.1-T1.2 `ListSupportGrants`"),
        new(
            "webhook:read",
            "07.6.2",
            "plans/features/07-policy-decisions/07.6.2-outbound-webhooks-and-delivery-operations.md:165 — 07.6.2-T2.1 endpoint CRUD + deliveries list"),
        new(
            "webhook:write",
            "07.6.2",
            "plans/features/07-policy-decisions/07.6.2-outbound-webhooks-and-delivery-operations.md:165 — 07.6.2-T2.1 endpoint CRUD"),
    ];

    /// <summary>
    /// The <c>reserved</c> codes whose only reference is a spec <c>x-permission</c>:
    /// the operation is in <c>contracts/openapi.yaml</c> before its handler ships, so
    /// the code is neither handler-referenced nor unreferenced. The catalogue's own
    /// description names the owning story of each — <c>draft:dry_run</c> and
    /// <c>draft:merge_request</c> (V2, 03.6.1), <c>healing:approve</c> and
    /// <c>healing:revoke</c> (V2, 00 §8.7, 08), <c>job:claim</c> and
    /// <c>job:execute</c> (V2, 00 §9.3, 05.7).
    /// </summary>
    /// <remarks>
    /// The set is pinned rather than derived: a new spec-only reserved code must be
    /// added here with its owning story, and one that gains a handler must be removed,
    /// so the allowance cannot widen unnoticed.
    /// </remarks>
    private static readonly string[] ReservedSpecOnlyCodes =
    [
        "draft:dry_run",
        "draft:merge_request",
        "healing:approve",
        "healing:revoke",
        "job:claim",
        "job:execute",
    ];

    // ------------------------------------------------------------------ real solution

    /// <summary>
    /// AC2, direction one: every code a Mediator handler declares through
    /// <c>[Permission]</c> — and every <c>x-permission</c> code the served spec
    /// declares — is a code of <c>contracts/permissions.json</c>. A code that is not
    /// (a typo, a hand-written string, a permission removed from the catalogue) fails
    /// here.
    /// </summary>
    [Fact]
    public void CodeRefsSubsetOfCatalogue()
    {
        // The scan must see every module project: a new module that this test project
        // does not reference would otherwise be skipped silently, and its handlers
        // would never be checked.
        var moduleProjects = ModuleProjectNames();
        moduleProjects.ShouldNotBeEmpty("src/Modules must hold the Evidgate.Modules.* projects");

        var loaded = ModuleAssemblies().Select(assembly => assembly.GetName().Name!).ToHashSet(StringComparer.Ordinal);
        var missing = moduleProjects.Where(name => !loaded.Contains(name)).ToList();
        missing.ShouldBeEmpty(
            "every src/Modules/Evidgate.Modules.* project must be loaded for the handler scan "
            + $"(ProjectReference it from Architecture.Tests.csproj): {string.Join(", ", missing)}");

        var handlers = HandlerTypes(ModuleAssemblies());
        handlers.ShouldNotBeEmpty("the solution must declare Mediator handlers for the scan to mean anything");

        var referenced = ReferencedCodes();
        referenced.ShouldNotBeEmpty("the referenced set must be non-empty, or the drift check is vacuous");

        var violations = CatalogueDriftRules.CodeRefsSubsetOfCatalogue(CatalogueCodes(), referenced);

        violations.ShouldBeEmpty(
            "every [Permission] code and every spec x-permission must be a contracts/permissions.json code (AC2):\n"
            + string.Join('\n', violations));
    }

    /// <summary>
    /// AC2, direction two: a catalogue code no handler and no spec operation
    /// references must be tagged <c>reserved: true</c> (it is awaiting its first
    /// consumer) or classified as a V1-pending consumer in
    /// <see cref="PendingV1Consumers"/>; a <c>reserved</c> code that a handler does
    /// reference is a violation (the flag says the opposite of what the code does).
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Referenced" means: a Mediator handler declares the code through
    /// <c>[Permission]</c>, or an operation of <c>contracts/openapi.yaml</c> declares
    /// it as its <c>x-permission</c>. Both directions are checked, so a code that
    /// gains its first consumer without the catalogue being updated fails here.
    /// </para>
    /// <para>
    /// The V1-pending list is the third state the two-way rule needs: a code whose
    /// consumer is a V1 story that has not shipped is neither referenced nor
    /// <c>reserved</c> (see <see cref="PendingV1Consumers"/> for why the flag is not
    /// flipped instead). The list is asserted non-empty and every entry must name a
    /// story file that exists under <c>plans/features/</c>, so it cannot rot silently.
    /// </para>
    /// <para>
    /// A <c>reserved</c> code whose only reference is a spec <c>x-permission</c> is
    /// allowed by the rule: the operation is in the contract before its handler ships.
    /// The set is pinned by <see cref="ReservedSpecOnlyCodes"/>, so it cannot widen
    /// unnoticed and a code that gains a handler must leave the list.
    /// </para>
    /// </remarks>
    [Fact]
    public void UnreferencedMustBeReserved()
    {
        var catalogue = CatalogueRows();
        catalogue.ShouldNotBeEmpty("contracts/permissions.json must declare the catalogue");

        var handlerCodes = HandlerPermissionCodes();
        handlerCodes.ShouldNotBeEmpty("the handler scan must find at least one [Permission] code");

        var specCodes = SpecPermissionCodes();
        specCodes.ShouldNotBeEmpty("contracts/openapi.yaml must declare x-permission codes");

        PendingV1Consumers.ShouldNotBeEmpty(
            "the V1-pending classification must be non-empty, or the two-way rule is vacuous");

        var features = Path.Combine(SolutionLayout.RepoRoot, "plans", "features");
        foreach (var pending in PendingV1Consumers)
        {
            Directory.GetFiles(features, $"{pending.Story}-*.md", SearchOption.AllDirectories)
                .ShouldNotBeEmpty(
                    $"'{pending.Code}' names story '{pending.Story}', which must exist under plans/features/ "
                    + $"(cited as {pending.Citation})");
        }

        var violations = CatalogueDriftRules.UnreferencedMustBeReserved(
            catalogue,
            handlerCodes,
            specCodes,
            PendingV1Consumers.Select(pending => pending.Code).ToList());

        violations.ShouldBeEmpty(
            "every catalogue code with no consumer must be tagged reserved: true or classified as a V1-pending "
            + "consumer, and a reserved code must have no handler (AC2):\n" + string.Join('\n', violations));

        CatalogueDriftRules.ReservedSpecOnly(catalogue, handlerCodes, specCodes)
            .ShouldBe(
                ReservedSpecOnlyCodes,
                "the reserved codes referenced only by the spec are pinned: a new one must be added here with its "
                + "owning story, and one that gains a handler must be removed (AC2)");
    }

    /// <summary>
    /// 02.2.1-T1.1: the generated constants are a build product of the catalogue, so
    /// <c>Permissions.All</c> must equal the catalogue code set exactly — a code added
    /// to the JSON without regenerating, or a hand-edited constant, fails here.
    /// </summary>
    [Fact]
    public void Generated_Constants_Match_The_Catalogue()
    {
        var catalogue = CatalogueCodes();
        catalogue.ShouldNotBeEmpty("contracts/permissions.json must declare the catalogue");

        var violations = CatalogueDriftRules.GeneratedConstantsMatchCatalogue(catalogue, Permissions.All);

        violations.ShouldBeEmpty(
            "Permissions.All must equal the catalogue code set exactly (02.2.1-T1.1):\n"
            + string.Join('\n', violations));
    }

    // ------------------------------------------------------------- seeded violations

    /// <summary>AC2: the checker reports a referenced code the catalogue does not declare.</summary>
    [Fact]
    public void CodeRefsSubsetOfCatalogue_Reports_A_Seeded_Unknown_Code()
    {
        string[] catalogue = ["run:create", "run:read"];

        CatalogueDriftRules.CodeRefsSubsetOfCatalogue(catalogue, ["run:create", "run:read"])
            .ShouldBeEmpty("a referenced set inside the catalogue is clean");

        var violations = CatalogueDriftRules.CodeRefsSubsetOfCatalogue(
            catalogue, ["run:create", "not_a:permission"]);

        violations.ShouldHaveSingleItem().ShouldContain("not_a:permission");
    }

    /// <summary>
    /// AC2: the checker reports both seeded violations — an unreferenced code that is
    /// not reserved and not V1-pending, and a reserved code a handler references —
    /// and stays silent on the clean shapes around them. The three guards on the
    /// V1-pending classification (reserved, now-referenced, unknown code) are seeded
    /// one at a time.
    /// </summary>
    [Fact]
    public void UnreferencedMustBeReserved_Reports_Seeded_Violations()
    {
        CatalogueCode[] clean =
        [
            new("run:create", Reserved: false),   // referenced by a handler
            new("audit:read", Reserved: false),   // referenced by the spec only
            new("sso:read", Reserved: true),      // reserved and unreferenced
            new("billing:read", Reserved: false), // unreferenced, not reserved, but classified as V1-pending
        ];

        CatalogueDriftRules.UnreferencedMustBeReserved(clean, ["run:create"], ["audit:read"], ["billing:read"])
            .ShouldBeEmpty(
                "a handler-referenced code, a spec-referenced code, a reserved unreferenced code and a "
                + "V1-pending code are all clean");

        CatalogueCode[] seeded =
        [
            new("run:create", Reserved: false),   // referenced by a handler: clean
            new("job:claim", Reserved: true),     // reserved and handler-referenced: violation
            new("sso:read", Reserved: true),      // reserved and unreferenced: clean
            new("billing:read", Reserved: false), // unreferenced, not reserved, not pending: violation
        ];

        var violations = CatalogueDriftRules.UnreferencedMustBeReserved(
            seeded, ["run:create", "job:claim"], [], []);

        violations.Count.ShouldBe(2);
        violations.ShouldContain(v => v.Contains("billing:read", StringComparison.Ordinal));
        violations.ShouldContain(v => v.Contains("job:claim", StringComparison.Ordinal));
        violations.ShouldAllBe(
            v => !v.Contains("run:create", StringComparison.Ordinal)
                 && !v.Contains("sso:read", StringComparison.Ordinal));

        // Guard one: a V1-pending code that is tagged reserved: true (reserved means a V2/V3 consumer).
        CatalogueDriftRules.UnreferencedMustBeReserved(
                [new("sso:read", Reserved: true)], [], [], ["sso:read"])
            .ShouldHaveSingleItem()
            .ShouldContain("reserved");

        // Guard two: a V1-pending code that is now referenced — by a handler, or by the spec.
        CatalogueDriftRules.UnreferencedMustBeReserved(
                [new("billing:read", Reserved: false)], ["billing:read"], [], ["billing:read"])
            .ShouldHaveSingleItem()
            .ShouldContain("shipped");

        CatalogueDriftRules.UnreferencedMustBeReserved(
                [new("billing:read", Reserved: false)], [], ["billing:read"], ["billing:read"])
            .ShouldHaveSingleItem()
            .ShouldContain("shipped");

        // Guard three: a V1-pending code the catalogue does not declare.
        CatalogueDriftRules.UnreferencedMustBeReserved(
                [new("run:create", Reserved: false)], ["run:create"], [], ["not_a:permission"])
            .ShouldHaveSingleItem()
            .ShouldContain("not_a:permission");

        // Guard four: the spec-only allowance. A reserved code referenced only by the
        // spec is the pinned shape; one that gains a handler leaves it (and is the
        // violation UnreferencedMustBeReserved reports); a non-reserved spec-only code
        // is not the shape at all.
        CatalogueDriftRules.ReservedSpecOnly([new("job:claim", Reserved: true)], [], ["job:claim"])
            .ShouldHaveSingleItem()
            .ShouldBe("job:claim");

        CatalogueDriftRules.ReservedSpecOnly([new("job:claim", Reserved: true)], ["job:claim"], ["job:claim"])
            .ShouldBeEmpty("a reserved code with a handler is not the spec-only shape");

        CatalogueDriftRules.ReservedSpecOnly([new("job:claim", Reserved: false)], [], ["job:claim"])
            .ShouldBeEmpty("a non-reserved spec-only code is not the spec-only shape");
    }

    /// <summary>AC2 / T1.1: the checker reports a constant missing from the catalogue and one the catalogue does not declare.</summary>
    [Fact]
    public void Generated_Constants_Match_The_Catalogue_Reports_Seeded_Drift()
    {
        string[] catalogue = ["run:create", "run:read"];

        CatalogueDriftRules.GeneratedConstantsMatchCatalogue(catalogue, ["run:read", "run:create"])
            .ShouldBeEmpty("the same set in another order is not drift");

        CatalogueDriftRules.GeneratedConstantsMatchCatalogue(catalogue, ["run:create"])
            .ShouldHaveSingleItem()
            .ShouldContain("run:read");

        CatalogueDriftRules.GeneratedConstantsMatchCatalogue(catalogue, ["run:create", "run:read", "run:cancel"])
            .ShouldHaveSingleItem()
            .ShouldContain("run:cancel");
    }

    // ------------------------------------------------------------------- repository

    /// <summary>The catalogue rows of <c>contracts/permissions.json</c>, ordinal by code.</summary>
    private static IReadOnlyList<CatalogueCode> CatalogueRows()
    {
        var path = Path.Combine(SolutionLayout.RepoRoot, "contracts", "permissions.json");

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return
        [
            .. document.RootElement.GetProperty("permissions").EnumerateArray()
                .Select(entry => new CatalogueCode(
                    entry.GetProperty("code").GetString()!,
                    entry.GetProperty("reserved").GetBoolean()))
                .OrderBy(entry => entry.Code, StringComparer.Ordinal),
        ];
    }

    /// <summary>The catalogue's code set, ordinal.</summary>
    private static IReadOnlyList<string> CatalogueCodes() =>
        [.. CatalogueRows().Select(entry => entry.Code)];

    /// <summary>
    /// Every <c>x-permission</c> code of <c>contracts/openapi.yaml</c> (the sentinels
    /// <c>none</c> and <c>authenticated</c> excluded), read as text with the same
    /// indentation-agnostic scan <c>PolicyPermissionContractTests</c> uses.
    /// </summary>
    private static IReadOnlyList<string> SpecPermissionCodes()
    {
        var path = Path.Combine(SolutionLayout.RepoRoot, "contracts", "openapi.yaml");
        var codes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(path))
        {
            var match = Regex.Match(line, @"^\s+x-permission:\s*(?<value>\S+)\s*$");
            if (!match.Success)
            {
                continue;
            }

            var value = match.Groups["value"].Value;
            if (!PermissionSentinels.Contains(value, StringComparer.Ordinal))
            {
                codes.Add(value);
            }
        }

        return [.. codes];
    }

    /// <summary>Every code a handler or the spec references, ordinal.</summary>
    private static IReadOnlyList<string> ReferencedCodes() =>
    [
        .. HandlerPermissionCodes()
            .Concat(SpecPermissionCodes())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>The permission codes the module handlers declare through <c>[Permission]</c>, ordinal.</summary>
    private static IReadOnlyList<string> HandlerPermissionCodes()
    {
        var codes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var handler in HandlerTypes(ModuleAssemblies()))
        {
            if (DeclaredPermission(handler) is { } code)
            {
                codes.Add(code);
            }
        }

        return [.. codes];
    }

    /// <summary>The <c>Evidgate.Modules.*</c> assemblies of the solution (the <c>.Contracts</c> projects excluded).</summary>
    private static IReadOnlyList<Assembly> ModuleAssemblies() =>
    [
        .. SolutionLayout.EvidgateAssemblies()
            .Where(assembly => IsModuleAssembly(assembly.GetName().Name)),
    ];

    /// <summary>The module project directory names under <c>src/Modules</c>, ordinal.</summary>
    private static IReadOnlyList<string> ModuleProjectNames()
    {
        var modules = Path.Combine(SolutionLayout.RepoRoot, "src", "Modules");

        return
        [
            .. Directory.GetDirectories(modules)
                .Select(path => Path.GetFileName(path))
                .Where(name => IsModuleAssembly(name))
                .Select(name => name!)
                .Order(StringComparer.Ordinal),
        ];
    }

    private static bool IsModuleAssembly(string? name) =>
        name is not null
        && name.StartsWith("Evidgate.Modules.", StringComparison.Ordinal)
        && !name.EndsWith(".Contracts", StringComparison.Ordinal);

    /// <summary>Every Mediator request handler declared in <paramref name="assemblies"/>, ordinal by full name.</summary>
    private static IReadOnlyList<Type> HandlerTypes(IEnumerable<Assembly> assemblies) =>
    [
        .. assemblies
            .SelectMany(ArchitectureRules.SafeGetTypes)
            .Where(IsHandler)
            .OrderBy(type => type.FullName, StringComparer.Ordinal),
    ];

    private static bool IsHandler(Type type) =>
        !type.IsAbstract
        && !type.IsInterface
        && type.GetInterfaces().Any(@interface =>
            @interface.Namespace == "Mediator"
            && HandlerInterfaceNames.Contains(@interface.Name.Split('`')[0], StringComparer.Ordinal));

    /// <summary>
    /// The permission a handler declares via <c>[Permission]</c>, read from the
    /// attribute data so the internal handler type is never instantiated (the same
    /// reader <c>PolicyPermissionContractTests</c> uses).
    /// </summary>
    private static string? DeclaredPermission(Type handler)
    {
        var attribute = handler
            .GetCustomAttributesData()
            .SingleOrDefault(data => data.AttributeType.FullName == typeof(PermissionAttribute).FullName);

        return attribute is { ConstructorArguments.Count: 1 }
            ? attribute.ConstructorArguments[0].Value as string
            : null;
    }

    // -------------------------------------------------------------- pure rule functions

    /// <summary>One catalogue row as the checkers see it: the code and its <c>reserved</c> flag.</summary>
    internal sealed record CatalogueCode(string Code, bool Reserved);

    /// <summary>
    /// One catalogue code whose first consumer is a V1 story that has not shipped
    /// yet: the code, the owning story id, and the plan line that gives it the code.
    /// </summary>
    internal sealed record PendingConsumer(string Code, string Story, string Citation);

    /// <summary>
    /// The AC2 drift rules as pure functions: each takes its inputs explicitly and
    /// returns one message per violation, so a test can prove it on a synthetic
    /// violation and then run the same function over the real solution.
    /// </summary>
    internal static class CatalogueDriftRules
    {
        /// <summary>
        /// AC2 direction one: every referenced code must be a catalogue code.
        /// </summary>
        /// <param name="catalogueCodes">The codes <c>contracts/permissions.json</c> declares.</param>
        /// <param name="referencedCodes">The codes handlers and the spec reference.</param>
        public static IReadOnlyList<string> CodeRefsSubsetOfCatalogue(
            IReadOnlyCollection<string> catalogueCodes,
            IEnumerable<string> referencedCodes)
        {
            ArgumentNullException.ThrowIfNull(catalogueCodes);
            ArgumentNullException.ThrowIfNull(referencedCodes);

            var catalogue = catalogueCodes.ToHashSet(StringComparer.Ordinal);

            return
            [
                .. referencedCodes
                    .Distinct(StringComparer.Ordinal)
                    .Where(code => !catalogue.Contains(code))
                    .Order(StringComparer.Ordinal)
                    .Select(code => $"'{code}' is referenced by a handler or the spec but is not a contracts/permissions.json code (AC2)"),
            ];
        }

        /// <summary>
        /// AC2 direction two: a code no handler and no spec operation references must
        /// be tagged <c>reserved: true</c> or classified as a V1-pending consumer; a
        /// <c>reserved</c> code a handler references is a violation (reserved means
        /// awaiting a consumer).
        /// </summary>
        /// <param name="catalogue">The catalogue rows with their <c>reserved</c> flags.</param>
        /// <param name="handlerCodes">The codes Mediator handlers declare.</param>
        /// <param name="specCodes">The codes <c>contracts/openapi.yaml</c> declares as <c>x-permission</c>.</param>
        /// <param name="pendingCodes">
        /// The codes classified as V1-pending consumers: their first consumer is a V1
        /// story that has not shipped, so they are legitimately unreferenced without
        /// being <c>reserved</c>. The classification is guarded in both directions, so
        /// an entry cannot rot: a pending code that is <c>reserved</c>, that is now
        /// referenced, or that the catalogue does not declare is a violation.
        /// </param>
        public static IReadOnlyList<string> UnreferencedMustBeReserved(
            IReadOnlyCollection<CatalogueCode> catalogue,
            IReadOnlyCollection<string> handlerCodes,
            IReadOnlyCollection<string> specCodes,
            IReadOnlyCollection<string> pendingCodes)
        {
            ArgumentNullException.ThrowIfNull(catalogue);
            ArgumentNullException.ThrowIfNull(handlerCodes);
            ArgumentNullException.ThrowIfNull(specCodes);
            ArgumentNullException.ThrowIfNull(pendingCodes);

            var handlers = handlerCodes.ToHashSet(StringComparer.Ordinal);
            var spec = specCodes.ToHashSet(StringComparer.Ordinal);
            var pending = pendingCodes.ToHashSet(StringComparer.Ordinal);
            var violations = new List<string>();

            foreach (var entry in catalogue.OrderBy(row => row.Code, StringComparer.Ordinal))
            {
                var referenced = handlers.Contains(entry.Code) || spec.Contains(entry.Code);
                var isPending = pending.Contains(entry.Code);

                if (!referenced && !entry.Reserved && !isPending)
                {
                    violations.Add(
                        $"'{entry.Code}' is referenced by no handler and no x-permission, so it must be tagged reserved: true or classified as a V1-pending consumer (AC2)");
                }

                if (entry.Reserved && handlers.Contains(entry.Code))
                {
                    violations.Add(
                        $"'{entry.Code}' is tagged reserved: true but a handler references it; reserved means awaiting a consumer (AC2)");
                }

                if (entry.Reserved && isPending)
                {
                    violations.Add(
                        $"'{entry.Code}' is classified as a V1-pending consumer but is tagged reserved: true; reserved means a V2/V3 consumer (AC2)");
                }

                if (referenced && isPending)
                {
                    violations.Add(
                        $"'{entry.Code}' is classified as a V1-pending consumer but is now referenced; the consumer shipped, so remove the entry (AC2)");
                }
            }

            var catalogueCodes = catalogue.Select(row => row.Code).ToHashSet(StringComparer.Ordinal);

            foreach (var code in pending.Except(catalogueCodes, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                violations.Add(
                    $"'{code}' is classified as a V1-pending consumer but is not a contracts/permissions.json code (AC2)");
            }

            return violations;
        }

        /// <summary>
        /// AC2, the spec-only allowance: the <c>reserved</c> codes whose only reference
        /// is a spec <c>x-permission</c> — the operation is in the contract before its
        /// handler ships. The caller pins the expected set, so the allowance cannot
        /// widen unnoticed.
        /// </summary>
        /// <param name="catalogue">The catalogue rows with their <c>reserved</c> flags.</param>
        /// <param name="handlerCodes">The codes Mediator handlers declare.</param>
        /// <param name="specCodes">The codes <c>contracts/openapi.yaml</c> declares as <c>x-permission</c>.</param>
        public static IReadOnlyList<string> ReservedSpecOnly(
            IReadOnlyCollection<CatalogueCode> catalogue,
            IReadOnlyCollection<string> handlerCodes,
            IReadOnlyCollection<string> specCodes)
        {
            ArgumentNullException.ThrowIfNull(catalogue);
            ArgumentNullException.ThrowIfNull(handlerCodes);
            ArgumentNullException.ThrowIfNull(specCodes);

            var handlers = handlerCodes.ToHashSet(StringComparer.Ordinal);
            var spec = specCodes.ToHashSet(StringComparer.Ordinal);

            return
            [
                .. catalogue
                    .Where(entry => entry.Reserved && !handlers.Contains(entry.Code) && spec.Contains(entry.Code))
                    .Select(entry => entry.Code)
                    .Order(StringComparer.Ordinal),
            ];
        }

        /// <summary>
        /// T1.1: the generated constants must equal the catalogue code set exactly,
        /// both directions.
        /// </summary>
        /// <param name="catalogueCodes">The codes <c>contracts/permissions.json</c> declares.</param>
        /// <param name="generatedCodes">The codes <c>Permissions.All</c> carries.</param>
        public static IReadOnlyList<string> GeneratedConstantsMatchCatalogue(
            IReadOnlyCollection<string> catalogueCodes,
            IReadOnlyCollection<string> generatedCodes)
        {
            ArgumentNullException.ThrowIfNull(catalogueCodes);
            ArgumentNullException.ThrowIfNull(generatedCodes);

            var catalogue = catalogueCodes.ToHashSet(StringComparer.Ordinal);
            var generated = generatedCodes.ToHashSet(StringComparer.Ordinal);
            var violations = new List<string>();

            foreach (var code in catalogue.Except(generated).Order(StringComparer.Ordinal))
            {
                violations.Add($"'{code}' is in contracts/permissions.json but missing from Permissions.All");
            }

            foreach (var code in generated.Except(catalogue).Order(StringComparer.Ordinal))
            {
                violations.Add($"'{code}' is in Permissions.All but not in contracts/permissions.json");
            }

            return violations;
        }
    }
}
