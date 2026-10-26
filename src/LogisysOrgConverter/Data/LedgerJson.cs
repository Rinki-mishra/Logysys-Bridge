using System.Text.Json;
using LogisysOrgConverter.Conversion;

namespace LogisysOrgConverter.Data;

public static class LedgerJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string? Write(LedgerChecklist? checklist) =>
        checklist is null ? null : JsonSerializer.Serialize(checklist, Options);

    public static LedgerChecklist? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LedgerChecklist>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
