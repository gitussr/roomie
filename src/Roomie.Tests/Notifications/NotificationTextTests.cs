using Roomie.Core.Notifications;

namespace Roomie.Tests.Notifications;

public sealed class NotificationTextTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("hello", "hello")]
    [InlineData("  two\r\n\r\nlines\t here  ", "two lines here")]
    [InlineData("नमस्ते", "नमस्ते")]
    public void Clean_PutsTextOnOneLine(string? input, string expected) =>
        Assert.Equal(expected, NotificationText.Clean(input, 100));

    [Fact]
    public void Clean_RemovesDirectionMarks_SoAFileNameCantHideItsExtension()
    {
        // With the mark, Windows would draw the end of this name backwards, so a .exe appears to end in another extension.
        var cleaned = NotificationText.Clean("photo_\u202Egpj.exe", 100);

        Assert.Equal("photo_gpj.exe", cleaned);
    }

    [Fact]
    public void Clean_RemovesControlCharacters() =>
        Assert.Equal("a b", NotificationText.Clean("a\u0000\u0007b", 100));

    [Fact]
    public void Clean_ShortensLongText_WithinTheLimit()
    {
        var cleaned = NotificationText.Clean(new string('x', 1000), NotificationText.MaxBodyLength);

        Assert.Equal(NotificationText.MaxBodyLength, cleaned.Length);
        Assert.EndsWith("…", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_NeverSplitsAnEmoji()
    {
        var cleaned = NotificationText.Clean(string.Concat(Enumerable.Repeat("😀", 50)), 11);

        Assert.True(cleaned.Length <= 11);
        Assert.EndsWith("😀…", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_TextOfExactlyTheLimit_IsLeftWhole()
    {
        var text = new string('x', 63);

        Assert.Equal(text, NotificationText.Clean(text, NotificationText.MaxTitleLength));
    }
}
