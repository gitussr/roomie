using Roomie.Core.Identity;

namespace Roomie.Tests.Identity;

public sealed class IdentityFormatTests
{
    [Fact]
    public void ToDisplayId_UsesFirstTwelveHexDigits()
    {
        var deviceId = Guid.Parse("7f92a31c-8d20-4b71-a6e3-0123456789ab");

        Assert.Equal("RM-7F92-A31C-8D20", DeviceIdFormat.ToDisplayId(deviceId));
    }

    [Fact]
    public void Fingerprint_IsSha256OfSpkiInHex()
    {
        var fingerprint = CertificateFingerprint.FromSubjectPublicKeyInfo("abc"u8);

        Assert.Equal("SHA256:BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", fingerprint);
        Assert.Equal("BA78 16BF", CertificateFingerprint.ToDisplayString(fingerprint)[..9]);
    }

    [Fact]
    public void Fingerprint_AreEqual_RequiresExactMatch()
    {
        var fingerprint = CertificateFingerprint.FromSubjectPublicKeyInfo("abc"u8);

        Assert.True(CertificateFingerprint.AreEqual(fingerprint, fingerprint));
        Assert.False(CertificateFingerprint.AreEqual(fingerprint, fingerprint.ToLowerInvariant()));
        Assert.False(CertificateFingerprint.AreEqual(fingerprint, null));
    }

    [Theory]
    [InlineData("  Subhashish ", "Subhashish")]
    [InlineData("Office PC", "Office PC")]
    public void Username_TryNormalize_AcceptsAndTrims(string input, string expected)
    {
        Assert.True(Username.TryNormalize(input, out var normalized, out _));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("tab\there")]
    [InlineData("evil‮txt.exe")]
    [InlineData("123456789012345678901234567890123")]
    public void Username_TryNormalize_RejectsInvalid(string? input)
    {
        Assert.False(Username.TryNormalize(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("", "User")]
    [InlineData("‮\t", "User")]
    [InlineData(" C4 ", "C4")]
    public void Username_Sanitize_ProducesValidName(string input, string expected)
    {
        var sanitized = Username.Sanitize(input);

        Assert.Equal(expected, sanitized);
        Assert.True(Username.TryNormalize(sanitized, out _, out _));
    }

    [Fact]
    public void Username_Sanitize_TruncatesWithoutSplittingSurrogatePairs()
    {
        var sanitized = Username.Sanitize(new string('a', 31) + "😀😀");

        Assert.True(Username.TryNormalize(sanitized, out _, out _));
        Assert.False(char.IsHighSurrogate(sanitized[^1]));
    }
}
