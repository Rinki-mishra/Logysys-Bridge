using LogisysOrgConverter.Conversion;
using Microsoft.EntityFrameworkCore;

namespace LogisysOrgConverter.Data;

public sealed class TemplatePaths
{
    public required string India { get; init; }

    public required string Overseas { get; init; }
}

public sealed class ConversionStore
{
    private readonly AppDbContext _db;
    private readonly OrganisationConverter _converter;
    private readonly TemplateWorkbookWriter _writer;
    private readonly TemplatePaths _templates;

    public ConversionStore(
        AppDbContext db,
        OrganisationConverter converter,
        TemplateWorkbookWriter writer,
        TemplatePaths templates)
    {
        _db = db;
        _converter = converter;
        _writer = writer;
        _templates = templates;
    }

    public async Task<Guid> ConvertAsync(string fileName, Stream stream, CancellationToken cancellationToken = default)
    {
        var rows = ClientSheetReader.Read(stream);
        var catalog = await LoadCatalogAsync(cancellationToken);
        var result = _converter.Convert(rows, catalog);

        var run = new ConversionRun
        {
            Id = Guid.NewGuid(),
            SourceFileName = Path.GetFileName(fileName),
            CreatedAtUtc = DateTime.UtcNow,
            IndiaCount = result.India.Count,
            OverseasCount = result.Overseas.Count,
            HeldCount = result.Held.Count,
            IndiaFile = result.India.Count == 0 ? null : _writer.Write(_templates.India, result.India),
            OverseasFile = result.Overseas.Count == 0 ? null : _writer.Write(_templates.Overseas, result.Overseas)
        };

        foreach (var ready in result.India.Concat(result.Overseas).OrderBy(item => item.SourceRowNumber))
        {
            run.Rows.Add(ToRow(run.Id, ready));
        }

        foreach (var held in result.Held)
        {
            run.Rows.Add(new ConversionRunRow
            {
                RunId = run.Id,
                SourceRowNumber = held.SourceRowNumber,
                OrganisationName = held.Name,
                Status = "Held",
                Template = string.Empty,
                Reason = held.Reason,
                BranchName = held.BranchName,
                City = held.City,
                State = held.State,
                Country = held.Country,
                RequesterEmail = held.RequesterEmail,
                RequesterName = NullIfBlank(held.RequesterName)
            });
        }

        foreach (var merged in result.Merged)
        {
            run.Rows.Add(new ConversionRunRow
            {
                RunId = run.Id,
                SourceRowNumber = merged.SourceRowNumber,
                OrganisationName = merged.Name,
                Status = "Merged",
                Template = string.Empty,
                Reason = merged.AddressUsed
                    ? HoldReason.MergedInto(merged.MergedIntoRowNumber)
                    : HoldReason.MergedIntoWithoutAddress(merged.MergedIntoRowNumber),
                BranchName = NullIfBlank(merged.City),
                City = NullIfBlank(merged.City),
                Country = NullIfBlank(merged.Country),
                RequesterEmail = merged.RequesterEmail,
                RequesterName = NullIfBlank(merged.RequesterName)
            });
        }

        run.Rows = run.Rows.OrderBy(item => item.SourceRowNumber).ToList();
        _db.Runs.Add(run);
        await _db.SaveChangesAsync(cancellationToken);
        return run.Id;
    }

    public async Task<ConversionRun?> GetRunAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (run is null)
        {
            return null;
        }

        run.Rows = await _db.RunRows.AsNoTracking()
            .Where(row => row.RunId == id)
            .OrderBy(row => row.SourceRowNumber)
            .ToListAsync(cancellationToken);
        return run;
    }

    public async Task<MappingCatalog> LoadCatalogAsync(CancellationToken cancellationToken = default)
    {
        var aliases = await _db.CountryAliases.AsNoTracking().ToListAsync(cancellationToken);
        var states = await _db.KnownStates.AsNoTracking().ToListAsync(cancellationToken);
        var maps = await _db.FieldMaps.AsNoTracking().OrderBy(map => map.SortOrder).ToListAsync(cancellationToken);
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases)
        {
            lookup[alias.AliasKey] = alias.CanonicalName;
        }

        return new MappingCatalog
        {
            CountryAliases = lookup,
            KnownStates = states.Select(state => new KnownStateRule(state.CountryCanonical, state.Name, state.Abbreviation, state.GstCode)).ToList(),
            FieldMaps = maps.Select(map => new FieldMapRule(
                map.TemplateCode,
                map.TargetHeader,
                map.Transform,
                map.SourceColumn,
                map.ConstantValue,
                map.FlagKey,
                map.FallbackColumn,
                map.SortOrder)).ToList()
        };
    }

    private static ConversionRunRow ToRow(Guid runId, ReadyOrganisation ready) => new()
    {
        RunId = runId,
        SourceRowNumber = ready.SourceRowNumber,
        OrganisationName = ready.Name,
        Status = "Ready",
        Template = ready.TemplateCode == MappingCatalog.IndiaTemplate ? "India" : "Overseas",
        Reason = ready.Note,
        BranchName = ready.BranchName,
        City = ready.City,
        State = ready.State,
        Country = ready.Country,
        RequesterEmail = ready.RequesterEmail,
        RequesterName = NullIfBlank(ready.RequesterName)
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
