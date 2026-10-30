using System.Reflection;
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
