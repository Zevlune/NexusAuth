namespace NexusAuth.Shared.DependencyInjection;

/// <summary>Identifies a scoped implementation by its service contract and unique key.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class KeyedServiceAttribute(Type serviceType, string serviceKey) : Attribute
{
    public Type ServiceType { get; } = serviceType;
    public string ServiceKey { get; } = serviceKey;
}
