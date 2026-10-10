using System.Globalization;
using System.Text.RegularExpressions;

namespace PTGOilSystem.Web.Services.Accounting;

// Immutable journal metadata for new purchase postings. Existing journal text is never
// rewritten. Quantity is needed to value a historical partial direct sale after a loading edit.
public static class PurchaseQuantitySnapshot
{
    public static string Append(string description, decimal quantityMt)
    {
        if (quantityMt <= 0m || decimal.Round(quantityMt, 4) != quantityMt)
            throw new ArgumentOutOfRangeException(nameof(quantityMt));
        return description + " [QuantityMt:v1=" + quantityMt.ToString("0.0000", CultureInfo.InvariantCulture) + "]";
    }

    public static bool TryRead(string? description, out decimal quantityMt)
    {
        quantityMt = 0m;
        if (string.IsNullOrEmpty(description)) return false;
        var matches = Regex.Matches(description, @"\[QuantityMt:v1=([0-9]+\.[0-9]{4})\]", RegexOptions.CultureInvariant);
        return matches.Count == 1 && decimal.TryParse(matches[0].Groups[1].Value,
            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quantityMt) && quantityMt > 0m;
    }
}
