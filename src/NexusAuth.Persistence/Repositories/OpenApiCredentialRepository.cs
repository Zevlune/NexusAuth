using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Persistence.Repositories;

public sealed class OpenApiCredentialRepository(IUnitOfWork unitOfWork)
    : EfCoreEntityRepository<OpenApiCredential, Guid>(unitOfWork), IOpenApiCredentialRepository
{
    private readonly LuckDbContextBase dbContext = unitOfWork.GetLuckDbContext() as LuckDbContextBase
        ?? throw new InvalidOperationException("Failed to resolve LuckDbContext.");
    private readonly IUnitOfWork unitOfWork = unitOfWork;

    public Task<OpenApiCredential?> FindByTokenHashAsync(string tokenHash, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(tokenHash) ? Task.FromResult<OpenApiCredential?>(null) : FindAll(item => item.TokenHash == tokenHash.Trim()).FirstOrDefaultAsync(ct);
    public Task<OpenApiCredential?> FindByNameAsync(string name, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(name) ? Task.FromResult<OpenApiCredential?>(null) : FindAll(item => item.Name == name.Trim()).FirstOrDefaultAsync(ct);
    public async Task<OpenApiCredential?> FindByIdAsync(Guid id, CancellationToken ct = default) => await FindAsync(id);
    public async Task<IReadOnlyList<OpenApiCredential>> GetAllAsync(CancellationToken ct = default) => await FindAll().OrderByDescending(item => item.CreatedAt).ToListAsync(ct);
    public async Task AddAsync(OpenApiCredential credential, CancellationToken ct = default) { dbContext.Add(credential); await unitOfWork.CommitAsync(ct); }
    public async Task UpdateAsync(OpenApiCredential credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        // Management callers load the entity through this repository, so it is
        // already tracked. Let EF persist only the properties changed by the
        // caller and include the PostgreSQL xmin concurrency token in the WHERE.
        await unitOfWork.CommitAsync(ct);
    }

    public async Task RecordUseAsync(Guid id, DateTimeOffset now, CancellationToken ct = default)
    {
        now = now.ToUniversalTime();
        await dbContext.Set<OpenApiCredential>()
            .Where(credential => credential.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(credential => credential.LastUsedAt, (DateTimeOffset?)now),
                ct);
    }
}
