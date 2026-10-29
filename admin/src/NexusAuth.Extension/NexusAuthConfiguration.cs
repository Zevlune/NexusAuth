using System.Net;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace NexusAuth.Extension;

internal sealed class NexusAuthConfigurationRetriever(NexusAuthClientOptions options)
    : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address,
        IDocumentRetriever retriever,
        CancellationToken ct)
    {
        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await OpenIdConnectConfigurationRetriever.GetAsync(address, retriever, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NexusAuthProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (IsInvalidMetadataException(exception))
        {
            throw NexusAuthProtocolResponse.InvalidResponse(HttpStatusCode.OK, exception);
        }

        var error = NexusAuthConfigurationValidation.ValidateConfiguration(configuration, options);
        if (error is not null)
            throw new InvalidOperationException(error);

        return configuration;
    }

    private static bool IsInvalidMetadataException(Exception exception) =>
        exception is JsonException
            or FormatException
            or InvalidDataException
            or NullReferenceException
            or ArgumentException;
}

internal sealed class NexusAuthDocumentRetriever(
    IHttpClientFactory httpClientFactory,
    NexusAuthClientOptions options) : IDocumentRetriever
{
    public async Task<string> GetDocumentAsync(string address, CancellationToken ct)
    {
        if (!NexusAuthConfigurationValidation.IsAbsoluteHttpUri(
                address,
                options.RequireHttpsMetadata,
                allowQuery: true))
        {
            throw new InvalidOperationException(
                "Provider metadata endpoints must be absolute HTTPS URIs without user info or fragments.");
        }

        using var client = httpClientFactory.CreateClient(NexusAuthClient.HttpClientName);
        using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw NexusAuthProtocolResponse.FromResponse(body, response.StatusCode);

        return body;
    }
}
