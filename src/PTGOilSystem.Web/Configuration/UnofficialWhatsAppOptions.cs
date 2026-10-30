namespace PTGOilSystem.Web.Configuration;

public sealed class UnofficialWhatsAppOptions
{
    public const string SectionName = "WhatsAppUnofficial";
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:8787";
    public string Secret { get; set; } = "";
}
