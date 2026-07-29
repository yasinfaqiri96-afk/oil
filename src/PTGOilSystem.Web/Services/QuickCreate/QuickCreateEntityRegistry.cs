using System.Globalization;
using System.Reflection;

namespace PTGOilSystem.Web.Services.QuickCreate;

public static class QuickCreateEntityRegistry
{
    public static readonly QuickCreateEntityDefinition[] Entities =
    [
        new("CashAccount", "CashAccounts", "حساب جدید", "CashAccount", ["Name"]),
        new("ServiceProvider", "ServiceProviders", "شرکت خدماتی جدید", "ServiceProvider", ["Name"]),
        new("OperationalAsset", "OperationalAssets", "دارایی جدید", "OperationalAsset", ["AssetCode", "Name"], JoinLabelParts: true),
        new("StorageTank", "StorageTanks", "مخزن جدید", "StorageTank", ["DisplayName", "TankCode"]),
        new("ExpenseType", "ExpenseTypes", "نوع مصرف جدید", "ExpenseType", ["NamePersian", "Name"]),
        new("PurchaseContract", "Contracts", "قرارداد جدید", "Contract", ["ContractNumber"]),
        new("SaleContract", "Contracts", "قرارداد جدید", "Contract", ["ContractNumber"]),
        new("SourceContract", "Contracts", "قرارداد جدید", "Contract", ["ContractNumber"]),
        new("Contract", "Contracts", "قرارداد جدید", "Contract", ["ContractNumber"]),
        new("DestinationTerminal", "Terminals", "ترمینل جدید", "Terminal", ["Name"]),
        new("SourceTerminal", "Terminals", "ترمینل جدید", "Terminal", ["Name"]),
        new("Terminal", "Terminals", "ترمینل جدید", "Terminal", ["Name"]),
        new("DestinationLocation", "Locations", "موقعیت جدید", "Location", ["NamePersian", "Name"]),
        new("SourceLocation", "Locations", "موقعیت جدید", "Location", ["NamePersian", "Name"]),
        new("Location", "Locations", "موقعیت جدید", "Location", ["NamePersian", "Name"]),
        new("SaleCustomer", "Customers", "مشتری جدید", "Customer", ["Name"]),
        new("Customer", "Customers", "مشتری جدید", "Customer", ["Name"]),
        new("Supplier", "Suppliers", "تأمین‌کننده جدید", "Supplier", ["Name"]),
        new("Company", "Companies", "شرکت جدید", "Company", ["Name"]),
        new("Partner", "Partners", "شریک جدید", "Partner", ["Name"]),
        new("Product", "Products", "جنس جدید", "Product", ["Name"]),
        new("Unit", "Units", "واحد جدید", "Unit", ["Name"]),
        new("Truck", "Trucks", "موتر جدید", "Truck", ["PlateNumber"]),
        new("Driver", "Drivers", "راننده جدید", "Driver", ["FullName"]),
        new("Wagon", "Wagons", "واگن جدید", "Wagon", ["WagonNumber"]),
        new("Vessel", "Vessels", "کشتی جدید", "Vessel", ["Name"]),
        new("Shipment", "Shipments", "محموله جدید", "Shipment", ["ShipmentCode"]),
        new("Employee", "Employees", "کارمند جدید", "Employee", ["EmployeeCode", "FullName"], JoinLabelParts: true),
        new("Role", "Roles", "نقش جدید", "Role", ["Name"]),
        new("Sarraf", "Sarrafs", "صراف جدید", "Sarraf", ["Name"]),
        new("Currency", "Currencies", "ارز جدید", "Currency", ["Code", "Name"], JoinLabelParts: true)
    ];

    public static QuickCreateEntityDefinition? ForController(string? controller)
        => Entities.FirstOrDefault(entity =>
            string.Equals(entity.Controller, controller, StringComparison.OrdinalIgnoreCase));

    public static object? ReadProperty(object entity, string propertyName)
        => entity.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(entity);

    public static string? ReadString(object entity, string propertyName)
        => Convert.ToString(ReadProperty(entity, propertyName), CultureInfo.InvariantCulture);
}

public sealed record QuickCreateEntityDefinition(
    string Key,
    string Controller,
    string CreateLabel,
    string EntityTypeName,
    string[] LabelProperties,
    bool JoinLabelParts = false)
{
    public string BuildLabel(object entity)
    {
        var parts = LabelProperties
            .Select(property => QuickCreateEntityRegistry.ReadString(entity, property)?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

        if (parts.Length == 0)
        {
            return QuickCreateEntityRegistry.ReadString(entity, "Id") ?? "";
        }

        return JoinLabelParts ? string.Join(" - ", parts) : parts[0];
    }
}
