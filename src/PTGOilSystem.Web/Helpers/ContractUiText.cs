using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Helpers;

public sealed record ContractLookupOption(int Id, string Display);

public static class ContractUiText
{
    public static string ResolveUnitText(Unit? unit)
        => ResolveUnitText(unit?.Symbol, unit?.Code, unit?.NamePersian, unit?.Name);

    public static string ResolveUnitText(
        string? symbol,
        string? code,
        string? namePersian,
        string? name)
    {
        foreach (var candidate in new[] { symbol, code, namePersian, name })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return "—";
    }

    public static string FormatLookup(Contract contract)
        => contract.DisplayLabel;

    public static string FormatDisplayLabel(string? contractName, string? contractNumber)
        => Contract.BuildDisplayLabel(contractName, contractNumber);

    public static string FormatLookup(
        string? contractName,
        string contractNumber,
        ContractType contractType,
        string? productName,
        string? unitText)
        => FormatDisplayLabel(contractName, contractNumber);

    public static IReadOnlyList<ContractLookupOption> ToLookupOptions(IEnumerable<Contract> contracts)
        => contracts
            .Select(contract => new ContractLookupOption(contract.Id, FormatLookup(contract)))
            .ToList();
}
