using System.Text.Json;
using System.Text.Json.Serialization;

namespace PTGOilSystem.Web.Services.Backups;

/// <summary>
/// شناسنامهٔ داخل آرشیو (manifest.json). هدفش این است که سال‌ها بعد بتوان بدون
/// دسترسی به برنامه فهمید این فایل چیست، از کجا آمده و چگونه بازیابی می‌شود.
/// </summary>
public sealed class BackupManifest
{
    public const string FileName = "manifest.json";
    public const string DatabaseDumpEntryName = "database.dump";
    public const string SettingsEntryName = "settings.json";
    public const string UploadsEntryPrefix = "uploads/";

    /// <summary>نسخهٔ ساختار آرشیو. بازیابی فقط نسخه‌های شناخته‌شده را می‌پذیرد.</summary>
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("product")] public string Product { get; set; } = "PTG Oil System";
    [JsonPropertyName("appVersion")] public string? AppVersion { get; set; }
    [JsonPropertyName("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [JsonPropertyName("createdBy")] public string? CreatedBy { get; set; }
    [JsonPropertyName("triggerKind")] public string? TriggerKind { get; set; }

    [JsonPropertyName("databaseName")] public string? DatabaseName { get; set; }
    [JsonPropertyName("databaseDumpFormat")] public string DatabaseDumpFormat { get; set; } = "pg_dump custom (-Fc)";
    [JsonPropertyName("databaseDumpBytes")] public long DatabaseDumpBytes { get; set; }
    [JsonPropertyName("databaseDumpSha256")] public string? DatabaseDumpSha256 { get; set; }

    [JsonPropertyName("uploadsFileCount")] public int UploadsFileCount { get; set; }
    [JsonPropertyName("uploadsBytes")] public long UploadsBytes { get; set; }

    [JsonPropertyName("restoreHint")] public string RestoreHint { get; set; } =
        "pg_restore --clean --if-exists --no-owner --dbname=<database> database.dump";

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static BackupManifest? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BackupManifest>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
