using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LogisysOrgConverter.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LogisysOrgConverter.Ai;

/// <summary>
/// Proposes resolutions for rows the converter held (missing city/country/name it would not guess).
/// ADVISORY ONLY: proposals are stored on the row and surfaced in run-status.log. Nothing is
/// applied to the India/Overseas files - a human accepts first, exactly like agent approval.
/// </summary>
public sealed class HeldRowResolver
{
    public const string HttpClientName = "openai";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly OpenAiOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AppDbContext _db;
    private readonly ILogger<HeldRowResolver> _logger;

    public HeldRowResolver(
        IOptions<OpenAiOptions> options,
        IHttpClientFactory httpFactory,
        AppDbContext db,
        ILogger<HeldRowResolver> logger)
    {
        _options = options.Value;
        _httpFactory = httpFactory;
        _db = db;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>Returns how many held rows got a proposal. Never throws into the pipeline.</summary>
    public async Task<int> ProposeForRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogDebug("Held-row proposals skipped: no OpenAi:ApiKey configured.");
            return 0;
        }

        var rows = await _db.RunRows
            .Where(row => row.RunId == runId
                && row.Status == RowStatuses.Held
                && row.ProposalJson == null)
            .OrderBy(row => row.SourceRowNumber)
            .Take(_options.MaxRowsPerRun)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation("Proposing resolutions for {Count} held row(s) in run {RunId}.", rows.Count, runId);

        var proposed = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var proposal = await ProposeAsync(row, cancellationToken);
                if (proposal is null)
                {
                    continue;
                }

                row.ProposalJson = HeldRowProposal.Write(proposal);
                proposed++;
                _logger.LogInformation(
                    "Row {Row}: {Summary}", row.SourceRowNumber, proposal.Summary());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A model failure must never fail the run - the row simply stays held.
                _logger.LogWarning(ex, "Could not propose a resolution for row {Row}.", row.SourceRowNumber);
            }
        }

        if (proposed > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return proposed;
    }

    private async Task<HeldRowProposal?> ProposeAsync(ConversionRunRow row, CancellationToken cancellationToken)
    {
        var cells = ReadCells(row.CellsJson);
        var client = _httpFactory.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(Math.Max(10, _options.TimeoutSeconds));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        var request = new
        {
            model = _options.Model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = BuildUserPrompt(row, cells) }
            }
        };

        using var response = await client.PostAsJsonAsync("chat/completions", request, Json, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "OpenAI returned {Status} for row {Row}: {Body}",
                (int)response.StatusCode, row.SourceRowNumber, Trim(body, 300));
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var content = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var parsed = JsonSerializer.Deserialize<HeldRowProposal>(content, Json);
        if (parsed is null)
        {
            return null;
        }

        return parsed with
        {
            Model = _options.Model,
            ProposedAtUtc = DateTime.UtcNow,
            Decision = ProposalDecisions.Pending
        };
    }

    private const string SystemPrompt = """
        You help a freight-forwarding team resolve organisation records that an automated
        converter refused to guess at. You are given the raw spreadsheet cells of one request
        and the reason the converter held it.

        Propose ONLY what the evidence supports. These records are created in a live system,
        so an invented city is worse than no answer. If the address does not identify a
        place, set insufficientEvidence to true and explain briefly.

        Rules:
        - Use the address lines, postal code and phone/email domain as evidence.
        - An Indian PIN code is 6 digits; a US ZIP is 5. Use them to confirm country.
        - Do not translate or re-spell the organisation name; copy it as written.
        - State/Province must be a real subdivision of the country you give.
        - confidence is 0..1 and must reflect genuine certainty, not politeness.
        - rationale is one short sentence naming the evidence you used.

        Reply with JSON only:
        {"name":string|null,"branchName":string|null,"city":string|null,"state":string|null,
         "country":string|null,"postalCode":string|null,"confidence":number,
         "rationale":string,"insufficientEvidence":boolean}
        """;

    private static string BuildUserPrompt(ConversionRunRow row, IReadOnlyDictionary<string, string> cells)
    {
        var lines = new List<string>
        {
            $"Held reason: {row.Reason}",
            $"Source row: {row.SourceRowNumber}",
            $"Name as imported: {Blank(row.OrganisationName)}",
            $"Branch: {Blank(row.BranchName)}  City: {Blank(row.City)}  State: {Blank(row.State)}  Country: {Blank(row.Country)}",
            string.Empty,
            "Raw spreadsheet cells:"
        };

        if (cells.Count == 0)
        {
            lines.Add("(none captured)");
        }
        else
        {
            foreach (var cell in cells.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(cell.Value))
                {
                    lines.Add($"  {cell.Key}: {Trim(cell.Value, 300)}");
                }
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyDictionary<string, string> ReadCells(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? "(blank)" : value;

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
