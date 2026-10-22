using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NexusAuth.Shared.DependencyInjection;

public static class KeyedServiceExtension
{
    /// <summary>Scans attributed implementations and registers them in the current service collection.</summary>
    public static IServiceCollection AddScopeKeyedServices(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        var registrations = assemblies.Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters)
            .SelectMany(type => type.GetCustomAttributes<KeyedServiceAttribute>(inherit: false)
                .Select(attribute => (ImplementationType: type, Attribute: attribute)))
            .ToArray();
        var registeredKeys = new HashSet<(Type ServiceType, string ServiceKey)>();

        // Validate the whole scan before changing registrations. A duplicate must fail at startup.
        foreach (var (implementationType, attribute) in registrations)
        {
            if (string.IsNullOrWhiteSpace(attribute.ServiceKey))
                throw new InvalidOperationException($"{implementationType.FullName} must specify a nonempty service key.");
            if (!attribute.ServiceType.IsAssignableFrom(implementationType))
                throw new InvalidOperationException($"{implementationType.FullName} does not implement {attribute.ServiceType.FullName}.");
            if (!registeredKeys.Add((attribute.ServiceType, attribute.ServiceKey))
                || services.Any(descriptor => descriptor.IsKeyedService
                    && descriptor.ServiceType == attribute.ServiceType
                    && Equals(descriptor.ServiceKey, attribute.ServiceKey)))
                throw new InvalidOperationException($"Duplicate service key '{attribute.ServiceKey}' for {attribute.ServiceType.FullName}.");
        }

        foreach (var (implementationType, attribute) in registrations)
        {
            services.TryAdd(ServiceDescriptor.Scoped(implementationType, implementationType));
            services.AddKeyedScoped(attribute.ServiceType, attribute.ServiceKey,
                (provider, _) => provider.GetRequiredService(implementationType));
        }

        return services;
    }

    public static TService? ResolveKeyed<TService>(this IServiceProvider provider, string serviceKey) where TService : class
        => provider.GetKeyedService<TService>(serviceKey);
}
