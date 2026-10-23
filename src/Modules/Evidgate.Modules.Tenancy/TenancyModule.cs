using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Tenancy.Application.Commands;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Tenancy.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Tenancy;

/// <summary>
/// Composition seam of the Tenancy module (00 §5.5): the org/project write model, the
/// commands' shape validators and the <see cref="ITenantLimits"/> read contract of
/// 02.1.1-T3. Mediator's source generator registers the handlers themselves.
/// </summary>
internal static class TenancyModule
{
    /// <summary>Registers the module's services.</summary>
    public static IServiceCollection AddTenancyModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 02.1.1-T2: the EF write model of organization/project (00 §5.5 C4).
        services.AddEvidgateDbContext<TenancyDbContext>(typeof(TenancyDbContext).Assembly);

        // C2 stage 2: a validator that is not registered never runs, so each command's
        // is registered explicitly; TryAddEnumerable keeps a test's replacement.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidator<CreateOrganizationCommand>, CreateOrganizationCommandValidator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidator<UpdateOrganizationCommand>, UpdateOrganizationCommandValidator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidator<CreateProjectCommand>, CreateProjectCommandValidator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidator<UpdateProjectCommand>, UpdateProjectCommandValidator>());

        // 02.1.1-T3.1: the limit surface plan 12 reads; stateless over the ambient scope.
        services.TryAddSingleton<ITenantLimits, TenantLimits>();

        return services;
    }
}
