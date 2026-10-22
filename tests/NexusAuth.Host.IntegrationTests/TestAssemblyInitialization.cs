using System.Runtime.CompilerServices;
using Luck.Framework.Infrastructure;

namespace NexusAuth.Host.IntegrationTests;

internal static class TestAssemblyInitialization
{
    [ModuleInitializer]
    internal static void ExcludeTestDoublesFromAutomaticInjection()
    {
        AssemblyHelper.AddExcludeLibs(typeof(TestAssemblyInitialization).Assembly.GetName().Name!);
    }
}
