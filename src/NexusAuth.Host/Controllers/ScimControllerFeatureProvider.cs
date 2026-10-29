using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace NexusAuth.Host.Controllers;

public sealed class ScimControllerFeatureProvider(IConfiguration configuration) : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        if (!configuration.GetValue("Scim:Enabled", true))
        {
            var controller = feature.Controllers.FirstOrDefault(type => type.AsType() == typeof(ScimController));
            if (controller is not null)
                feature.Controllers.Remove(controller);
        }
    }
}
