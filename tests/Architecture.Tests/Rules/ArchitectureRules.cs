using System.Reflection;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Mediator;

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

    // Mediator 3.0.2 handler interfaces (Mediator.Abstractions). Authorization is
    // a pipeline concern for request-scoped work (00 §5.5 C2, §5.7 S2): command,
    // query, request and stream handlers. INotificationHandler is deliberately
    // excluded — published notifications are not request-scoped under C2, and
    // integration events go through the outbox (C7), not user authorization.
    private static readonly string[] HandlerInterfaceNames =
    [
        "ICommandHandler",
        "IQueryHandler",
        "IRequestHandler",
        "IStreamCommandHandler",
        "IStreamQueryHandler",
        "IStreamRequestHandler",
    ];

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
    public static IReadOnlyList<string> EveryHandlerDeclaresPermission(IEnumerable<Type> types)
    {
        var violations = new List<string>();

        foreach (var type in Enumerate(types).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.IsAbstract || type.IsInterface)
            {
                continue;
            }

            var implementsHandler = type
                .GetInterfaces()
                .Any(i => i.Namespace == "Mediator"
                    && HandlerInterfaceNames.Contains(i.Name.Split('`')[0]));

            if (!implementsHandler)
            {
                continue;
            }

            var permissionAttribute = type
                .GetCustomAttributesData()
                .SingleOrDefault(a => a.AttributeType.FullName == typeof(PermissionAttribute).FullName);

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

    /// <summary>
    /// AC4 / 00 §5.5 C8 + 01.4.2-T4: every Mediator request handler must answer
    /// <c>Result</c>/<c>Result&lt;T&gt;</c>. The C2 pipeline's tenant-bound stages
    /// (tenant transaction, authorization, idempotency) run for messages carrying
    /// that contract, so a handler returning a bare DTO would silently run outside
    /// the tenant scope. The sprint-1 skeleton slice (01.1.2-T3) is the pinned
    /// exception: 01.1.2 §8 records that its routes carry no tenant scope and no
    /// permission, and 04/05 replace it with Result-carrying commands.
    /// </summary>
    /// <param name="types">Types to check (the real solution, or fixtures).</param>
    /// <param name="exemptHandlerTypeNames">Full names exempted as the pinned skeleton slice.</param>
    public static IReadOnlyList<string> EveryPipelineHandlerReturnsResult(
        IEnumerable<Type> types,
        IReadOnlyCollection<string> exemptHandlerTypeNames)
    {
        ArgumentNullException.ThrowIfNull(exemptHandlerTypeNames);

        var violations = new List<string>();

        foreach (var type in Enumerate(types).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.IsAbstract || type.IsInterface || exemptHandlerTypeNames.Contains(type.FullName))
            {
                continue;
            }

            foreach (var face in type.GetInterfaces())
            {
                if (!face.IsGenericType)
                {
                    continue;
                }

                var definition = face.GetGenericTypeDefinition();
                var response = definition == typeof(ICommandHandler<>) || definition == typeof(IRequestHandler<>)
                    ? typeof(Unit)
                    : definition == typeof(ICommandHandler<,>)
                        || definition == typeof(IQueryHandler<,>)
                        || definition == typeof(IRequestHandler<,>)
                            ? face.GetGenericArguments()[1]
                            : null;

                if (response is not null && !typeof(Result).IsAssignableFrom(response))
                {
                    violations.Add(
                        $"{type.FullName} handles a Mediator request but returns {response.Name}: a pipeline handler " +
                        "must answer Result/Result<T> (00 §5.5 C8) so the C2 tenant stages run for it");
                }
            }
        }

        return violations.Distinct(StringComparer.Ordinal).ToList();
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
