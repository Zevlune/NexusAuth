using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using NexusAuth.Shared.DependencyInjection;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public class KeyedServiceExtensionTests
{
    [Fact]
    public void Scan_registers_all_keys_and_contracts_with_one_instance_per_scope()
    {
        var assembly = Implementation((typeof(IKeyedServiceTestMarker), "first"), (typeof(IKeyedServiceTestMarker), "alias"), (typeof(ISecondKeyedServiceTestMarker), "first"));
        var services = new ServiceCollection().AddScopeKeyedServices(assembly);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.ResolveKeyed<IKeyedServiceTestMarker>("first");
        Assert.NotNull(service);
        Assert.Same(service, scope.ServiceProvider.ResolveKeyed<IKeyedServiceTestMarker>("alias"));
        Assert.Same(service, scope.ServiceProvider.ResolveKeyed<ISecondKeyedServiceTestMarker>("first"));
        Assert.Null(scope.ServiceProvider.ResolveKeyed<IKeyedServiceTestMarker>("FIRST"));
        Assert.Null(scope.ServiceProvider.ResolveKeyed<IKeyedServiceTestMarker>("unknown"));
        using var otherScope = provider.CreateScope();
        Assert.NotSame(service, otherScope.ServiceProvider.ResolveKeyed<IKeyedServiceTestMarker>("first"));
        Assert.Throws<InvalidOperationException>(() => provider.ResolveKeyed<IKeyedServiceTestMarker>("first"));
    }

    [Fact]
    public void Registration_is_local_to_each_service_collection()
    {
        var assembly = Implementation((typeof(IKeyedServiceTestMarker), "same-key"));
        using var firstProvider = new ServiceCollection().AddScopeKeyedServices(assembly).BuildServiceProvider();
        using var secondProvider = new ServiceCollection().AddScopeKeyedServices(assembly).BuildServiceProvider();
        Assert.NotNull(firstProvider.ResolveKeyed<IKeyedServiceTestMarker>("same-key"));
        Assert.NotNull(secondProvider.ResolveKeyed<IKeyedServiceTestMarker>("same-key"));
        Assert.NotSame(firstProvider.ResolveKeyed<IKeyedServiceTestMarker>("same-key"), secondProvider.ResolveKeyed<IKeyedServiceTestMarker>("same-key"));
    }

    [Fact]
    public void Duplicate_contract_key_is_rejected_before_any_registration_changes()
    {
        var services = new ServiceCollection();
        var first = Implementation((typeof(IKeyedServiceTestMarker), "duplicate"));
        var second = Implementation((typeof(IKeyedServiceTestMarker), "duplicate"));
        var error = Assert.Throws<InvalidOperationException>(() => services.AddScopeKeyedServices(first, second));
        Assert.Contains("duplicate", error.Message);
        Assert.Contains(nameof(IKeyedServiceTestMarker), error.Message);
        Assert.Empty(services);
    }

    [Fact]
    public void Duplicate_existing_key_is_rejected()
    {
        var assembly = Implementation((typeof(IKeyedServiceTestMarker), "existing"));
        var services = new ServiceCollection().AddScopeKeyedServices(assembly);
        var count = services.Count;
        Assert.Throws<InvalidOperationException>(() => services.AddScopeKeyedServices(assembly));
        Assert.Equal(count, services.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_keys_are_rejected(string key)
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddScopeKeyedServices(Implementation((typeof(IKeyedServiceTestMarker), key))));
        Assert.Empty(services);
    }

    [Fact]
    public void Attribute_contract_must_be_implemented()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddScopeKeyedServices(Implementation((typeof(IDisposable), "invalid"))));
        Assert.Empty(services);
    }

    private static Assembly Implementation(params (Type Contract, string Key)[] keys)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"KeyedServices_{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Services");
        var type = module.DefineType("Implementation", TypeAttributes.Public | TypeAttributes.Class);
        type.AddInterfaceImplementation(typeof(IKeyedServiceTestMarker));
        type.AddInterfaceImplementation(typeof(ISecondKeyedServiceTestMarker));
        type.DefineDefaultConstructor(MethodAttributes.Public);
        var constructor = typeof(KeyedServiceAttribute).GetConstructor([typeof(Type), typeof(string)])!;
        foreach (var (contract, key) in keys)
            type.SetCustomAttribute(new CustomAttributeBuilder(constructor, [contract, key]));
        type.CreateType();
        return assembly;
    }

}
