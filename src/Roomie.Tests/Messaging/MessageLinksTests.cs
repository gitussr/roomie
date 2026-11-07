using Roomie.Core.Messaging;

namespace Roomie.Tests.Messaging;

public sealed class MessageLinksTests
{
    private static string[] Links(string text) =>
        MessageLinks.Split(text).Where(s => s.IsLink).Select(s => s.Text).ToArray();

    [Theory]
    [InlineData("see https://example.com now", "https://example.com")]
    [InlineData("http://example.com/a/b?x=1&y=2#top", "http://example.com/a/b?x=1&y=2#top")]
    [InlineData("go to www.example.com", "www.example.com")]
    [InlineData("HTTPS://EXAMPLE.COM", "HTTPS://EXAMPLE.COM")]
    [InlineData("http://192.168.1.20:8080/status", "http://192.168.1.20:8080/status")]
    public void Split_FindsWebAddresses(string text, string link) =>
        Assert.Equal([link], Links(text));

    [Theory]
    [InlineData("Look at https://example.com.", "https://example.com")]
    [InlineData("https://example.com, and", "https://example.com")]
    [InlineData("really https://example.com?!", "https://example.com")]
    [InlineData("(see https://example.com)", "https://example.com")]
    [InlineData("\"https://example.com\"", "https://example.com")]
    [InlineData("https://en.wikipedia.org/wiki/Mercury_(planet)", "https://en.wikipedia.org/wiki/Mercury_(planet)")]
    [InlineData("(https://en.wikipedia.org/wiki/Mercury_(planet))", "https://en.wikipedia.org/wiki/Mercury_(planet)")]
    public void Split_LeavesOffSentencePunctuation(string text, string link) =>
        Assert.Equal([link], Links(text));

    [Fact]
    public void Split_FindsSeveralLinks()
    {
        Assert.Equal(["https://a.example", "www.b.example"], Links("https://a.example and www.b.example"));
    }

    [Theory]
    [InlineData("hello there")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("file://server/share/setup.exe")]
    [InlineData("\\\\server\\share\\setup.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("search-ms:query=secret")]
    [InlineData("ftp://example.com/file")]
    [InlineData("mail me at someone@www.example.com")]
    [InlineData("http://")]
    [InlineData("www.")]
    public void Split_OnlyLinksWebPages(string text) =>
        Assert.Empty(Links(text));

    [Fact]
    public void Split_DoesNotLinkAnAddressThatHidesItsRealSite()
    {
        // This opens evil.example, not yourbank.example.
        Assert.Empty(Links("https://yourbank.example@evil.example/login"));
    }

    [Fact]
    public void Split_StopsALinkAtADirectionMark()
    {
        var segments = MessageLinks.Split("https://example.com/\u202Eexe.txt");

        Assert.Equal("https://example.com/", segments[0].Text);
        Assert.False(segments[1].IsLink);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("see https://example.com. Or (www.example.org) ok")]
    [InlineData("https://a.example https://b.example")]
    [InlineData("file://x and https://y.example")]
    public void Split_KeepsAllTheText(string text) =>
        Assert.Equal(text, string.Concat(MessageLinks.Split(text).Select(s => s.Text)));

    [Fact]
    public void Split_IgnoresEmptyText()
    {
        Assert.Empty(MessageLinks.Split(null));
        Assert.Empty(MessageLinks.Split(""));
    }

    [Fact]
    public void ToAddress_AddsHttpsToWww() =>
        Assert.Equal("https://www.example.com/", MessageLinks.ToAddress("www.example.com"));

    [Fact]
    public void ToAddress_ShowsLookAlikeNamesInTheirPlainForm()
    {
        // The first letter is Cyrillic, so this is not apple.com, though it looks it.
        var address = MessageLinks.ToAddress("https://\u0430pple.com/");

        Assert.NotNull(address);
        Assert.StartsWith("https://xn--", address);
    }

    [Fact]
    public void ToAddress_RefusesVeryLongAddresses() =>
        Assert.Null(MessageLinks.ToAddress("https://example.com/" + new string('a', MessageLinks.MaxAddressLength)));

    [Fact]
    public void HasLink_TellsWhetherThereIsOne()
    {
        Assert.True(MessageLinks.HasLink("try www.example.com"));
        Assert.False(MessageLinks.HasLink("try file://example"));
    }
}
