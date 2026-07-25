using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Services;
using SmartSwitch.Infrastructure.Logging;
using SmartSwitch.Infrastructure.Modules;
using SmartSwitch.Infrastructure.Network;
using SmartSwitch.Infrastructure.Packages;
using SmartSwitch.Infrastructure.PreOs;
using SmartSwitch.Infrastructure.PreOs;
using SmartSwitch.Infrastructure.SystemAccess;

namespace SmartSwitch.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSmartSwitch(
        this IServiceCollection services,
        params Assembly[] additionalModuleAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IMigrationLogger, JsonFileMigrationLogger>();
        services.AddSingleton<INetworkInformationService, NetworkInformationService>();
        services.AddSingleton<INetworkTransferService, NetworkTransferService>();
        services.AddSingleton<IApplicationInventoryService, ApplicationInventoryService>();
        services.AddSingleton<IMigrationPackageService, MigrationPackageService>();
        services.AddSingleton<ISystemCompatibilityService, SystemCompatibilityService>();
        services.AddSingleton<IPrivilegeService, PrivilegeService>();
        services.AddSingleton<IPreOsPackageApplier, PreOsPackageApplier>();
        services.AddSingleton<IPreOsService, PreOsService>();
        services.AddSingleton<IPreOsPackageApplier, PreOsPackageApplier>();
        services.AddSingleton<IPreOsService, PreOsService>();

        var assemblies = additionalModuleAssemblies
            .Append(typeof(UserFilesMigrationModule).Assembly)
            .Distinct()
            .ToArray();
        var moduleTypes = assemblies
            .SelectMany(GetLoadableTypes)
            .Where(type =>
                type is { IsClass: true, IsAbstract: false } &&
                typeof(IMigrationModule).IsAssignableFrom(type))
            .Distinct()
            .ToArray();

        foreach (var moduleType in moduleTypes)
        {
            services.AddSingleton(typeof(IMigrationModule), moduleType);
        }

        services.AddSingleton<IMigrationEngine, MigrationEngine>();
        return services;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }
}
