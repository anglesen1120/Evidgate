using System.Reflection;
using System.Runtime.InteropServices;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;

namespace Architecture.Tests.Rules;

/// <summary>
/// Pure rule functions for the 00 §5.5 module shape and §5.7 S2 pipeline
/// authorization contract. Every rule takes its inputs explicitly (a reference
/// graph or a list of types) so the tests can prove each rule on synthetic
/// violations and then run the same function over the real solution.
/// </summary>
public static class ArchitectureRules
{
    private const string ModulePrefix = "Evidgate.Modules.";
    private const string ContractsSuffix = ".Contracts";

    /// <summary>
    /// AC1 / 00 §5.5: a project belonging to module <c>Evidgate.Modules.&lt;X&gt;</c>
    /// may reference another module's <c>Evidgate.Modules.&lt;Y&gt;.Contracts</c>
    /// project only. Same-module references and non-module targets are allowed.
    /// Returns one violation per offending edge; each message names both projects.
    /// </summary>
    public static IReadOnlyList<string> ModuleToModuleViaContractsOnly(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> projectReferences)
    {
        var violations = new List<string>();

        foreach (var (project, references) in projectReferences.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var sourceModule = ModuleNameOf(project);
            if (sourceModule is null)
            {
                continue;
            }

            foreach (var reference in references.Order(StringComparer.Ordinal))
            {
                var targetModule = ModuleNameOf(reference);
                if (targetModule is null || targetModule == sourceModule)
                {
                    continue;
                }

                if (!reference.EndsWith(ContractsSuffix, StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{project} references {reference}: module '{sourceModule}' may only reference " +
                        $"module '{targetModule}' through Evidgate.Modules.{targetModule}.Contracts (00 §5.5)");
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// AC5 / 00 §5.5: types in a <c>*.Domain*</c> namespace must not directly
    /// reference types in a <c>*.Infrastructure*</c> namespace. Direct references
    /// are base types, interfaces, fields, properties, method signatures,
    /// attributes and generic arguments of the declaring type itself.
    /// </summary>
    public static IReadOnlyList<string> NoDomainReferencesInfrastructure(IEnumerable<Type> types)
    {
        var violations = new List<string>();

        foreach (var type in Enumerate(types).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.Namespace is null || !HasSegment(type.Namespace, "Domain"))
            {
                continue;
            }

            foreach (var dependency in DirectDependencies(type).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (dependency != type && dependency.Namespace is { } ns && HasSegment(ns, "Infrastructure"))
                {
                    violations.Add(
                        $"{type.FullName} references {dependency.FullName}: Domain must not reference Infrastructure (00 §5.5)");
                }
            }
        }

        // A backing field + property + getter all surface the same dependency
        // type; report each (domain type, infrastructure type) pair once.
        return violations.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// AC5 / 00 §5.5: a module implementation assembly (<c>Evidgate.Modules.&lt;X&gt;</c>,
    /// not <c>.Contracts</c>) may not declare public types outside its
    /// <c>…Contracts</c> or <c>…Endpoints</c> namespaces — everything else must
    /// be <c>internal</c>. <paramref name="assemblyName"/> is taken explicitly so
    /// the rule can be tested on fixture types living in the test assembly.
    /// </summary>
    public static IReadOnlyList<string> NoPublicTypesOutsideContractsOrEndpoints(
        string assemblyName,
        IEnumerable<Type> types)
    {
        var violations = new List<string>();

        foreach (var type in Enumerate(types).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.Namespace is null || !(type.IsPublic || type.IsNestedPublic))
            {
                continue;
            }

            if (!HasSegment(type.Namespace, "Contracts") && !HasSegment(type.Namespace, "Endpoints"))
            {
                violations.Add(
                    $"{assemblyName}: public type {type.FullName} is outside a .Contracts/.Endpoints namespace; " +
                    "make it internal (00 §5.5)");
            }
        }

        return violations;
    }

    /// <summary>
    /// AC5 / 00 §5.7 S2: every class implementing a Mediator handler interface
    /// must carry <see cref="PermissionAttribute"/> with a valid lower-snake
    /// resource:action string. CustomAttributeData is used (not instantiation) so
    /// a malformed argument is reported as a violation instead of throwing.
    /// </summary>
    /// <summary>
    /// The only types outside the pipeline building block that may take an
    /// <c>IRequestTenantContextSetter</c> (02.1.1 security review): publishing the request
    /// tenant decides which org every later stage — RLS included — runs in, and a service
    /// token is not bound to an org, so a route that published <c>{o}</c> from the URL
    /// would let such a token act in any org. Only host entry points (02.6.2) and the
    /// pre-tenant <c>POST /v1/orgs</c>, which publishes an id derived from the caller,
    /// may do it.
    /// </summary>
    public static readonly IReadOnlySet<string> TenantContextSetterAllowlist = new HashSet<string>(StringComparer.Ordinal)
    {
        "Evidgate.Modules.Tenancy.Endpoints.TenancyEndpoints",

        // 02.6.2-T1.1: the host's entry point that resolves the tenant per request.
        "Evidgate.Api.Tenancy.TenantResolutionFilter",

        // 02.4.1/02.4.2: the credential exchange verifies the CI credential, then publishes the tenant
        // it resolved (pre-tenant resolution, TL T1).
        "Evidgate.Modules.Identity.Endpoints.CiTokenEndpoints",
    };

    /// <summary>
    /// Every type (outside <c>Evidgate.BuildingBlocks.Pipeline</c>, which defines and
    /// registers it) that takes <c>IRequestTenantContextSetter</c> as a constructor or
    /// method parameter, field or property and is not in
    /// <see cref="TenantContextSetterAllowlist"/>.
    /// </summary>
    public static IReadOnlyList<string> OnlyAllowlistedTypesPublishTheTenant(IEnumerable<Type> types)
    {
        const string SetterName = "Evidgate.BuildingBlocks.Pipeline.Tenancy.IRequestTenantContextSetter";
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var violations = new List<string>();

        foreach (var type in Enumerate(types).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.Namespace?.StartsWith("Evidgate.BuildingBlocks.Pipeline", StringComparison.Ordinal) == true)
            {
                continue;
            }

            var outer = type;
            while (outer.DeclaringType is not null)
            {
                outer = outer.DeclaringType;
            }

            if (TenantContextSetterAllowlist.Contains(outer.FullName ?? string.Empty))
            {
                continue;
            }

            var uses = type.GetConstructors(All).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
                .Concat(type.GetMethods(All).SelectMany(m => m.GetParameters()).Select(p => p.ParameterType))
                .Concat(type.GetFields(All).Select(f => f.FieldType))
                .Concat(type.GetProperties(All).Select(p => p.PropertyType))
                .Any(t => t.FullName == SetterName);

            if (uses)
            {
                violations.Add(
                    $"{type.FullName} takes IRequestTenantContextSetter; only host entry points and " +
                    "ArchitectureRules.TenantContextSetterAllowlist may publish the request tenant");
            }
        }

        return violations;
    }

    /// <summary>
    /// 02.7.1-T2.1: every command handler that answers a <c>Result</c> (a pipeline-managed
    /// mutation) carries <c>[AuditedAction]</c>, so a new mutation cannot ship without its
    /// audit row. Queries are read-only and exempt.
    /// </summary>
    public static IReadOnlyList<string> EveryMutationDeclaresAnAuditedAction(IEnumerable<Type> types)
    {
        var violations = new List<string>();

        foreach (var type in Enumerate(types).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.IsAbstract || type.IsInterface)
            {
                continue;
            }

            var answersResult = type
                .GetInterfaces()
                .Any(i => i.Namespace == "Mediator"
                    && i.Name.StartsWith("ICommandHandler", StringComparison.Ordinal)
                    && i.GetGenericArguments().LastOrDefault() is { } response
                    && (response.Name == "Result" || response.Name.StartsWith("Result`", StringComparison.Ordinal)));

            if (!answersResult)
            {
                continue;
            }

            var audited = type
                .GetCustomAttributesData()
                .Any(a => a.AttributeType.FullName == typeof(AuditedActionAttribute).FullName);

            if (!audited)
            {
                violations.Add(
                    $"{type.FullName} is a mutation handler without [AuditedAction]; " +
                    "every permission-checked mutation appends one audit_log row (02.7.1-T2.1)");
            }
        }

        return violations;
    }

    /// <summary>
    /// The only handlers allowed to carry <see cref="AuthenticatedMemberAttribute"/>:
    /// the ones that create the tenant the caller will then be a member of (02.1.1 §5).
    /// </summary>
    public static readonly IReadOnlySet<string> AuthenticatedMemberAllowlist = new HashSet<string>(StringComparer.Ordinal)
    {
        "Evidgate.Modules.Tenancy.Application.Commands.CreateOrganizationCommandHandler",
    };

    /// <summary>
    /// 02.4.1/02.4.2: the only handlers that may carry <c>[CredentialAuthenticated]</c> instead of a permission — the two
    /// credential exchanges, whose endpoint verifies the credential before the pipeline runs.
    /// </summary>
    public static readonly IReadOnlySet<string> CredentialAuthenticatedAllowlist = new HashSet<string>(StringComparer.Ordinal)
    {
        "Evidgate.Modules.Identity.Application.Commands.ExchangeGitHubTokenCommandHandler",
        "Evidgate.Modules.Identity.Application.Commands.ExchangeIngestTokenCommandHandler",
    };

    public static IReadOnlyList<string> EveryHandlerDeclaresPermission(IEnumerable<Type> types)
    {
        var violations = new List<string>();

        foreach (var type in Enumerate(types).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.IsAbstract || type.IsInterface || !PipelineHandlerRules.IsRequestHandler(type))
            {
                continue;
            }

            var permissionAttribute = type
                .GetCustomAttributesData()
                .SingleOrDefault(a => a.AttributeType.FullName == typeof(PermissionAttribute).FullName);

            var authenticatedMember = type
                .GetCustomAttributesData()
                .Any(a => a.AttributeType.FullName == typeof(AuthenticatedMemberAttribute).FullName);

            var credentialAuthenticated = type
                .GetCustomAttributesData()
                .Any(a => a.AttributeType.FullName == typeof(CredentialAuthenticatedAttribute).FullName);

            if (credentialAuthenticated)
            {
                if (!CredentialAuthenticatedAllowlist.Contains(type.FullName ?? string.Empty))
                {
                    violations.Add(
                        $"{type.FullName} carries [CredentialAuthenticated] but is not in " +
                        "ArchitectureRules.CredentialAuthenticatedAllowlist; tenant handlers declare a permission (00 §5.7 S2)");
                }

                if (permissionAttribute is not null || authenticatedMember)
                {
                    violations.Add($"{type.FullName} carries [CredentialAuthenticated] and another gate; declare exactly one gate");
                }

                continue;
            }

            if (authenticatedMember)
            {
                // 02.1.1: the tenant-creating handler cannot be gated by a tenant
                // permission. The exception is closed by name, and never combined with
                // [Permission] (two gates would leave the reader guessing which wins).
                if (!AuthenticatedMemberAllowlist.Contains(type.FullName ?? string.Empty))
                {
                    violations.Add(
                        $"{type.FullName} carries [AuthenticatedMember] but is not in " +
                        "ArchitectureRules.AuthenticatedMemberAllowlist; tenant handlers declare a permission (00 §5.7 S2)");
                }

                if (permissionAttribute is not null)
                {
                    violations.Add(
                        $"{type.FullName} carries both [AuthenticatedMember] and [Permission]; declare exactly one gate");
                }

                continue;
            }

            if (permissionAttribute is null)
            {
                violations.Add(
                    $"{type.FullName} implements a Mediator handler interface but has no " +
                    "[Permission(\"resource:action\")] attribute; authorization lives in the pipeline (00 §5.7 S2)");
                continue;
            }

            var permission = permissionAttribute.ConstructorArguments.Count == 1
                ? permissionAttribute.ConstructorArguments[0].Value as string
                : null;
            if (!PermissionAttribute.IsValid(permission))
            {
                violations.Add($"{type.FullName}: invalid permission '{permission}'");
            }
        }

        return violations;
    }

    /// <summary>Returns the module name of a project, or null when it is not a module project.</summary>
    public static string? ModuleNameOf(string projectName)
    {
        if (!projectName.StartsWith(ModulePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = projectName[ModulePrefix.Length..];
        var dot = rest.IndexOf('.');
        return dot < 0 ? rest : rest[..dot];
    }

    /// <summary>True when <paramref name="ns"/> contains a dotted segment equal to <paramref name="segment"/>.</summary>
    private static bool HasSegment(string ns, string segment) =>
        ns.Split('.').Contains(segment, StringComparer.Ordinal);

    /// <summary>Types declared in an assembly, tolerating unloadable dependencies.</summary>
    public static IReadOnlyList<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>().ToList();
        }
    }

    private static IEnumerable<Type> Enumerate(IEnumerable<Type> types) =>
        types.Where(t => t is not null);

    private static IEnumerable<Type> DirectDependencies(Type type)
    {
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var t in Flatten(type.BaseType))
        {
            yield return t;
        }

        foreach (var i in type.GetInterfaces())
        {
            foreach (var t in Flatten(i))
            {
                yield return t;
            }
        }

        foreach (var f in type.GetFields(flags))
        {
            foreach (var t in Flatten(f.FieldType))
            {
                yield return t;
            }
        }

        foreach (var c in type.GetConstructors(flags))
        {
            foreach (var param in c.GetParameters())
            {
                foreach (var t in Flatten(param.ParameterType))
                {
                    yield return t;
                }
            }
        }

        foreach (var p in type.GetProperties(flags))
        {
            foreach (var t in Flatten(p.PropertyType))
            {
                yield return t;
            }
        }

        foreach (var m in type.GetMethods(flags))
        {
            foreach (var t in Flatten(m.ReturnType))
            {
                yield return t;
            }

            foreach (var param in m.GetParameters())
            {
                foreach (var t in Flatten(param.ParameterType))
                {
                    yield return t;
                }
            }
        }

        foreach (var attr in type.GetCustomAttributesData())
        {
            foreach (var t in Flatten(attr.AttributeType))
            {
                yield return t;
            }
        }
    }

    /// <summary>Frameworks a pure decision function may never reach: I/O, a network client, a database driver.</summary>
    public static readonly IReadOnlyList<string> ImpureAssemblyPrefixes =
    [
        "Npgsql",
        "System.Net.Http",
        "System.Data",
        "System.IO.Pipelines",
        "Microsoft.AspNetCore.Http",
        "Microsoft.Extensions.Http",
    ];

    /// <summary>The names of the assemblies an assembly references directly.</summary>
    public static IReadOnlyList<string> ReferencedAssemblyNames(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(name => name.Name).OfType<string>()];

    /// <summary>
    /// The assembly graph the evaluation reaches: every name it can get to through the reference graph, plus the
    /// references that could not be resolved, which leave its purity unproven.
    /// </summary>
    /// <param name="Reachable">Every assembly name reachable from the root, sorted ordinally.</param>
    /// <param name="Unresolved">References that could not be loaded, so their own references are unknown.</param>
    public sealed record AssemblyClosure(
        IReadOnlyList<string> Reachable,
        IReadOnlyList<string> Unresolved);

    /// <summary>
    /// Every assembly name the evaluation can reach from <paramref name="assembly"/> through the reference graph, not
    /// only its direct references. An assembly carries no assembly reference for a type it never names, so a bridge
    /// through a <c>.Contracts</c> project is invisible to a direct-reference check; the closure sees it.
    /// </summary>
    /// <remarks>
    /// The walk expands every assembly the solution ships, wherever it was loaded from, and stops only at the .NET
    /// shared framework: the framework's own references (it reaches an HTTP stack and a pipe library internally) say
    /// nothing about this solution's purity, while the solution's own references always do. A reference that cannot be
    /// resolved is reported instead of being trusted, so an unknown edge fails the guard rather than passing it.
    /// </remarks>
    public static AssemblyClosure ReachableAssemblyNames(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var cache = new Dictionary<string, Assembly?>(StringComparer.Ordinal);
        var root = assembly.GetName().Name
            ?? throw new ArgumentException("assembly has no simple name", nameof(assembly));

        return ReachableAssemblyNames(
            root,
            name => Load(name) is { } loaded
                && !StopsAtSharedFramework(loaded.Location, runtimeDirectory)
                ? [.. loaded.GetReferencedAssemblies().Select(reference => reference.Name).OfType<string>()]
                : [],
            name => Load(name) is not null);

        Assembly? Load(string name)
        {
            if (!cache.TryGetValue(name, out var known))
            {
                known = TryLoad(name);
                cache[name] = known;
            }

            return known;
        }
    }

    /// <summary>
    /// True when the walk must not expand an assembly loaded from <paramref name="location"/>: it belongs to the
    /// .NET shared framework, so its references are the framework's own graph. A solution assembly keeps the walk
    /// going whatever directory it was loaded from.
    /// </summary>
    public static bool StopsAtSharedFramework(string location, string runtimeDirectory)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(runtimeDirectory);

        return location.Length == 0
            || (runtimeDirectory.Length > 0
                && location.StartsWith(runtimeDirectory, StringComparison.Ordinal));
    }

    /// <summary>The reachable closure of <paramref name="root"/> over an explicit reference graph.</summary>
    /// <param name="root">The assembly the walk starts from.</param>
    /// <param name="referencedAssemblies">
    /// The references of one assembly. Return no names for an assembly whose own references stop the walk.
    /// </param>
    public static IReadOnlyList<string> ReachableAssemblyNames(
        string root,
        Func<string, IReadOnlyList<string>> referencedAssemblies) =>
        ReachableAssemblyNames(root, referencedAssemblies, _ => true).Reachable;

    /// <summary>
    /// The reachable closure of <paramref name="root"/> over an explicit reference graph, recording every name the
    /// caller cannot resolve. An unresolved name stays in the closure, so the guard reports it, and is never expanded.
    /// </summary>
    /// <param name="root">The assembly the walk starts from.</param>
    /// <param name="referencedAssemblies">The references of one assembly.</param>
    /// <param name="resolvable">Whether the caller can load the assembly, and so read its own references.</param>
    public static AssemblyClosure ReachableAssemblyNames(
        string root,
        Func<string, IReadOnlyList<string>> referencedAssemblies,
        Func<string, bool> resolvable)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(referencedAssemblies);
        ArgumentNullException.ThrowIfNull(resolvable);

        var unresolved = new List<string>();

        var reachable = ReachableAssemblyNamesCore(root, name =>
        {
            if (resolvable(name))
            {
                return referencedAssemblies(name);
            }

            unresolved.Add(name);
            return [];
        });

        return new AssemblyClosure(reachable, [.. unresolved.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    private static IReadOnlyList<string> ReachableAssemblyNamesCore(
        string root,
        Func<string, IReadOnlyList<string>> referencedAssemblies)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { root };
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            foreach (var reference in referencedAssemblies(queue.Dequeue()))
            {
                if (visited.Add(reference))
                {
                    queue.Enqueue(reference);
                }
            }
        }

        return [.. visited.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The evaluator is a pure function: its assembly may reach a module's <c>.Contracts</c> and the framework
    /// primitives, never a database driver, an HTTP stack, an Infrastructure assembly or another module's
    /// implementation. It is applied to the reachable closure, so a reference that arrives through a contracts
    /// assembly is still a violation.
    /// </summary>
    public static IReadOnlyList<string> EvaluatorStaysPure(IEnumerable<string> referencedAssemblies)
    {
        ArgumentNullException.ThrowIfNull(referencedAssemblies);

        var violations = new List<string>();

        foreach (var name in referencedAssemblies.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
        {
            if (ImpureAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                violations.Add($"Evidgate.Evaluation must not reach {name}");
            }
            else if (name.Contains(".Infrastructure", StringComparison.Ordinal))
            {
                violations.Add($"Evidgate.Evaluation must not reach infrastructure assembly {name}");
            }
            else if (name.StartsWith("Evidgate.Modules.", StringComparison.Ordinal)
                && !name.EndsWith(".Contracts", StringComparison.Ordinal))
            {
                violations.Add($"Evidgate.Evaluation must reference module contracts only, not {name}");
            }
        }

        return violations;
    }

    /// <summary>
    /// The evaluator reaches nothing impure, and every reachable reference is known: a reference the walk could not
    /// resolve is a violation too, because an unknown edge cannot be proven pure.
    /// </summary>
    public static IReadOnlyList<string> EvaluatorStaysPure(AssemblyClosure closure)
    {
        ArgumentNullException.ThrowIfNull(closure);

        return
        [
            .. EvaluatorStaysPure(closure.Reachable)
                .Concat(closure.Unresolved.Select(name =>
                    $"Evidgate.Evaluation reaches {name}, which could not be resolved, so its purity is unproven"))
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>Source files that disable the banned-API diagnostic instead of respecting it.</summary>
    /// <param name="sources">One entry per file: the path to report and its text.</param>
    public static IReadOnlyList<string> BannedApiSuppressions(IReadOnlyDictionary<string, string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        return
        [
            .. sources
                .Where(entry => Disables(entry.Value, "RS0030"))
                .Select(entry => $"{entry.Key} disables the banned-API diagnostic RS0030")
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// The static fields of <paramref name="types"/> a caller can change: not a constant held by an immutable slot,
    /// or a constant-free static holding an array or a mutable collection. Shared state like that is what makes a
    /// repeated evaluation differ from the first one. Compiler-generated closure holders only cache stateless
    /// delegates, which no caller can change, so they are skipped.
    /// </summary>
    public static IReadOnlyList<string> StaticMutableFields(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        var violations = new List<string>();

        foreach (var type in Enumerate(types).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            // The compiler marks its closure, lambda and iterator holders this way, and those only cache stateless
            // delegates. A hand-applied attribute on a normally named type must not buy the same exemption.
            if (type.Name.Contains("<>", StringComparison.Ordinal)
                && type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false))
            {
                continue;
            }

            var fields = type
                .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .OrderBy(field => field.Name, StringComparer.Ordinal);

            foreach (var field in fields)
            {
                if (field.IsLiteral)
                {
                    continue;
                }

                if (!field.IsInitOnly || IsMutableCollection(field.FieldType))
                {
                    violations.Add($"{type.FullName}.{field.Name} is a static mutable field");
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// True when the text disables the given diagnostic: a <c>#pragma warning disable</c> whose diagnostic list names
    /// it, or a suppression attribute whose identifier names it, with or without the optional title after a colon.
    /// </summary>
    public static bool Disables(string text, string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(diagnostic);

        return DisablesByPragma(text, diagnostic) || DisablesByAttribute(text, diagnostic);
    }

    /// <summary>Every <c>#pragma warning disable</c> line, split into its diagnostic entries.</summary>
    private static bool DisablesByPragma(string text, string diagnostic)
    {
        for (var index = text.IndexOf('#');
             index >= 0;
             index = text.IndexOf('#', index + 1))
        {
            var lineEnd = text.IndexOf('\n', index);
            var line = lineEnd < 0 ? text[index..] : text[index..lineEnd];
            var lineComment = line.IndexOf("//", StringComparison.Ordinal);
            var blockComment = line.IndexOf("/*", StringComparison.Ordinal);
            var cut = lineComment < 0 ? blockComment : blockComment < 0 ? lineComment : Math.Min(lineComment, blockComment);
            if (cut >= 0)
            {
                line = line[..cut];
            }

            var entries = line.Split([' ', '\t', '\r', ','], StringSplitOptions.RemoveEmptyEntries);

            var pragma = Array.FindIndex(entries, entry => entry.TrimStart('#') == "pragma");
            if (pragma < 0
                || entries.Length < pragma + 3
                || entries[pragma + 1] != "warning"
                || entries[pragma + 2] != "disable")
            {
                continue;
            }

            var listed = entries.Skip(pragma + 3).ToArray();

            // A directive with no diagnostic list disables every warning, so it disables this one too.
            if (listed.Length == 0 || listed.Contains(diagnostic, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DisablesByAttribute(string text, string diagnostic) =>
        text.Contains("SuppressMessage", StringComparison.Ordinal)
        && (text.Contains($"\"{diagnostic}\"", StringComparison.Ordinal)
            || text.Contains($"\"{diagnostic}:", StringComparison.Ordinal));

    /// <summary>True for an array or one of the mutable generic collections the framework ships.</summary>
    private static bool IsMutableCollection(Type type)
    {
        if (type.IsArray)
        {
            return true;
        }

        if (!type.IsGenericType)
        {
            return false;
        }

        var definition = type.GetGenericTypeDefinition();

        return definition == typeof(List<>)
            || definition == typeof(Dictionary<,>)
            || definition == typeof(HashSet<>)
            || definition == typeof(SortedSet<>)
            || definition == typeof(SortedDictionary<,>)
            || definition == typeof(LinkedList<>)
            || definition == typeof(Queue<>)
            || definition == typeof(Stack<>);
    }

    private static Assembly? TryLoad(string name)
    {
        try
        {
            return Assembly.Load(new AssemblyName(name));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (FileLoadException)
        {
            return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static IEnumerable<Type> Flatten(Type? type)
    {
        if (type is null)
        {
            yield break;
        }

        yield return type;

        foreach (var arg in type.GenericTypeArguments)
        {
            foreach (var t in Flatten(arg))
            {
                yield return t;
            }
        }
    }
}
