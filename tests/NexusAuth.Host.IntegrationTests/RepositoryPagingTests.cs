using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Luck.EntityFrameworkCore.DbContexts;
using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NexusAuth.Persistence.Repositories;
using Xunit;
using AuthDbContext = NexusAuth.Persistence.DbContext;

namespace NexusAuth.Host.IntegrationTests;

/// <summary>
/// (page - 1) * pageSize used to overflow int for large page numbers, producing a negative
/// OFFSET that PostgreSQL rejects (HTTP 500). The offset must be clamped to a non-negative int.
/// </summary>
public sealed class RepositoryPagingTests
{
    public static TheoryData<string> Repositories => ["users", "api_resources", "oauth_clients", "login_audit_logs"];

    [Theory]
    [MemberData(nameof(Repositories))]
    public async Task Huge_page_numbers_produce_a_non_negative_offset(string repository)
    {
        var capture = new PagedQueryCaptureInterceptor();
        await using var context = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=localhost;Database=metadata-only;Username=test;Password=test")
            .AddInterceptors(new SuppressOpenInterceptor(), capture)
            .Options);
        var unitOfWork = new TestUnitOfWork(context);
        Func<Task> query = repository switch
        {
            "users" => () => new UserRepository(unitOfWork).GetAdminPagedAsync(null, null, int.MaxValue, 100),
            "api_resources" => () => new ApiResourceRepository(unitOfWork).GetPagedAsync(null, null, int.MaxValue, 100),
            "oauth_clients" => () => new OAuthClientRepository(unitOfWork).GetPagedAsync(null, null, int.MaxValue, 100),
            _ => () => new LoginAuditLogRepository(unitOfWork).GetPagedAsync(null, null, null, int.MaxValue, 100),
        };

        await Assert.ThrowsAnyAsync<Exception>(query);

        var (sql, parameters) = Assert.Single(capture.PagedQueries);
        var offset = Regex.Match(sql, @"OFFSET (@\w+)");
        Assert.True(offset.Success, sql);
        Assert.Equal(int.MaxValue, Assert.IsType<int>(parameters[offset.Groups[1].Value]));
    }

    private sealed class TestUnitOfWork(AuthDbContext context) : IUnitOfWork
    {
        public ILuckDbContext GetLuckDbContext() => context;
        public Task<int> CommitAsync(CancellationToken ct = default) => context.SaveChangesAsync(ct);
    }

    private sealed class SuppressOpenInterceptor : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) => ValueTask.FromResult(InterceptionResult.Suppress());
    }

    /// <summary>Answers the COUNT query with 0 and captures (then aborts) the paged query.</summary>
    private sealed class PagedQueryCaptureInterceptor : DbCommandInterceptor
    {
        public List<(string Sql, IReadOnlyDictionary<string, object?> Parameters)> PagedQueries { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase))
            {
                var table = new DataTable();
                table.Columns.Add("count", typeof(int));
                table.Rows.Add(0);
                return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
            }

            PagedQueries.Add((command.CommandText, command.Parameters.Cast<DbParameter>()
                .ToDictionary(parameter => parameter.ParameterName.StartsWith('@') ? parameter.ParameterName : $"@{parameter.ParameterName}", parameter => parameter.Value)));
            throw new InvalidOperationException("Paged query captured; execution intentionally aborted.");
        }
    }
}
