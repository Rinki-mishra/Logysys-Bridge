namespace LogisysOrgConverter.Conversion;

// Well-known cities used when the form puts a city in the country field, or leaves
// state blank. Values are (country canonical name, optional state/region).
public static class CityStates
{
    private static readonly Dictionary<string, (string Country, string? State)> Places =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // United States
            ["Atlanta"] = ("United States of America", "Georgia"),
            ["Austin"] = ("United States of America", "Texas"),
            ["Baltimore"] = ("United States of America", "Maryland"),
            ["Boston"] = ("United States of America", "Massachusetts"),
            ["Charlotte"] = ("United States of America", "North Carolina"),
            ["Chicago"] = ("United States of America", "Illinois"),
            ["Cincinnati"] = ("United States of America", "Ohio"),
            ["Cleveland"] = ("United States of America", "Ohio"),
            ["Columbus"] = ("United States of America", "Ohio"),
            ["Cary"] = ("United States of America", "North Carolina"),
            ["Dallas"] = ("United States of America", "Texas"),
            ["Denver"] = ("United States of America", "Colorado"),
            ["Detroit"] = ("United States of America", "Michigan"),
            ["Houston"] = ("United States of America", "Texas"),
            ["Indianapolis"] = ("United States of America", "Indiana"),
            ["Jacksonville"] = ("United States of America", "Florida"),
            ["Kansas City"] = ("United States of America", "Missouri"),
            ["Las Vegas"] = ("United States of America", "Nevada"),
            ["Los Angeles"] = ("United States of America", "California"),
            ["Memphis"] = ("United States of America", "Tennessee"),
            ["Miami"] = ("United States of America", "Florida"),
            ["Milwaukee"] = ("United States of America", "Wisconsin"),
            ["Minneapolis"] = ("United States of America", "Minnesota"),
            ["Nashville"] = ("United States of America", "Tennessee"),
            ["New York"] = ("United States of America", "New York"),
            ["Orlando"] = ("United States of America", "Florida"),
            ["Philadelphia"] = ("United States of America", "Pennsylvania"),
            ["Phoenix"] = ("United States of America", "Arizona"),
            ["Pittsburgh"] = ("United States of America", "Pennsylvania"),
            ["Portland"] = ("United States of America", "Oregon"),
            ["Raleigh"] = ("United States of America", "North Carolina"),
            ["Sacramento"] = ("United States of America", "California"),
            ["San Antonio"] = ("United States of America", "Texas"),
            ["San Diego"] = ("United States of America", "California"),
            ["San Francisco"] = ("United States of America", "California"),
            ["San Jose"] = ("United States of America", "California"),
            ["Seattle"] = ("United States of America", "Washington"),
            ["St Louis"] = ("United States of America", "Missouri"),
            ["St. Louis"] = ("United States of America", "Missouri"),
            ["Tampa"] = ("United States of America", "Florida"),

            // India
            ["Ahmedabad"] = ("India", "Gujarat"),
            ["Bengaluru"] = ("India", "Karnataka"),
            ["Bangalore"] = ("India", "Karnataka"),
            ["Chennai"] = ("India", "Tamil Nadu"),
            ["Hyderabad"] = ("India", "Telangana"),
            ["Jaipur"] = ("India", "Rajasthan"),
            ["Kolkata"] = ("India", "West Bengal"),
            ["Mumbai"] = ("India", "Maharashtra"),
            ["New Delhi"] = ("India", "Delhi"),
            ["Delhi"] = ("India", "Delhi"),
            ["Pune"] = ("India", "Maharashtra"),
            ["Surat"] = ("India", "Gujarat"),

            // Elsewhere — cities often typed into the country column
            ["Lagos"] = ("Nigeria", null),
            ["Abuja"] = ("Nigeria", null),
            ["Nairobi"] = ("Kenya", null),
            ["Mombasa"] = ("Kenya", null),
            ["Accra"] = ("Ghana", null),
            ["Cairo"] = ("Egypt", null),
            ["Johannesburg"] = ("South Africa", null),
            ["Cape Town"] = ("South Africa", null),
            ["Durban"] = ("South Africa", null),
            ["Dubai"] = ("United Arab Emirates", "Dubai"),
            ["Abu Dhabi"] = ("United Arab Emirates", "Abu Dhabi"),
            ["Sharjah"] = ("United Arab Emirates", "Sharjah"),
            ["Doha"] = ("Qatar", null),

            ["Riyadh"] = ("Saudi Arabia", null),
            ["Jeddah"] = ("Saudi Arabia", null),
            ["Muscat"] = ("Oman", null),
            ["Kuwait City"] = ("Kuwait", null),
            ["Manama"] = ("Bahrain", null),
            ["Istanbul"] = ("Turkey", null),
            ["Ankara"] = ("Turkey", null),
            ["London"] = ("United Kingdom", null),
            ["Manchester"] = ("United Kingdom", null),
            ["Birmingham"] = ("United Kingdom", null),
            ["Paris"] = ("France", null),
            ["Frankfurt"] = ("Germany", null),
            ["Hamburg"] = ("Germany", null),
            ["Munich"] = ("Germany", null),
            ["Berlin"] = ("Germany", null),
            ["Amsterdam"] = ("Netherlands", null),
            ["Rotterdam"] = ("Netherlands", null),
            ["Antwerp"] = ("Belgium", null),
            ["Brussels"] = ("Belgium", null),
            ["Madrid"] = ("Spain", null),
            ["Barcelona"] = ("Spain", null),
            ["Milan"] = ("Italy", "Lombardy"),
            ["Milano"] = ("Italy", "Lombardy"),
            ["Rome"] = ("Italy", "Lazio"),
            ["Roma"] = ("Italy", "Lazio"),
            ["Turin"] = ("Italy", "Piedmont"),
            ["Torino"] = ("Italy", "Piedmont"),
            ["Naples"] = ("Italy", "Campania"),
            ["Napoli"] = ("Italy", "Campania"),
            ["Florence"] = ("Italy", "Tuscany"),
            ["Firenze"] = ("Italy", "Tuscany"),
            ["Venice"] = ("Italy", "Veneto"),
            ["Venezia"] = ("Italy", "Veneto"),
            ["Bologna"] = ("Italy", "Emilia-Romagna"),
            ["Genoa"] = ("Italy", "Liguria"),
            ["Genova"] = ("Italy", "Liguria"),
            ["Palermo"] = ("Italy", "Sicily"),
            ["Catania"] = ("Italy", "Sicily"),
            ["Bari"] = ("Italy", "Apulia"),
            ["Verona"] = ("Italy", "Veneto"),
            ["Padua"] = ("Italy", "Veneto"),
            ["Padova"] = ("Italy", "Veneto"),
            ["Brescia"] = ("Italy", "Lombardy"),
            ["Bergamo"] = ("Italy", "Lombardy"),
            ["Palazzolo"] = ("Italy", "Lombardy"),
            ["Zurich"] = ("Switzerland", null),

            ["Geneva"] = ("Switzerland", null),
            ["Vienna"] = ("Austria", null),
            ["Toronto"] = ("Canada", "Ontario"),
            ["Montreal"] = ("Canada", "Quebec"),
            ["Vancouver"] = ("Canada", "British Columbia"),
            ["Calgary"] = ("Canada", "Alberta"),
            ["Ottawa"] = ("Canada", "Ontario"),
            ["Edmonton"] = ("Canada", "Alberta"),
            ["Winnipeg"] = ("Canada", "Manitoba"),
            ["Quebec City"] = ("Canada", "Quebec"),
            ["Halifax"] = ("Canada", "Nova Scotia"),
            ["Mirabel"] = ("Canada", "Quebec"),

            ["Sydney"] = ("Australia", null),
            ["Melbourne"] = ("Australia", null),
            ["Brisbane"] = ("Australia", null),
            ["Perth"] = ("Australia", null),
            ["Auckland"] = ("New Zealand", null),
            ["Shanghai"] = ("China", null),
            ["Beijing"] = ("China", null),
            ["Guangzhou"] = ("China", null),
            ["Shenzhen"] = ("China", null),
            ["Hong Kong"] = ("Hong Kong", null),
            ["Tokyo"] = ("Japan", null),
            ["Osaka"] = ("Japan", null),
            ["Seoul"] = ("South Korea", null),
            ["Busan"] = ("South Korea", null),
            ["Singapore"] = ("Singapore", null),
            ["Kuala Lumpur"] = ("Malaysia", null),
            ["Jakarta"] = ("Indonesia", null),
            ["Bangkok"] = ("Thailand", null),
            ["Ho Chi Minh"] = ("Vietnam", null),
            ["Ho Chi Minh City"] = ("Vietnam", null),
            ["Hanoi"] = ("Vietnam", null),
            ["Manila"] = ("Philippines", null),
            ["Dhaka"] = ("Bangladesh", null),
            ["Colombo"] = ("Sri Lanka", null),
            ["Kathmandu"] = ("Nepal", null),
            ["Karachi"] = ("Pakistan", null),
            ["Lahore"] = ("Pakistan", null),
            ["Sao Paulo"] = ("Brazil", null),
            ["São Paulo"] = ("Brazil", null),
            ["Rio de Janeiro"] = ("Brazil", null),
            ["Mexico City"] = ("Mexico", null),
            ["Buenos Aires"] = ("Argentina", null)
        };

    public static bool TryGetState(string country, string city, out string state)
    {
        state = string.Empty;
        if (!TryResolvePlace(city, out var mappedCountry, out var mappedState)
            || !mappedCountry.Equals(country, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(mappedState))
        {
            return false;
        }

        state = mappedState;
        return true;
    }

    public static bool TryResolvePlace(string place, out string country, out string? state)
    {
        country = string.Empty;
        state = null;
        if (string.IsNullOrWhiteSpace(place) || !Places.TryGetValue(place.Trim(), out var mapped))
        {
            return false;
        }

        country = mapped.Country;
        state = mapped.State;
        return true;
    }
}
