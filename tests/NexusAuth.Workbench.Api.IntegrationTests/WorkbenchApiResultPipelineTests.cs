using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.ApiResources;
using NexusAuth.Workbench.Api.Filters;
using Npgsql;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchApiResultPipelineTests
{
    [Fact]
    public async Task Duplicate_resource_preserves_the_main_business_error_code()
    {
        using var host = await StartHostAsync();
        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/api/workbench-api-result-tests/duplicate-resource");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        Assert.Equal("ApiResourceAlreadyExists", document.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Successful_object_result_keeps_the_compatible_envelope()
    {
        using var host = await StartHostAsync();
        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/api/workbench-api-result-tests/success");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("ok", root.GetProperty("result").GetProperty("value").GetString());
    }

    [Fact]
    public async Task No_content_remains_204_without_a_body()
    {
        using var host = await StartHostAsync();
        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/api/workbench-api-result-tests/no-content");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("unauthorized-result", HttpStatusCode.Unauthorized, "Authentication is required.")]
    [InlineData("forbid", HttpStatusCode.Forbidden, "You do not have permission to perform this action.")]
    [InlineData("bad-request-result", HttpStatusCode.BadRequest, "The request is invalid.")]
    [InlineData("not-found-result", HttpStatusCode.NotFound, "The requested resource was not found.")]
    [InlineData("conflict-result", HttpStatusCode.Conflict, "The request conflicts with the current state.")]
    public async Task Action_results_preserve_their_http_status_and_return_an_error_envelope(
        string route,
        HttpStatusCode expectedStatus,
        string expectedMessage)
    {
        using var host = await StartHostAsync();
        using var client = host.GetTestClient();
        using var response = await client.GetAsync($"/api/workbench-api-result-tests/{route}");

        await AssertErrorEnvelopeAsync(response, expectedStatus, expectedMessage);
    }

    [Theory]
    [InlineData("argument-exception", HttpStatusCode.BadRequest, "The submitted value is invalid.")]
    [InlineData("key-not-found", HttpStatusCode.NotFound, "The requested item does not exist.")]
    [InlineData("business-conflict", HttpStatusCode.Conflict, "The item is already in use.")]
    public async Task Known_service_exceptions_map_to_safe_http_errors(
        string route,
        HttpStatusCode expectedStatus,
        string expectedMessage)
    {
        using var host = await StartHostAsync();
        using var client = host.GetTestClient();
        using var response = await client.GetAsync($"/api/workbench-api-result-tests/{route}");

        await AssertErrorEnvelopeAsync(response, expectedStatus, expectedMessage);
    }

    [Fact]
    public async Task Database_conflicts_return_409_without_database_details()
    {
        using var host = await StartHostAsync();
        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/api/workbench-api-result-tests/database-conflict");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("duplicate key", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive-sql", body, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(body);
        AssertErrorEnvelope(
            document.RootElement,
            "The requested change conflicts with an existing record.");
    }

    [Fact]
    public async Task Non_unique_database_failures_return_safe_500()
    {
        using var host = await StartHostAsync();
        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/api/workbench-api-result-tests/database-failure");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("database unavailable", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive-sql", body, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(body);
        AssertErrorEnvelope(document.RootElement, "An unexpected error occurred. Please try again later.");
    }

    [Fact]
    public async Task Database_concurrency_conflicts_return_safe_409()
    {
        using var host = await StartHostAsync();
        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/api/workbench-api-result-tests/concurrency-conflict");

        await AssertErrorEnvelopeAsync(
            response,
            HttpStatusCode.Conflict,
            "The requested change conflicts with an existing record.");
    }

    [Fact]
    public async Task Unknown_invalid_operation_exceptions_return_safe_500()
    {
        using var host = await StartHostAsync("Staging");
        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/api/workbench-api-result-tests/invalid-operation");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("internal framework detail", body, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(body);
        AssertErrorEnvelope(document.RootElement, "An unexpected error occurred. Please try again later.");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public async Task Unexpected_exceptions_return_500_without_leaking_details(string environment)
    {
        using var host = await StartHostAsync(environment);
        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/api/workbench-api-result-tests/unexpected-exception");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("unexpected-secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(body);
        AssertErrorEnvelope(document.RootElement, "An unexpected error occurred. Please try again later.");
    }

    private static async Task<IHost> StartHostAsync(string environment = "Testing")
    {
        var host = await new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureWebHost(builder => builder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddControllers()
                        .AddApplicationPart(typeof(WorkbenchApiResultTestController).Assembly);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .StartAsync();

        return host;
    }

    private static async Task AssertErrorEnvelopeAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedMessage)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        AssertErrorEnvelope(document.RootElement, expectedMessage);
    }

    private static void AssertErrorEnvelope(JsonElement root, string expectedMessage)
    {
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(expectedMessage, root.GetProperty("message").GetString());
        Assert.Equal(expectedMessage, root.GetProperty("errorMessage").GetString());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}

[ApiController]
[WorkbenchApiResult]
[Route("api/workbench-api-result-tests")]
public sealed class WorkbenchApiResultTestController : ControllerBase
{
    [HttpGet("duplicate-resource")]
    public IActionResult DuplicateResource() => throw new ApiResourceAlreadyExistsException("duplicate");

    [HttpGet("success")]
    public IActionResult Success() => Ok(new { value = "ok" });

    [HttpGet("no-content")]
    public IActionResult NoContentResponse() => NoContent();

    [HttpGet("unauthorized-result")]
    public IActionResult UnauthorizedResponse() => Unauthorized();

    [HttpGet("forbid")]
    public IActionResult ForbiddenResponse() => Forbid();

    [HttpGet("bad-request-result")]
    public IActionResult BadRequestResponse() => BadRequest();

    [HttpGet("not-found-result")]
    public IActionResult NotFoundResponse() => NotFound();

    [HttpGet("conflict-result")]
    public IActionResult ConflictResponse() => Conflict();

    [HttpGet("argument-exception")]
    public IActionResult ArgumentExceptionResponse() =>
        throw new ArgumentException("The submitted value is invalid.");

    [HttpGet("key-not-found")]
    public IActionResult KeyNotFoundExceptionResponse() =>
        throw new KeyNotFoundException("The requested item does not exist.");

    [HttpGet("business-conflict")]
    public IActionResult BusinessConflictResponse() =>
        throw new WorkbenchBusinessConflictException("The item is already in use.");

    [HttpGet("invalid-operation")]
    public IActionResult InvalidOperationExceptionResponse() =>
        throw new InvalidOperationException("internal framework detail");

    [HttpGet("database-conflict")]
    public IActionResult DatabaseConflictResponse() =>
        throw new DbUpdateException(
            "duplicate key sensitive-sql",
            new PostgresException("duplicate key sensitive-sql", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));

    [HttpGet("database-failure")]
    public IActionResult DatabaseFailureResponse() =>
        throw new DbUpdateException("database unavailable sensitive-sql");

    [HttpGet("concurrency-conflict")]
    public IActionResult DatabaseConcurrencyConflictResponse() =>
        throw new DbUpdateConcurrencyException("concurrency sensitive-sql");

    [HttpGet("unexpected-exception")]
    public IActionResult UnexpectedExceptionResponse() =>
        throw new InvalidCastException("unexpected-secret");
}
