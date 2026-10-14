using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace NexusAuth.Host.Swagger;

/// <summary>
/// 让 SCIM 2.0 endpoints 在 OpenAPI 文档中显式使用 application/scim+json，
/// 并仅对这些端点声明 Bearer security requirement。
/// </summary>
internal sealed class ScimDocumentFilter : IDocumentFilter
{
    private const string ScimPathPrefix = "/scim/v2";
    private const string ScimMediaType = "application/scim+json";

    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        if (document.Paths is null)
            return;

        foreach (var path in document.Paths)
        {
            if (path.Value.Operations is null
                || !path.Key.StartsWith(ScimPathPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var operation in path.Value.Operations)
            {
                operation.Value.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(SwaggerConfiguration.BearerSecuritySchemeName, document, null)] = []
                    }
                ];

                ApplyRequestMediaType(operation.Value);
                ApplyResponseMediaTypes(document, operation.Value);
            }
        }
    }

    private static void ApplyRequestMediaType(OpenApiOperation operation)
    {
        var requestBody = operation.RequestBody;
        if (requestBody?.Content is not { Count: > 0 } content)
            return;

        ReplaceJsonMediaType(content);
        AddScimExample(content, CreateUserExample());
    }

    private static void ApplyResponseMediaTypes(OpenApiDocument document, OpenApiOperation operation)
    {
        if (operation.Responses is not { Count: > 0 } responses)
            return;

        var hasContent = false;
        foreach (var response in responses.Values)
        {
            if (response.Content is { Count: > 0 } content)
            {
                ReplaceJsonMediaType(content);
                hasContent = true;
            }
        }

        if (!hasContent)
            return;

        var userSchema = new OpenApiSchemaReference("ScimUserResponse", document, null);
        foreach (var response in responses.Values)
        {
            if (response.Content is not { } content)
                continue;

            if (!content.ContainsKey(ScimMediaType))
            {
                var schema = FindJsonSchema(content) ?? userSchema;
                content[ScimMediaType] = new OpenApiMediaType { Schema = schema };
            }

            content[ScimMediaType].Example = CreateUserExample();
        }
    }

    private static void ReplaceJsonMediaType(IDictionary<string, OpenApiMediaType> content)
    {
        if (!content.TryGetValue("application/json", out var jsonMediaType))
            return;

        content[ScimMediaType] = new OpenApiMediaType
        {
            Schema = jsonMediaType.Schema,
            Encoding = jsonMediaType.Encoding,
            Extensions = jsonMediaType.Extensions
        };
    }

    private static void AddScimExample(IDictionary<string, OpenApiMediaType> content, JsonNode example)
    {
        if (!content.TryGetValue(ScimMediaType, out var scimMediaType))
        {
            scimMediaType = new OpenApiMediaType { Schema = FindJsonSchema(content) };
            content[ScimMediaType] = scimMediaType;
        }

        scimMediaType.Example = example;
    }

    private static IOpenApiSchema? FindJsonSchema(IDictionary<string, OpenApiMediaType> content) =>
        content.TryGetValue(ScimMediaType, out var scimMediaType) ? scimMediaType.Schema
        : content.TryGetValue("application/json", out var jsonMediaType) ? jsonMediaType.Schema
        : null;

    private static JsonNode CreateUserExample() => new JsonObject
    {
        ["schemas"] = new JsonArray(JsonValue.Create("urn:ietf:params:scim:schemas:core:2.0:User")),
        ["id"] = JsonValue.Create("4b3c2f70-3b3c-4a52-9f6d-6b0a3a2f9c1d"),
        ["externalId"] = JsonValue.Create("idp-1001"),
        ["userName"] = JsonValue.Create("alice"),
        ["active"] = JsonValue.Create(true),
        ["name"] = new JsonObject
        {
            ["givenName"] = JsonValue.Create("Alice"),
            ["familyName"] = JsonValue.Create("Zhang")
        },
        ["emails"] = new JsonArray(new JsonObject
        {
            ["value"] = JsonValue.Create("alice@example.com"),
            ["type"] = JsonValue.Create("work"),
            ["primary"] = JsonValue.Create(true)
        })
    };
}
