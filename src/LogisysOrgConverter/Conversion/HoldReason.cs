namespace LogisysOrgConverter.Conversion;

public static class HoldReason
{
    public const string MissingName = "Organisation name is missing.";

    public const string BlankCountry = "Country is blank.";

    public static string UnknownCountry(string country) =>
        $"Country \"{country}\" is not in the country list. Add it, then upload the sheet again.";

    public static string PostalMovedFromCountry(string postal, string country) =>
        $"Postal code {postal} was moved out of the country field; country is {country}.";

    public static string CountryTakenFromCity(string country, string was) =>
        string.IsNullOrWhiteSpace(was)
            ? $"Country set to {country} from the city field."
            : $"Country set to {country} from the city field (was \"{was}\").";

    public static string CountryTakenFromAddress(string country, string was) =>
        string.IsNullOrWhiteSpace(was)
            ? $"Country set to {country} from the address."
            : $"Country set to {country} from the address (was \"{was}\").";

    public static string LocationRepairedFromAddress(string city, string? state, string country) =>
        string.IsNullOrWhiteSpace(state)
            ? $"Location set from the address: {city}, {country}."
            : $"Location set from the address: {city}, {state}, {country}.";

    public static string CountryTakenFromPlaceName(string country, string place) =>
        $"Country set to {country} because \"{place}\" is a city, not a country.";

    public static string AddressUpdatedFrom(int row) =>
        $"Address updated from row {row}, which is the same organisation.";

    public static string MergedInto(int row) =>
        $"Same organisation as row {row}. Not created again; its address was used for row {row}.";

    public static string CityIsCountry(string city) =>
        $"City \"{city}\" is the name of another country. Branch name is taken from the city, so this row was left out.";

    public static string CityIsOwnCountry(string city) =>
        $"City \"{city}\" is the country name, and no real city was found in the address. Branch name is taken from the city, so this row was left out.";

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

    public static string ClearedDuplicatedState(string city, string country) =>
        $"State repeated city \"{city}\" and is not valid for {country}, so state was cleared and the organisation was kept.";

    public static string CityWasCountryName(string was, string city) =>
        $"City was \"{was}\" (the country name), so it was replaced with \"{city}\".";

    public static string StateFilledFromAddress(string state, string city) =>
        $"State set to {state} because city and state were both \"{city}\".";

    public static string StateFilledFromCity(string state, string city) =>
        $"State set to {state} for city \"{city}\".";

    public static string InvalidPan(string pan) =>
        $"PAN \"{pan}\" is not a 10-character PAN.";

    public static string InvalidGstin(string gstin) =>
        $"GSTIN \"{gstin}\" is not a 15-character GSTIN.";

    public static string GstinPanMismatch(string gstin, string pan) =>
        $"GSTIN \"{gstin}\" does not contain PAN \"{pan}\".";

    public static string GstinStateMismatch(string gstin, string state) =>
        $"GSTIN \"{gstin}\" does not match the GST state code for {state}.";

    public static string AgentNeedsApproval(string approver) =>
        $"Overseas agent: not created until {approver} approves it.";

    public static string AgentApproved(string when) =>
        $"Overseas agent approved {when}. It is in the approved agents file, not the overseas file.";

    public static string AgentRejected(string when) =>
        $"Overseas agent not approved ({when}), so it will not be created.";

    public static string GstinFromCertificate(string gstin) =>
        $"GSTIN {gstin} was read from the GST certificate (the form had no GST number).";

    public static string GstinCorrectedFromCertificate(string typed, string gstin) =>
        $"GSTIN {gstin} was taken from the GST certificate; the form said \"{typed}\".";

    public static string GstinMatchesCertificate(string gstin) =>
        $"GSTIN {gstin} matches the GST certificate.";

    public static string GstCertificateNotRead(string problem, string typed) =>
        typed.Length > 0
            ? $"GST certificate not read ({problem}), so the GSTIN typed in the form was used."
            : $"GST certificate not read ({problem}).";

    public static string UnknownGstState(string state) =>
        $"State \"{state}\" is not in the India GST state list, so the GSTIN cannot be checked.";
}
