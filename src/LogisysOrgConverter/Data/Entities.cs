namespace LogisysOrgConverter.Data;

public sealed class CountryAlias
{
    public int Id { get; set; }

    public string Alias { get; set; } = string.Empty;

    public string AliasKey { get; set; } = string.Empty;

    public string CanonicalName { get; set; } = string.Empty;
}

public sealed class KnownState
{
    public int Id { get; set; }

    public string CountryCanonical { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Abbreviation { get; set; }

    public string? GstCode { get; set; }
}

public sealed class FieldMap
{
    public int Id { get; set; }

    public string TemplateCode { get; set; } = string.Empty;

    public string TargetHeader { get; set; } = string.Empty;

    public string Transform { get; set; } = string.Empty;

    public string? SourceColumn { get; set; }

    public string? ConstantValue { get; set; }

    public string? FlagKey { get; set; }

    public string? FallbackColumn { get; set; }

    public int SortOrder { get; set; }
}

public sealed class ConversionRun
{
    public Guid Id { get; set; }

    public string SourceFileName { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public int IndiaCount { get; set; }

    public int OverseasCount { get; set; }

    public int HeldCount { get; set; }

    public byte[]? IndiaFile { get; set; }

    public byte[]? OverseasFile { get; set; }

    public byte[]? UpdatesFile { get; set; }

    public List<ConversionRunRow> Rows { get; set; } = [];
}

public sealed class ConversionRunRow
{
    public int Id { get; set; }

    public Guid RunId { get; set; }

    public int SourceRowNumber { get; set; }

    public string OrganisationName { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Template { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public string? BranchName { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Country { get; set; }

    public string? RequesterEmail { get; set; }

    public string? RequesterName { get; set; }

    public string? ImportStatus { get; set; }

    public string? ImportMessage { get; set; }

    public DateTime? ImportUpdatedAtUtc { get; set; }

    public DateTime? EmailedAtUtc { get; set; }

    public string? LedgerJson { get; set; }

    public DateTime? LedgerLinkedAtUtc { get; set; }

    public DateTime? LedgerRequestedAtUtc { get; set; }
}

public sealed class ExistingOrganisation
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string NameKey { get; set; } = string.Empty;

    public DateTime AddedAtUtc { get; set; }
}

public static class RowStatuses
{
    public const string Ready = "Ready";

    public const string Held = "Held";

    public const string Merged = "Merged";

    public const string Update = "Update";
}

public static class ImportStatuses
{
    public const string Created = "Created";

    public const string Failed = "Failed";
}

public sealed class EmailNotification
{
    public int Id { get; set; }

    public Guid RunId { get; set; }

    public string RecipientEmail { get; set; } = string.Empty;

    public string? RecipientName { get; set; }

    public string Organisations { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string Delivery { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
