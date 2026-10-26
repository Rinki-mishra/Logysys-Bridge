namespace LogisysOrgConverter.Conversion;

public static class HoldReason
{
    public const string MissingName = "Organisation name is missing.";

    public const string BlankCountry = "Country is blank.";

    public static string UnknownCountry(string country) =>
        $"Country \"{country}\" is not in the country list. Add it, then upload the sheet again.";

    public static string AddressUpdatedFrom(int row) =>
        $"Address updated from row {row}, which is the same organisation.";

    public static string MergedInto(int row) =>
        $"Same organisation as row {row}. Not created again; its address was used for row {row}.";

    public static string CityIsCountry(string city) =>
        $"City \"{city}\" is the name of another country. Branch name is taken from the city, so this row was left out.";

    public static string ReadFromAddress(IEnumerable<string> found) =>
        $"Read from the address: {string.Join(", ", found)}.";

    public static string NotInAddress(IEnumerable<string> labels) =>
        $"The {string.Join(" and ", labels)} could not be found in the address either.";

    public static string AlreadyExists(string existsIn) =>
        $"Already in Logi-Sys ({existsIn}). Not created again; the new address is in the address updates file.";

    public static string AlreadyExistsWithoutAddress(string existsIn) =>
        $"Already in Logi-Sys ({existsIn}). Not created again. The request has no usable city and country, so there is no address to update.";

    public static string MergedIntoWithoutAddress(int row) =>
        $"Same organisation as row {row}. Not created again.";

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
