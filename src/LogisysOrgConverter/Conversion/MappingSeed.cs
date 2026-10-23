namespace LogisysOrgConverter.Conversion;

public static class MappingSeed
{
    public static IReadOnlyList<FieldMapRule> FieldMaps { get; } = BuildFieldMaps();

    public static IReadOnlyList<CountryAliasRule> Countries { get; } = BuildCountries();

    public static IReadOnlyList<KnownStateRule> KnownStates { get; } = BuildStates();

    public static MappingCatalog CreateCatalog()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var country in Countries)
        {
            aliases[NormalizeAlias(country.Alias)] = country.CanonicalName;
        }

        return new MappingCatalog
        {
            FieldMaps = FieldMaps,
            CountryAliases = aliases,
            KnownStates = KnownStates
        };
    }

    public static string NormalizeAlias(string value) =>
        string.Join(' ', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static IReadOnlyList<FieldMapRule> BuildFieldMaps()
    {
        var maps = new List<FieldMapRule>();
        AddShared(maps, MappingCatalog.IndiaTemplate);
        AddShared(maps, MappingCatalog.OverseasTemplate);
        maps.Add(Map(MappingCatalog.IndiaTemplate, "REG-PAN NO", "Registration", "AM", 210));
        maps.Add(Map(MappingCatalog.IndiaTemplate, "REG-GSTIN", "Registration", "AO", 220));
        maps.Add(Map(MappingCatalog.IndiaTemplate, "REG-BIN No", "Registration", "AW", 230));
        maps.Add(Map(MappingCatalog.IndiaTemplate, "REG-IE Code No", "Registration", "AQ", 240));
        return maps;
    }

    private static void AddShared(List<FieldMapRule> maps, string template)
    {
        maps.Add(new FieldMapRule(template, "Name", "Name", "W", null, null, "J", 10));
        maps.Add(Map(template, "IsGlobal", "Constant", null, 20, "yes"));
        maps.Add(new FieldMapRule(template, "IsShipper", "OrgFlag", "X", null, "Shipper", null, 30));
        maps.Add(new FieldMapRule(template, "ISConsignee", "OrgFlag", "X", null, "Consignee", null, 40));
        maps.Add(new FieldMapRule(template, "IsAgent", "OrgFlag", "X", null, "Agent", null, 50));
        maps.Add(new FieldMapRule(template, "IsCarrier", "OrgFlag", "X", null, "Carrier", null, 60));
        maps.Add(new FieldMapRule(template, "IsServiceProvider", "OrgFlag", "X", null, "ServiceProvider", null, 70));
        maps.Add(Map(template, "BranchName", "Direct", "AB", 80));
        maps.Add(Map(template, "BranchAddress1", "Direct", "Y", 90));
        maps.Add(Map(template, "BranchAddress2", "Direct", "Z", 100));
        maps.Add(Map(template, "BranchAddress3", "Direct", "AA", 110));
        maps.Add(Map(template, "City", "Direct", "AB", 120));
        maps.Add(Map(template, "State", "Direct", "AC", 130));
        maps.Add(Map(template, "Country", "Country", "AD", 140));
        maps.Add(Map(template, "PostalCode", "Direct", "AE", 150));
        maps.Add(Map(template, "Telephone", "Direct", "AF", 160));
        maps.Add(Map(template, "EMailID", "Email", "AG", 170));
        maps.Add(Map(template, "Sales Person", "Direct", "V", 180));
    }

    // Logi-Sys creates an ACC contact from both EMailID and Notification Email, so filling both fails the import on Org_Contacts.
    public static IReadOnlyList<string> RetiredTargetHeaders { get; } = ["Notification Email"];

    private static FieldMapRule Map(string template, string header, string transform, string? source, int sort, string? constant = null) =>
        new(template, header, transform, source, constant, null, null, sort);

    private static IReadOnlyList<CountryAliasRule> BuildCountries()
    {
        var rules = new List<CountryAliasRule>();
        Add(rules, "India", "India", "IND", "IN");
        Add(rules, "United States of America", "United States of America", "United States", "USA", "US", "U.S.A.", "U.S.", "America");
        Add(rules, "Australia", "Australia", "AUS");
        Add(rules, "United Kingdom", "United Kingdom", "UK", "U.K.", "Great Britain", "Britain");
        Add(rules, "United Arab Emirates", "United Arab Emirates", "UAE", "U.A.E.");
        Add(rules, "Singapore", "Singapore");
        Add(rules, "China", "China", "PRC");
        Add(rules, "Hong Kong", "Hong Kong", "HK");
        Add(rules, "Japan", "Japan");
        Add(rules, "South Korea", "South Korea", "Korea", "Republic of Korea");
        Add(rules, "Malaysia", "Malaysia");
        Add(rules, "Indonesia", "Indonesia");
        Add(rules, "Thailand", "Thailand");
        Add(rules, "Vietnam", "Vietnam", "Viet Nam");
        Add(rules, "Bangladesh", "Bangladesh");
        Add(rules, "Sri Lanka", "Sri Lanka");
        Add(rules, "Nepal", "Nepal");
        Add(rules, "Saudi Arabia", "Saudi Arabia", "KSA");
        Add(rules, "Qatar", "Qatar");
        Add(rules, "Oman", "Oman");
        Add(rules, "Kuwait", "Kuwait");
        Add(rules, "Bahrain", "Bahrain");
        Add(rules, "Germany", "Germany");
        Add(rules, "Netherlands", "Netherlands", "The Netherlands", "Holland");
        Add(rules, "Belgium", "Belgium");
        Add(rules, "France", "France");
        Add(rules, "Italy", "Italy");
        Add(rules, "Spain", "Spain");
        Add(rules, "Canada", "Canada");
        Add(rules, "Mexico", "Mexico");
        Add(rules, "Brazil", "Brazil");
        Add(rules, "New Zealand", "New Zealand", "NZ");
        Add(rules, "South Africa", "South Africa");
        Add(rules, "Kenya", "Kenya");
        Add(rules, "Nigeria", "Nigeria");
        Add(rules, "Egypt", "Egypt");
        Add(rules, "Turkey", "Turkey", "Türkiye");
        Add(rules, "Philippines", "Philippines");
        return rules;
    }

    private static void Add(List<CountryAliasRule> rules, string canonical, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            rules.Add(new CountryAliasRule(canonical, alias));
        }
    }

    private static IReadOnlyList<KnownStateRule> BuildStates()
    {
        (string Name, string Abbreviation)[] usStates =
        [
            ("Alabama", "AL"), ("Alaska", "AK"), ("Arizona", "AZ"), ("Arkansas", "AR"),
            ("California", "CA"), ("Colorado", "CO"), ("Connecticut", "CT"), ("Delaware", "DE"),
            ("Florida", "FL"), ("Georgia", "GA"), ("Hawaii", "HI"), ("Idaho", "ID"),
            ("Illinois", "IL"), ("Indiana", "IN"), ("Iowa", "IA"), ("Kansas", "KS"),
            ("Kentucky", "KY"), ("Louisiana", "LA"), ("Maine", "ME"), ("Maryland", "MD"),
            ("Massachusetts", "MA"), ("Michigan", "MI"), ("Minnesota", "MN"), ("Mississippi", "MS"),
            ("Missouri", "MO"), ("Montana", "MT"), ("Nebraska", "NE"), ("Nevada", "NV"),
            ("New Hampshire", "NH"), ("New Jersey", "NJ"), ("New Mexico", "NM"), ("New York", "NY"),
            ("North Carolina", "NC"), ("North Dakota", "ND"), ("Ohio", "OH"), ("Oklahoma", "OK"),
            ("Oregon", "OR"), ("Pennsylvania", "PA"), ("Rhode Island", "RI"), ("South Carolina", "SC"),
            ("South Dakota", "SD"), ("Tennessee", "TN"), ("Texas", "TX"), ("Utah", "UT"),
            ("Vermont", "VT"), ("Virginia", "VA"), ("Washington", "WA"), ("West Virginia", "WV"),
            ("Wisconsin", "WI"), ("Wyoming", "WY"), ("District of Columbia", "DC")
        ];
        (string Name, string Abbreviation)[] australianStates =
        [
            ("New South Wales", "NSW"), ("Victoria", "VIC"), ("Queensland", "QLD"),
            ("South Australia", "SA"), ("Western Australia", "WA"), ("Tasmania", "TAS"),
            ("Northern Territory", "NT"), ("Australian Capital Territory", "ACT")
        ];
        (string Name, string Abbreviation, string Gst)[] indianStates =
        [
            ("Jammu and Kashmir", "JK", "01"), ("Himachal Pradesh", "HP", "02"), ("Punjab", "PB", "03"),
            ("Chandigarh", "CH", "04"), ("Uttarakhand", "UK", "05"), ("Haryana", "HR", "06"),
            ("Delhi", "DL", "07"), ("Rajasthan", "RJ", "08"), ("Uttar Pradesh", "UP", "09"),
            ("Bihar", "BR", "10"), ("Sikkim", "SK", "11"), ("Arunachal Pradesh", "AR", "12"),
            ("Nagaland", "NL", "13"), ("Manipur", "MN", "14"), ("Mizoram", "MZ", "15"),
            ("Tripura", "TR", "16"), ("Meghalaya", "ML", "17"), ("Assam", "AS", "18"),
            ("West Bengal", "WB", "19"), ("Jharkhand", "JH", "20"), ("Odisha", "OD", "21"),
            ("Chhattisgarh", "CG", "22"), ("Madhya Pradesh", "MP", "23"), ("Gujarat", "GJ", "24"),
            ("Dadra and Nagar Haveli and Daman and Diu", "DH", "26"), ("Maharashtra", "MH", "27"),
            ("Karnataka", "KA", "29"), ("Goa", "GA", "30"), ("Lakshadweep", "LD", "31"),
            ("Kerala", "KL", "32"), ("Tamil Nadu", "TN", "33"), ("Puducherry", "PY", "34"),
            ("Andaman and Nicobar Islands", "AN", "35"), ("Telangana", "TS", "36"),
            ("Andhra Pradesh", "AP", "37"), ("Ladakh", "LA", "38")
        ];

        var states = new List<KnownStateRule>();
        foreach (var (name, abbreviation) in usStates)
        {
            states.Add(new KnownStateRule("United States of America", name, abbreviation, null));
        }

        foreach (var (name, abbreviation) in australianStates)
        {
            states.Add(new KnownStateRule("Australia", name, abbreviation, null));
        }

        foreach (var (name, abbreviation, gst) in indianStates)
        {
            states.Add(new KnownStateRule(MappingCatalog.IndiaCountry, name, abbreviation, gst));
        }

        states.Add(new KnownStateRule(MappingCatalog.IndiaCountry, "Andhra Pradesh", "AP", "28"));
        states.Add(new KnownStateRule(MappingCatalog.IndiaCountry, "Orissa", "OR", "21"));
        states.Add(new KnownStateRule(MappingCatalog.IndiaCountry, "New Delhi", "ND", "07"));
        return states;
    }
}
