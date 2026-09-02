using PTGOilSystem.Web.Helpers;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG-P1-04 — یکسان‌سازیِ canonical متن افغانی.
///
/// شکستِ واقعی که این تست‌ها pin می‌کنند: همان بارگیری با شمارهٔ سندِ نوشته‌شده با
/// ارقام فارسی/عربی سه کلیدِ متفاوت می‌ساخت، پس Unique Index روی
/// <c>LoadingRegister.ImportUniqueKey</c> بی‌اثر می‌شد و همان واگن دوباره وارد موجودی می‌گشت.
/// </summary>
public sealed class AfghanTextNormalizerTests
{
    // ------------------------------------------------------------------
    // ۱ — ارقام: لاتین، فارسی و عربی باید یک هویت بسازند
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("RWB-12345")]
    [InlineData("RWB-۱۲۳۴۵")]
    [InlineData("RWB-١٢٣٤٥")]
    public void ThreeDigitSystems_ProduceTheSameCanonicalIdentity(string value)
        => Assert.Equal("RWB-12345", AfghanTextNormalizer.CanonicalKey(value));

    [Fact]
    public void MixedDigitsInsideOneToken_AreAllLatinised()
        => Assert.Equal("CMR-2024-0071", AfghanTextNormalizer.CanonicalKey("CMR-۲۰۲4-٠٠71"));

    // ------------------------------------------------------------------
    // ۲ — حروف: ی/ي و ک/ك و شکل‌های الف
    // ------------------------------------------------------------------

    [Fact]
    public void ArabicYehAndKaf_FoldIntoThePersianForms()
        => Assert.Equal(
            AfghanTextNormalizer.CanonicalKey("کشتی"),
            AfghanTextNormalizer.CanonicalKey("كشتي"));

    [Fact]
    public void AlefVariants_FoldIntoPlainAlef()
        => Assert.Equal(
            AfghanTextNormalizer.CanonicalKey("احمد"),
            AfghanTextNormalizer.CanonicalKey("أحمد"));

    [Fact]
    public void TehMarbuta_FoldsIntoHeh()
        => Assert.Equal(
            AfghanTextNormalizer.CanonicalKey("شرکه"),
            AfghanTextNormalizer.CanonicalKey("شركة"));

    // ------------------------------------------------------------------
    // ۳ — کاراکترهای نامرئی و فاصله
    // ------------------------------------------------------------------

    [Fact]
    public void ZeroWidthNonJoiner_DoesNotCreateASecondIdentity()
        => Assert.Equal(
            AfghanTextNormalizer.CanonicalKey("میخواهم"),
            AfghanTextNormalizer.CanonicalKey("می\u200Cخواهم"));

    [Fact]
    public void DirectionMarksAndBom_AreDropped()
        => Assert.Equal("RWB-12345", AfghanTextNormalizer.CanonicalKey("\uFEFF\u200FRWB-۱۲۳۴۵\u200E"));

    [Fact]
    public void SurroundingAndRepeatedWhitespace_IsCollapsed()
        => Assert.Equal("RWB 12345", AfghanTextNormalizer.CanonicalKey("   RWB \t\n 12345  "));

    [Fact]
    public void TatweelAndHarakat_AreDropped()
        => Assert.Equal(
            AfghanTextNormalizer.CanonicalKey("محمد"),
            AfghanTextNormalizer.CanonicalKey("مُحَمـ__ـد".Replace("__", string.Empty)));

    // ------------------------------------------------------------------
    // ۴ — سازگاری عقب‌رو: متن لاتین دقیقاً مثل قبل رفتار می‌کند
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("rwb-12345", "RWB-12345")]
    [InlineData("Wgn 98765", "WGN 98765")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void LatinIdentifiers_KeepTheHistoricUpperCasedCollapsedBehaviour(string input, string expected)
        => Assert.Equal(expected, AfghanTextNormalizer.CanonicalKey(input));

    [Fact]
    public void NullInput_IsEmptyNotAnException()
        => Assert.Equal(string.Empty, AfghanTextNormalizer.CanonicalKey(null));

    // ------------------------------------------------------------------
    // ۵ — تفکیکِ «نمایش» از «کلید»: هویت‌های واقعاً متفاوت یکی نمی‌شوند
    // ------------------------------------------------------------------

    [Fact]
    public void DifferentDocumentNumbers_StayDifferent()
        => Assert.NotEqual(
            AfghanTextNormalizer.CanonicalKey("RWB-12345"),
            AfghanTextNormalizer.CanonicalKey("RWB-12346"));

    [Fact]
    public void HyphenIsPreserved_BecauseItCanSeparateTwoRealDocuments()
        => Assert.NotEqual(
            AfghanTextNormalizer.CanonicalKey("RWB-123"),
            AfghanTextNormalizer.CanonicalKey("RWB123"));

    [Fact]
    public void NormalizeDigits_LeavesLettersAndSpacingAlone()
        => Assert.Equal("سند 12345", AfghanTextNormalizer.NormalizeDigits("سند ۱۲۳۴۵"));

    [Fact]
    public void NormalizeForSearch_IsCaseInsensitiveAndDigitAware()
        => Assert.Equal(
            AfghanTextNormalizer.NormalizeForSearch("rwb-۱۲۳۴۵"),
            AfghanTextNormalizer.NormalizeForSearch("RWB-12345"));

    // ------------------------------------------------------------------
    // ۶ — عددخوانی با ارقام و جداکنندهٔ فارسی/عربی
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("۱۲۳۴۵", 12345)]
    [InlineData("١٢٣٤٥", 12345)]
    [InlineData("12,345", 12345)]
    [InlineData("۱۲٫۵", 12.5)]
    public void TryParseDecimal_ReadsAfghanNumberForms(string input, decimal expected)
    {
        Assert.True(AfghanTextNormalizer.TryParseDecimal(input, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryParseDecimal_RejectsNonNumbers()
        => Assert.False(AfghanTextNormalizer.TryParseDecimal("RWB-12345", out _));
}
