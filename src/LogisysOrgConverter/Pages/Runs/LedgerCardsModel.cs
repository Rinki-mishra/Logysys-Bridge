using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Data;

namespace LogisysOrgConverter.Pages.Runs;

public sealed record LedgerCardItem(ConversionRunRow Row, LedgerChecklist Checklist);

public sealed record LedgerCardsModel(Guid RunId, IReadOnlyList<LedgerCardItem> Items);
