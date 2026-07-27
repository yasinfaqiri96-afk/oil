using System.Net;
using PTGOilSystem.Web.Services.Backups;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// جزئیات پروتکل Resumable گوگل: تراز قطعه‌ها، خواندن نقطهٔ ادامه از هدر Range
/// و برداشتن شناسهٔ فایل از پاسخ نهایی. اینها همان چیزهایی هستند که آپلود پس از
/// قطع اینترنت را از سر می‌گیرند.
/// </summary>
public class GoogleDriveResumableUploadTests
{
    private const int Alignment = 256 * 1024;

    [Theory]
    [InlineData(8 * 1024 * 1024, 8 * 1024 * 1024)]
    [InlineData(1, Alignment)]
    [InlineData(0, Alignment)]
    [InlineData(-5, Alignment)]
    public void Chunk_Sizes_Are_Clamped_To_The_Google_Minimum(int requested, int expected)
        => Assert.Equal(expected, GoogleDriveBackupClient.NormalizeChunkSize(requested));

    [Fact]
    public void Chunk_Sizes_Are_Rounded_Down_To_A_Multiple_Of_256KB()
    {
        var normalized = GoogleDriveBackupClient.NormalizeChunkSize(Alignment + 1000);

        Assert.Equal(Alignment, normalized);
        Assert.Equal(0, normalized % Alignment);
    }

    [Fact]
    public void The_Resume_Offset_Is_The_Byte_After_The_Last_One_Google_Received()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)308);
        response.Headers.TryAddWithoutValidation("Range", "bytes=0-262143");

        Assert.Equal(262144, GoogleDriveBackupClient.ReadResumeOffset(response));
    }

    [Fact]
    public void A_Missing_Range_Header_Falls_Back_To_The_Caller_Supplied_Offset()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)308);

        Assert.Equal(4096, GoogleDriveBackupClient.ReadResumeOffset(response, fallback: 4096));
    }

    [Fact]
    public void A_Malformed_Range_Header_Falls_Back_Instead_Of_Restarting_From_Zero()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)308);
        response.Headers.TryAddWithoutValidation("Range", "bytes=0-");

        Assert.Equal(1024, GoogleDriveBackupClient.ReadResumeOffset(response, fallback: 1024));
    }

    [Fact]
    public void The_Drive_File_Id_Is_Read_From_The_Final_Response_Body()
        => Assert.Equal("1AbCdEf", GoogleDriveBackupClient.ReadFileId("{\"id\":\"1AbCdEf\",\"name\":\"ptg-backup.zip\"}"));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"name\":\"no-id.zip\"}")]
    public void A_Response_Without_A_Usable_Id_Yields_Null(string body)
        => Assert.Null(GoogleDriveBackupClient.ReadFileId(body));

    [Fact]
    public void Offline_Access_Is_Requested_So_A_Refresh_Token_Is_Actually_Issued()
    {
        // بدون access_type=offline و prompt=consent، گوگل Refresh Token نمی‌دهد و
        // بکاپ خودکار بعد از انقضای Access Token از کار می‌افتد.
        var source = File.ReadAllText(LocateClientSource());

        Assert.Contains("\"access_type\"] = \"offline\"", source, StringComparison.Ordinal);
        Assert.Contains("\"prompt\"] = \"consent\"", source, StringComparison.Ordinal);
        Assert.Contains("uploadType=resumable", source, StringComparison.Ordinal);
    }

    private static string LocateClientSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src", "PTGOilSystem.Web", "Services", "Backups", "GoogleDriveBackupClient.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("GoogleDriveBackupClient.cs was not found from the test output directory.");
    }
}
