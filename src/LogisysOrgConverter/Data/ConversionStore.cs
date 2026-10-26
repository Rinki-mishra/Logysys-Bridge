using LogisysOrgConverter.Conversion;
using System.Text.Json;
using LogisysOrgConverter.GoogleDrive;
using LogisysOrgConverter.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
    private readonly GstCertificateReader _certificates;
    private readonly EmailOptions _email;

    public ConversionStore(
        AppDbContext db,
        OrganisationConverter converter,
        TemplateWorkbookWriter writer,
        TemplatePaths templates,
        GstCertificateReader certificates,
        IOptions<EmailOptions> email)
    {
        _email = email.Value;
        _db = db;
        _converter = converter;
        _writer = writer;
        _templates = templates;
        _certificates = certificates;
    }

    public async Task<Guid> ConvertAsync(string fileName, Stream stream, CancellationToken cancellationToken = default)
    {
        var rows = ClientSheetReader.Read(stream);
        var catalog = await LoadCatalogAsync(cancellationToken);
        var certificates = await _certificates.ReadAsync(rows, cancellationToken);
        var result = _converter.Convert(rows, catalog, certificates);

        var run = new ConversionRun
        {
            Id = Guid.NewGuid(),
            SourceFileName = Path.GetFileName(fileName),
            CreatedAtUtc = DateTime.UtcNow,
            IndiaCount = result.India.Count,
            OverseasCount = result.Overseas.Count,
            HeldCount = result.Held.Count,
            IndiaFile = result.India.Count == 0 ? null : _writer.Write(_templates.India, result.India),
            OverseasFile = result.Overseas.Count == 0 ? null : _writer.Write(_templates.Overseas, result.Overseas),
            UpdatesFile = result.Updates.Any(update => update.HasAddress) ? AddressUpdateWriter.Write(result.Updates) : null
        };

        foreach (var update in result.Updates)
        {
            run.Rows.Add(new ConversionRunRow
            {
                RunId = run.Id,
                SourceRowNumber = update.SourceRowNumber,
                OrganisationName = update.Name,
                Status = RowStatuses.Update,
                Template = string.Empty,
                Reason = update.HasAddress
                    ? HoldReason.AlreadyExists(update.ExistsIn)
                    : HoldReason.AlreadyExistsWithoutAddress(update.ExistsIn),
                BranchName = NullIfBlank(update.City),
                City = NullIfBlank(update.City),
                State = NullIfBlank(update.State),
                Country = NullIfBlank(update.Country),
                RequesterEmail = update.RequesterEmail,
                RequesterName = NullIfBlank(update.RequesterName),
                LedgerJson = LedgerJson.Write(update.Ledger)
            });
        }

        foreach (var ready in result.India.Concat(result.Overseas).OrderBy(item => item.SourceRowNumber))
        {
            run.Rows.Add(ToRow(run.Id, ready));
        }

        foreach (var agent in result.AwaitingApproval)
        {
            var row = ToRow(run.Id, agent);
            row.Status = RowStatuses.Approval;
            row.Template = RowTemplates.OverseasAgent;
            row.Reason = HoldReason.AgentNeedsApproval(_email.AgentApprover);
            row.ApprovalDetails = agent.Note;
            row.CellsJson = JsonSerializer.Serialize(agent.Cells);
            run.Rows.Add(row);
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
                RequesterName = NullIfBlank(held.RequesterName),
                LedgerJson = LedgerJson.Write(held.Ledger)
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

        var existing = new Dictionary<string, string>(StringComparer.Ordinal);
        var created = await (
                from row in _db.RunRows.AsNoTracking()
                join run in _db.Runs.AsNoTracking() on row.RunId equals run.Id
                where row.Status == RowStatuses.Ready && row.ImportStatus == ImportStatuses.Created
                orderby run.CreatedAtUtc
                select new { row.OrganisationName, run.SourceFileName, run.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        foreach (var item in created)
        {
            var key = OrganisationConverter.NameKey(item.OrganisationName);
            if (key.Length > 0)
            {
                existing.TryAdd(key, $"created by this app from {item.SourceFileName}, converted {IndiaDate(item.CreatedAtUtc)}");
            }
        }

        foreach (var key in await _db.ExistingOrganisations.AsNoTracking().Select(item => item.NameKey).ToListAsync(cancellationToken))
        {
            existing.TryAdd(key, "on the Logi-Sys organisation list");
        }

        return new MappingCatalog
        {
            ExistingOrganisations = existing,
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
        RequesterName = NullIfBlank(ready.RequesterName),
        LedgerJson = LedgerJson.Write(ready.Ledger)
    };

    // An approved agent becomes a ready row of its own "Overseas agent" file; a rejected one is never created.
    public async Task<bool> DecideAgentAsync(Guid runId, int rowId, bool approved, CancellationToken cancellationToken = default)
    {
        var row = await _db.RunRows.FirstOrDefaultAsync(
            item => item.Id == rowId && item.RunId == runId && item.Template == RowTemplates.OverseasAgent
                && item.ImportStatus == null
                && (item.Status == RowStatuses.Approval || item.Status == RowStatuses.Ready || item.Status == RowStatuses.Rejected),
            cancellationToken);
        if (row is null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var when = IndiaDate(now);
        row.Status = approved ? RowStatuses.Ready : RowStatuses.Rejected;
        row.Reason = approved ? HoldReason.AgentApproved(when) : HoldReason.AgentRejected(when);
        row.ApprovalDecidedAtUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<byte[]?> GetApprovedAgentsFileAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.RunRows.AsNoTracking()
            .Where(row => row.RunId == runId && row.Template == RowTemplates.OverseasAgent && row.Status == RowStatuses.Ready && row.CellsJson != null)
            .OrderBy(row => row.SourceRowNumber)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        var organisations = rows
            .Select(row => new ReadyOrganisation(
                row.SourceRowNumber,
                MappingCatalog.OverseasTemplate,
                row.OrganisationName,
                row.BranchName ?? string.Empty,
                row.City ?? string.Empty,
                row.State ?? string.Empty,
                row.Country ?? string.Empty,
                JsonSerializer.Deserialize<Dictionary<string, string>>(row.CellsJson!)!))
            .ToList();
        return _writer.Write(_templates.Overseas, organisations);
    }

    public async Task<bool> SetLedgerLinkedAsync(Guid runId, int rowId, bool linked, CancellationToken cancellationToken = default)
    {
        var row = await _db.RunRows.FirstOrDefaultAsync(
            item => item.Id == rowId && item.RunId == runId && item.LedgerJson != null,
            cancellationToken);
        if (row is null)
        {
            return false;
        }

        row.LedgerLinkedAtUtc = linked ? DateTime.UtcNow : null;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Ready rows of this run that also sit, not yet imported, in another run's file.
    public async Task<IReadOnlyDictionary<int, string>> GetPendingElsewhereAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var mine = await _db.RunRows.AsNoTracking()
            .Where(row => row.RunId == runId && row.Status == RowStatuses.Ready && row.ImportStatus == null)
            .Select(row => new { row.Id, row.OrganisationName })
            .ToListAsync(cancellationToken);
        if (mine.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        var others = await (
                from row in _db.RunRows.AsNoTracking()
                join run in _db.Runs.AsNoTracking() on row.RunId equals run.Id
                where row.RunId != runId && row.Status == RowStatuses.Ready && row.ImportStatus == null
                select new { row.OrganisationName, run.SourceFileName, run.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        var byKey = others
            .GroupBy(item => OrganisationConverter.NameKey(item.OrganisationName))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedAtUtc).First());

        var result = new Dictionary<int, string>();
        foreach (var row in mine)
        {
            if (byKey.TryGetValue(OrganisationConverter.NameKey(row.OrganisationName), out var other))
            {
                result[row.Id] = $"Also in the {other.SourceFileName} files converted {IndiaDate(other.CreatedAtUtc)}, not imported yet. Import it from only one of them.";
            }
        }

        return result;
    }

    private static string IndiaDate(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"))
            .ToString("d MMM yyyy, HH:mm") + " IST";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
