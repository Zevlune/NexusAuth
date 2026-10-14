using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NexusAuth.Host.Controllers;
using NexusAuth.Workbench.Api.Controllers;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class RequestModelValidationTests
{
    public static IEnumerable<object[]> RequestModels() =>
        new[] { typeof(UsersController).Assembly, typeof(ScimController).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) || typeof(PageModel).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.IsDefined(typeof(FromBodyAttribute)))
            .Select(parameter => parameter.ParameterType)
            .Distinct()
            .OrderBy(type => type.FullName)
            .Select(type => new object[] { type });

    [Theory]
    [MemberData(nameof(RequestModels))]
    public void Http_request_models_can_be_validated_without_metadata_exceptions(Type requestType)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        using var provider = services.BuildServiceProvider();
        var model = JsonSerializer.Deserialize("{}", requestType);
        Assert.NotNull(model);
        var context = new ActionContext(
            new DefaultHttpContext { RequestServices = provider }, new RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor(), new ModelStateDictionary());

        // Invalid field values may add model errors, but invalid record metadata must never throw.
        provider.GetRequiredService<IObjectModelValidator>().Validate(context, null, requestType.Name, model);
    }
}
