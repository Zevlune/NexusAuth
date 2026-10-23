using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusAuth.Application.Services.Scim;
using NexusAuth.Application.Services.Sessions;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class ScimSecurityTests : IClassFixture<ScimSecurityFactory>
{
    private const string ReadWriteToken = "integration-read-write-token";
    private const string ReadOnlyToken = "integration-read-only-token";
    private const string ErrorSchema = "urn:ietf:params:scim:api:messages:2.0:Error";
    private readonly ScimSecurityFactory factory;

    public ScimSecurityTests(ScimSecurityFactory factory)
    {
        this.factory = factory;
        factory.Users.Reset();
        factory.Revocations.Clear();
    }

    [Fact]
    public async Task System_account_is_hidden_and_cannot_be_modified_or_deleted()
    {
        var admin = User.Create("admin", "Test-password-only-123!", "Administrator");
        admin.MarkAsSystemAccount();
        await factory.Users.AddAsync(admin);
        using var client = CreateClient(ReadWriteToken);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/scim/v2/Users/{admin.Id}")).StatusCode);
        using (var list = await ReadJsonAsync(await client.GetAsync("/scim/v2/Users?filter=userName%20eq%20%22admin%22")))
            Assert.Equal(0, list.RootElement.GetProperty("totalResults").GetInt32());
        using (var list = await ReadJsonAsync(await client.GetAsync("/scim/v2/Users")))
            Assert.Equal(0, list.RootElement.GetProperty("totalResults").GetInt32());

        var delete = await client.SendAsync(ScimRequest(HttpMethod.Delete, $"/scim/v2/Users/{admin.Id}", null));
        await AssertScimErrorAsync(delete, HttpStatusCode.Forbidden, null);
        var replace = await client.SendAsync(ScimRequest(HttpMethod.Put, $"/scim/v2/Users/{admin.Id}", UserJson("admin", active: true)));
        await AssertScimErrorAsync(replace, HttpStatusCode.Forbidden, null);
        var patch = await client.SendAsync(ScimRequest(HttpMethod.Patch, $"/scim/v2/Users/{admin.Id}", PatchJson("""{"op":"replace","path":"active","value":false}""")));
        await AssertScimErrorAsync(patch, HttpStatusCode.Forbidden, null);

        var stored = Assert.Single(factory.Users.Users);
        Assert.Same(admin, stored);
        Assert.True(stored.IsActive);
        Assert.Null(stored.TokenInvalidBefore);
        Assert.Empty(factory.Revocations);
    }

    [Fact]
    public async Task Replace_without_active_keeps_a_disabled_user_disabled_and_remove_active_disables()
    {
        using var client = CreateClient(ReadWriteToken);
        var id = await CreateUserAsync(client, UserJson("bob", active: true));

        (await client.SendAsync(ScimRequest(HttpMethod.Patch, $"/scim/v2/Users/{id}", PatchJson("""{"op":"replace","path":"active","value":false}""")))).EnsureSuccessStatusCode();
        var replace = await client.SendAsync(ScimRequest(HttpMethod.Put, $"/scim/v2/Users/{id}", UserJson("bob", active: null)));
        replace.EnsureSuccessStatusCode();
        using (var replaced = await ReadJsonAsync(replace))
            Assert.False(replaced.RootElement.GetProperty("active").GetBoolean());
        Assert.False(FindUser(id).IsActive);

        (await client.SendAsync(ScimRequest(HttpMethod.Patch, $"/scim/v2/Users/{id}", PatchJson("""{"op":"replace","path":"active","value":true}""")))).EnsureSuccessStatusCode();
        Assert.True(FindUser(id).IsActive);
        var remove = await client.SendAsync(ScimRequest(HttpMethod.Patch, $"/scim/v2/Users/{id}", PatchJson("""{"op":"remove","path":"active"}""")));
        remove.EnsureSuccessStatusCode();
        Assert.False(FindUser(id).IsActive);
        Assert.Contains(("session", id), factory.Revocations);
    }

    [Fact]
    public async Task Create_with_active_false_creates_an_inactive_user()
    {
        using var client = CreateClient(ReadWriteToken);

        var id = await CreateUserAsync(client, UserJson("inactive", active: false));

        Assert.False(FindUser(id).IsActive);
    }

    [Theory]
    [InlineData("active eq \"yes\"")]
    [InlineData("userName eq \"\"")]
    [InlineData("externalId eq \"  \"")]
    [InlineData("emails.value eq \"\"")]
    public async Task Invalid_filters_return_a_scim_invalid_filter_error(string filter)
    {
        using var client = CreateClient(ReadWriteToken);
        await CreateUserAsync(client, UserJson("visible", active: true));

        var response = await client.GetAsync($"/scim/v2/Users?filter={Uri.EscapeDataString(filter)}");

        await AssertScimErrorAsync(response, HttpStatusCode.BadRequest, "invalidFilter");
    }

    [Fact]
    public async Task Boolean_filter_values_are_accepted()
    {
        using var client = CreateClient(ReadWriteToken);
        await CreateUserAsync(client, UserJson("active.user", active: true));

        using var list = await ReadJsonAsync(await client.GetAsync("/scim/v2/Users?filter=active%20eq%20%22true%22"));

        Assert.Equal(1, list.RootElement.GetProperty("totalResults").GetInt32());
    }

    [Fact]
    public async Task Null_multi_valued_elements_are_rejected()
    {
        using var client = CreateClient(ReadWriteToken);

        var create = await client.SendAsync(ScimRequest(HttpMethod.Post, "/scim/v2/Users", """{"userName":"nullmail","emails":[null]}"""));
        await AssertScimErrorAsync(create, HttpStatusCode.BadRequest, "invalidValue");

        var id = await CreateUserAsync(client, UserJson("patch.null", active: true, email: "patch.null@example.com"));
        var patch = await client.SendAsync(ScimRequest(HttpMethod.Patch, $"/scim/v2/Users/{id}", PatchJson("""{"op":"replace","value":{"emails":[null]}}""")));
        await AssertScimErrorAsync(patch, HttpStatusCode.BadRequest, "invalidValue");
        Assert.Equal("patch.null@example.com", FindUser(id).Email);
    }

    [Fact]
    public async Task Duplicate_phone_number_and_normalized_email_return_uniqueness_errors()
    {
        using var client = CreateClient(ReadWriteToken);
        await CreateUserAsync(client, UserJson("first", active: true, email: "dup@example.com", phone: "+8613800000000"));

        var phone = await client.SendAsync(ScimRequest(HttpMethod.Post, "/scim/v2/Users", UserJson("second", active: true, phone: " +8613800000000 ")));
        await AssertScimErrorAsync(phone, HttpStatusCode.Conflict, "uniqueness");
        var email = await client.SendAsync(ScimRequest(HttpMethod.Post, "/scim/v2/Users", UserJson("third", active: true, email: "  DUP@example.com ")));
        await AssertScimErrorAsync(email, HttpStatusCode.Conflict, "uniqueness");
        Assert.Single(factory.Users.Users);
    }

    [Fact]
    public async Task Over_length_values_return_invalid_value_instead_of_a_database_error()
    {
        using var client = CreateClient(ReadWriteToken);

        var phone = await client.SendAsync(ScimRequest(HttpMethod.Post, "/scim/v2/Users", UserJson("long.phone", active: true, phone: new string('1', 21))));
        await AssertScimErrorAsync(phone, HttpStatusCode.BadRequest, "invalidValue");
        var userName = await client.SendAsync(ScimRequest(HttpMethod.Post, "/scim/v2/Users", UserJson(new string('u', 101), active: true)));
        await AssertScimErrorAsync(userName, HttpStatusCode.BadRequest, "invalidValue");

        var id = await CreateUserAsync(client, UserJson("long.title", active: true));
        var title = await client.SendAsync(ScimRequest(HttpMethod.Patch, $"/scim/v2/Users/{id}",
            PatchJson($$"""{"op":"replace","path":"title","value":"{{new string('t', 257)}}"}""")));
        await AssertScimErrorAsync(title, HttpStatusCode.BadRequest, "invalidValue");
        Assert.Null(FindUser(id).Title);
    }

    [Fact]
    public async Task Missing_user_returns_404_and_only_an_etag_mismatch_returns_412()
    {
        using var client = CreateClient(ReadWriteToken);
        var missing = Guid.NewGuid();

        await AssertScimErrorAsync(await client.SendAsync(ScimRequest(HttpMethod.Put, $"/scim/v2/Users/{missing}", UserJson("ghost", active: true))), HttpStatusCode.NotFound, null);
        await AssertScimErrorAsync(await client.SendAsync(ScimRequest(HttpMethod.Patch, $"/scim/v2/Users/{missing}", PatchJson("""{"op":"replace","path":"title","value":"x"}"""))), HttpStatusCode.NotFound, null);
        await AssertScimErrorAsync(await client.SendAsync(ScimRequest(HttpMethod.Delete, $"/scim/v2/Users/{missing}", null)), HttpStatusCode.NotFound, null);

        var id = await CreateUserAsync(client, UserJson("etag.delete", active: true));
        var stale = await client.SendAsync(ScimRequest(HttpMethod.Delete, $"/scim/v2/Users/{id}", null, "\"1\""));
        await AssertScimErrorAsync(stale, HttpStatusCode.PreconditionFailed, null);
        Assert.Single(factory.Users.Users);
    }

    [Fact]
    public async Task Authentication_failures_return_401_with_a_bearer_challenge_and_missing_scope_returns_403()
    {
        using var anonymous = factory.CreateClient();
        var missing = await anonymous.SendAsync(ScimRequest(HttpMethod.Post, "/scim/v2/Users", UserJson("anon", active: true)));
        await AssertScimErrorAsync(missing, HttpStatusCode.Unauthorized, "invalidToken");
        Assert.Equal("Bearer", Assert.Single(missing.Headers.WwwAuthenticate).Scheme);

        using var invalid = CreateClient("not-a-valid-token");
        var rejected = await invalid.SendAsync(ScimRequest(HttpMethod.Delete, $"/scim/v2/Users/{Guid.NewGuid()}", null));
        await AssertScimErrorAsync(rejected, HttpStatusCode.Unauthorized, "invalidToken");
        Assert.Contains("invalid_token", Assert.Single(rejected.Headers.WwwAuthenticate).Parameter);

        using var readOnly = CreateClient(ReadOnlyToken);
        var forbidden = await readOnly.SendAsync(ScimRequest(HttpMethod.Post, "/scim/v2/Users", UserJson("ro", active: true)));
        await AssertScimErrorAsync(forbidden, HttpStatusCode.Forbidden, "forbidden");
        Assert.Contains("insufficient_scope", Assert.Single(forbidden.Headers.WwwAuthenticate).Parameter);
        Assert.Empty(factory.Users.Users);
    }

    [Fact]
    public async Task Changing_email_or_phone_invalidates_tokens_and_revokes_sessions()
    {
        using var client = CreateClient(ReadWriteToken);
        var id = await CreateUserAsync(client, UserJson("contact", active: true, email: "old@example.com"));

        (await client.SendAsync(ScimRequest(HttpMethod.Patch, $"/scim/v2/Users/{id}", PatchJson("""{"op":"replace","path":"title","value":"Engineer"}""")))).EnsureSuccessStatusCode();
        Assert.Null(FindUser(id).TokenInvalidBefore);
        Assert.Empty(factory.Revocations);

        (await client.SendAsync(ScimRequest(HttpMethod.Patch, $"/scim/v2/Users/{id}", PatchJson("""{"op":"replace","path":"emails.value","value":"new@example.com"}""")))).EnsureSuccessStatusCode();
        Assert.NotNull(FindUser(id).TokenInvalidBefore);
        Assert.Contains(("refresh", id), factory.Revocations);
        Assert.Contains(("session", id), factory.Revocations);

        factory.Revocations.Clear();
        var replace = await client.SendAsync(ScimRequest(HttpMethod.Put, $"/scim/v2/Users/{id}", UserJson("contact", active: true, email: "new@example.com", phone: "+8613900000000")));
        replace.EnsureSuccessStatusCode();
        Assert.Contains(("session", id), factory.Revocations);
    }

    [Fact]
    public async Task Delete_revokes_sessions_and_removes_the_user()
    {
        using var client = CreateClient(ReadWriteToken);
        var id = await CreateUserAsync(client, UserJson("to.delete", active: true));

        var response = await client.SendAsync(ScimRequest(HttpMethod.Delete, $"/scim/v2/Users/{id}", null));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(factory.Users.Users);
        Assert.Contains(("refresh", id), factory.Revocations);
        Assert.Contains(("session", id), factory.Revocations);
    }

    private User FindUser(Guid id) => Assert.Single(factory.Users.Users, user => user.Id == id);

    private HttpClient CreateClient(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<Guid> CreateUserAsync(HttpClient client, string body)
    {
        var response = await client.SendAsync(ScimRequest(HttpMethod.Post, "/scim/v2/Users", body));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static string UserJson(string userName, bool? active, string? email = null, string? phone = null)
    {
        var properties = new Dictionary<string, object?>
        {
            ["schemas"] = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            ["userName"] = userName,
            ["name"] = new { givenName = "Test", familyName = "User" },
        };
        if (active is not null) properties["active"] = active;
        if (email is not null) properties["emails"] = new[] { new { value = email, primary = true } };
        if (phone is not null) properties["phoneNumbers"] = new[] { new { value = phone, primary = true } };
        return JsonSerializer.Serialize(properties);
    }

    private static string PatchJson(string operation) =>
        $$"""{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{{operation}}]}""";

    private static HttpRequestMessage ScimRequest(HttpMethod method, string path, string? body, string? etag = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/scim+json");
        if (etag is not null)
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        return request;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/scim+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task AssertScimErrorAsync(HttpResponseMessage response, HttpStatusCode status, string? scimType)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {(int)status} but got {(int)response.StatusCode}: {body}");
        using var document = await ReadJsonAsync(response);
        Assert.Contains(ErrorSchema, document.RootElement.GetProperty("schemas").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(((int)status).ToString(), document.RootElement.GetProperty("status").GetString());
        var actualScimType = document.RootElement.TryGetProperty("scimType", out var value) ? value.GetString() : null;
        Assert.Equal(scimType, actualScimType);
    }
}

public sealed class ScimSecurityFactory : WebApplicationFactory<AppWebModule>
{
    public InMemoryScimUserRepository Users { get; } = new();

    /// <summary>Records ("refresh" | "session", userId) for every revoke-all call.</summary>
    public ConcurrentQueue<(string Kind, Guid UserId)> Revocations { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development")
            .UseSetting("BootstrapAdmin:Username", string.Empty)
            .UseSetting("BootstrapAdmin:Password", string.Empty)
            .ConfigureTestServices(services =>
            {
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(BootstrapAdminHostedService)));
                services.RemoveAll<IUserRepository>();
                services.RemoveAll<IRefreshTokenRepository>();
                services.RemoveAll<ISsoSessionService>();
                services.RemoveAll<IScimCredentialService>();
                services.RemoveAll<IScimUserService>();
                services.AddSingleton(Users);
                services.AddSingleton<IUserRepository>(provider => provider.GetRequiredService<InMemoryScimUserRepository>());
                services.AddSingleton(RecordingRevocationProxy.Create<IRefreshTokenRepository>(Revocations, "refresh"));
                services.AddSingleton(RecordingRevocationProxy.Create<ISsoSessionService>(Revocations, "session"));
                services.AddSingleton<IScimCredentialService, TestScimCredentialService>();
                services.AddScoped<IScimUserService, ScimUserService>();
            });
    }
}

/// <summary>
/// Records RevokeAllForUserAsync calls. Implemented as a DispatchProxy on purpose: the Luck module scans the
/// test assembly for IScopedDependency implementations, so a concrete class would be auto-registered in every host.
/// </summary>
public class RecordingRevocationProxy : DispatchProxy
{
    private ConcurrentQueue<(string Kind, Guid UserId)> revocations = new();
    private string kind = string.Empty;

    public static T Create<T>(ConcurrentQueue<(string Kind, Guid UserId)> revocations, string kind) where T : class
    {
        var proxy = DispatchProxy.Create<T, RecordingRevocationProxy>();
        var recorder = (RecordingRevocationProxy)(object)proxy;
        recorder.revocations = revocations;
        recorder.kind = kind;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "RevokeAllForUserAsync" && args?[0] is Guid userId)
            revocations.Enqueue((kind, userId));
        if (targetMethod?.ReturnType == typeof(Task)) return Task.CompletedTask;
        if (targetMethod?.ReturnType.IsGenericType == true && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = targetMethod.ReturnType.GetGenericArguments()[0];
            var result = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [result]);
        }
        throw new NotSupportedException(targetMethod?.Name);
    }
}
