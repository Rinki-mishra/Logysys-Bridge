namespace LogisysOrgConverter.Conversion;

public static class HoldReason
{
    public const string MissingName = "Organisation name is missing.";

    public const string BlankCountry = "Country is blank.";

    public static string UnknownCountry(string country) =>
        $"Country \"{country}\" is not in the country list. Add it, then upload the sheet again.";

    public static string DuplicateName(string name, int firstRow) =>
        $"Organisation name \"{name}\" is already used on row {firstRow}.";

    public static string StreetCity(string city) =>
        $"City \"{city}\" looks like a street. Branch name is taken from the city, so this row was left out.";

    public static string CitySameAsState(string value, string country) =>
        $"City and State are both \"{value}\", and that is not a state for {country}. Branch name is taken from the city, so this row was left out.";

    public static string InvalidPan(string pan) =>
        $"PAN \"{pan}\" is not a 10-character PAN.";

    public static string InvalidGstin(string gstin) =>
        $"GSTIN \"{gstin}\" is not a 15-character GSTIN.";

    public static string GstinPanMismatch(string gstin, string pan) =>
        $"GSTIN \"{gstin}\" does not contain PAN \"{pan}\".";

    public static string GstinStateMismatch(string gstin, string state) =>
        $"GSTIN \"{gstin}\" does not match the GST state code for {state}.";

    public static string UnknownGstState(string state) =>
        $"State \"{state}\" is not in the India GST state list, so the GSTIN cannot be checked.";
}
