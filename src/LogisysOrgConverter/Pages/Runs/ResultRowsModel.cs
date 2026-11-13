using LogisysOrgConverter.Data;

namespace LogisysOrgConverter.Pages.Runs;

public sealed record ResultRowsModel(
    Guid RunId,
    IReadOnlyList<ConversionRunRow> Rows,
    IReadOnlyDictionary<int, string> PendingElsewhere);
