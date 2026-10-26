using LogisysOrgConverter.Conversion;
using Microsoft.EntityFrameworkCore;

namespace LogisysOrgConverter.Data;

public sealed class ExistingOrganisationStore
{
    private readonly AppDbContext _db;

    public ExistingOrganisationStore(AppDbContext db)
    {
        _db = db;
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        _db.ExistingOrganisations.CountAsync(cancellationToken);

    public Task<DateTime?> LastAddedAsync(CancellationToken cancellationToken = default) =>
        _db.ExistingOrganisations.MaxAsync(item => (DateTime?)item.AddedAtUtc, cancellationToken);

    public async Task<IReadOnlyList<ExistingOrganisation>> SearchAsync(string? query, int limit, CancellationToken cancellationToken = default)
    {
        var items = _db.ExistingOrganisations.AsNoTracking();
        var key = OrganisationConverter.NameKey(query ?? string.Empty);
        if (key.Length > 0)
        {
            items = items.Where(item => item.NameKey.Contains(key));
        }

        return await items.OrderBy(item => item.Name).Take(limit).ToListAsync(cancellationToken);
    }

    // The upload is the full Logi-Sys list, so it replaces the previous one.
    public async Task<int> ReplaceAsync(IEnumerable<string> names, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var unique = names
            .Select(name => (Name: name.Trim(), Key: OrganisationConverter.NameKey(name)))
            .Where(item => item.Key.Length > 0)
            .DistinctBy(item => item.Key)
            .Select(item => new ExistingOrganisation { Name = item.Name, NameKey = item.Key, AddedAtUtc = now })
            .ToList();

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            await _db.ExistingOrganisations.ExecuteDeleteAsync(cancellationToken);
            _db.ExistingOrganisations.AddRange(unique.Select(item => new ExistingOrganisation
            {
                Name = item.Name,
                NameKey = item.NameKey,
                AddedAtUtc = item.AddedAtUtc
            }));
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
        return unique.Count;
    }

    public async Task<bool> AddAsync(string name, CancellationToken cancellationToken = default)
    {
        var key = OrganisationConverter.NameKey(name);
        if (key.Length == 0 || await _db.ExistingOrganisations.AnyAsync(item => item.NameKey == key, cancellationToken))
        {
            return false;
        }

        _db.ExistingOrganisations.Add(new ExistingOrganisation { Name = name.Trim(), NameKey = key, AddedAtUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task RemoveAsync(int id, CancellationToken cancellationToken = default) =>
        await _db.ExistingOrganisations.Where(item => item.Id == id).ExecuteDeleteAsync(cancellationToken);
}
