using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogisysOrgConverter.Ai;

/// <summary>
/// What the model suggests for a row the converter held. ADVISORY ONLY - nothing here is
/// applied to a run. A human accepts or rejects it, exactly like overseas agent approval.
/// </summary>
public sealed record HeldRowProposal
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("branchName")]
    public string? BranchName { get; init; }

    [JsonPropertyName("city")]
    public string? City { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("postalCode")]
    public string? PostalCode { get; init; }

    /// <summary>0..1. Phase 4 will use this for auto-apply thresholds; today it is reporting only.</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    /// <summary>Why the model believes this, in one line, so a reviewer can judge quickly.</summary>
    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    /// <summary>Set when the model declines to guess - an honest abstention beats a invented city.</summary>
    [JsonPropertyName("insufficientEvidence")]
    public bool InsufficientEvidence { get; init; }

    // Set by us, not the model.
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("proposedAtUtc")]
    public DateTime ProposedAtUtc { get; init; }

    [JsonPropertyName("decision")]
    public string Decision { get; init; } = ProposalDecisions.Pending;

    public static string? Write(HeldRowProposal? proposal) =>
        proposal is null ? null : JsonSerializer.Serialize(proposal);

    public static HeldRowProposal? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HeldRowProposal>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string Summary()
    {
        if (InsufficientEvidence)
        {
            return "no proposal (insufficient evidence)"
                   + (string.IsNullOrWhiteSpace(Rationale) ? string.Empty : $" - {Rationale}");
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Name)) parts.Add($"name={Name}");
        if (!string.IsNullOrWhiteSpace(City)) parts.Add($"city={City}");
        if (!string.IsNullOrWhiteSpace(State)) parts.Add($"state={State}");
        if (!string.IsNullOrWhiteSpace(Country)) parts.Add($"country={Country}");
        if (!string.IsNullOrWhiteSpace(PostalCode)) parts.Add($"postal={PostalCode}");

        var body = parts.Count == 0 ? "no fields proposed" : string.Join(", ", parts);
        return $"{body} (confidence {Confidence:0.00})"
               + (string.IsNullOrWhiteSpace(Rationale) ? string.Empty : $" - {Rationale}");
    }
}

public static class ProposalDecisions
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
}
