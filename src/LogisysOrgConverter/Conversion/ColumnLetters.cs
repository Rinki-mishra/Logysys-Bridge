namespace LogisysOrgConverter.Conversion;

public static class ColumnLetters
{
    public static int Index(string letters)
    {
        var index = 0;
        foreach (var ch in letters.Trim().ToUpperInvariant())
        {
            if (ch is < 'A' or > 'Z')
            {
                throw new ArgumentException($"'{letters}' is not a column letter.", nameof(letters));
            }

            index = (index * 26) + (ch - 'A' + 1);
        }

        return index;
    }

    public static string FromIndex(int index)
    {
        if (index < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var letters = string.Empty;
        var value = index;
        while (value > 0)
        {
            value--;
            letters = (char)('A' + (value % 26)) + letters;
            value /= 26;
        }

        return letters;
    }
}
