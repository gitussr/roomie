using System.Reflection;
using System.Text;
using System.Text.Json;
using Roomie.Core.Protocol;

namespace Roomie.Tests.Protocol;

public sealed class RmpMessagePolicyTests
{
    private static readonly HashSet<string> All = [RmpCapabilities.Text, RmpCapabilities.Groups, RmpCapabilities.FileTransfer];
    private static readonly HashSet<string> None = [];

    private static readonly Guid Sender = Guid.Parse("7f92a31c-8d20-4b71-a6e3-0123456789ab");
    private static readonly Guid Recipient = Guid.Parse("a82f21c0-1111-4222-8333-444455556666");

    private static string? Code(string type, JsonElement payload, bool trusted = true, IReadOnlySet<string>? capabilities = null) =>
        RmpMessagePolicy.Check(type, payload, trusted, capabilities ?? All)?.Code;

    private static JsonElement Chat(string content) =>
        SessionPayloads.ToElement(new ChatMessagePayload("conversation-1", RmpContentTypes.Text, content));

    private static JsonElement Offer(string filename = "notes.pdf", long size = 1024, string? sha256 = null, string contentType = "application/pdf") =>
        SessionPayloads.ToElement(new FileOfferPayload(Guid.NewGuid(), filename, size, sha256 ?? new string('a', 64), contentType));

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void EveryMessageTypeHasRules()
    {
        var declared = typeof(RmpMessageTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);

        Assert.Equal(declared.Order(StringComparer.Ordinal), RmpMessagePolicy.KnownTypes.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(RmpMessageTypes.ChatMessage)]
    [InlineData(RmpMessageTypes.GroupState)]
    [InlineData(RmpMessageTypes.FileOffer)]
    [InlineData("FEATURE_FROM_THE_FUTURE")]
    public void UnpairedDevices_GetTheSameAnswerForEverythingTheyMayNotSend(string type)
    {
        Assert.Equal(RmpErrorCodes.PeerNotTrusted, Code(type, SessionPayloads.Empty, trusted: false));
    }

    [Theory]
    [InlineData(RmpMessageTypes.ChatMessage)]
    [InlineData(RmpMessageTypes.MessageRead)]
    [InlineData(RmpMessageTypes.GroupInvite)]
    [InlineData(RmpMessageTypes.FileAccept)]
    public void FeatureMessages_RequireTheirCapability(string type)
    {
        Assert.Equal(RmpErrorCodes.UnsupportedMessage, Code(type, SessionPayloads.Empty, capabilities: None));
    }

    [Fact]
    public void SessionAndPairingMessages_RequireNoCapability()
    {
        Assert.Null(Code(RmpMessageTypes.Heartbeat, SessionPayloads.Empty, trusted: false, capabilities: None));
        Assert.Null(Code(RmpMessageTypes.PairApprove, SessionPayloads.ToElement(new { pairingId = Guid.NewGuid() }), trusted: false, capabilities: None));
    }

    [Fact]
    public void UnknownTypes_AreUnsupported()
    {
        Assert.Equal(RmpErrorCodes.UnsupportedMessage, Code("FEATURE_FROM_THE_FUTURE", SessionPayloads.Empty));
    }

    [Fact]
    public void FileChunk_IsNeverAControlMessage()
    {
        Assert.Equal(RmpErrorCodes.InvalidFrame, Code(RmpMessageTypes.FileChunk, SessionPayloads.Empty));
    }

    [Fact]
    public void PayloadSize_IsLimitedPerType()
    {
        var large = SessionPayloads.ToElement(new { padding = new string('x', 2000) });

        Assert.Equal(RmpErrorCodes.MessageTooLarge, Code(RmpMessageTypes.MessageAck, large));
        Assert.Equal(RmpErrorCodes.MessageTooLarge, Code(RmpMessageTypes.PairApprove, SessionPayloads.ToElement(new { padding = new string('x', 3000) })));
        Assert.Equal(RmpErrorCodes.MessageTooLarge, Code(RmpMessageTypes.FileOffer, Offer(contentType: new string('x', RmpContentLimits.MaxFileMetadataBytes))));
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(RmpContentLimits.MaxTextBodyBytes, null)]
    [InlineData(RmpContentLimits.MaxTextBodyBytes + 1, RmpErrorCodes.MessageTooLarge)]
    public void ChatBody_IsLimitedTo16KiB(int length, string? expected)
    {
        Assert.Equal(expected, Code(RmpMessageTypes.ChatMessage, Chat(new string('x', length))));
    }

    [Fact]
    public void ChatBody_LimitCountsUtf8BytesNotCharacters()
    {
        // "न" takes three bytes in UTF-8.
        Assert.Null(Code(RmpMessageTypes.ChatMessage, Chat(new string('न', RmpContentLimits.MaxTextBodyBytes / 3))));
        Assert.Equal(RmpErrorCodes.MessageTooLarge, Code(RmpMessageTypes.ChatMessage, Chat(new string('न', (RmpContentLimits.MaxTextBodyBytes / 3) + 1))));
    }

    [Theory]
    [InlineData("""{"conversationId":"c1","contentType":"text","content":"hi"}""", true)]
    [InlineData("""{"conversationId":"c1","contentType":"text","content":"line one\r\n\ttwo"}""", true)]
    [InlineData("""{"conversationId":"c1","contentType":"text","content":"hi","groupId":"g1","membershipVersion":3}""", true)]
    [InlineData("""{"conversationId":"c1","contentType":"text","content":""}""", false)]
    [InlineData("""{"conversationId":"c1","contentType":"text","content":"bell\u0007"}""", false)]
    [InlineData("""{"conversationId":"c1","contentType":"html","content":"hi"}""", false)]
    [InlineData("""{"conversationId":"../x","contentType":"text","content":"hi"}""", false)]
    [InlineData("""{"contentType":"text","content":"hi"}""", false)]
    [InlineData("""{"conversationId":"c1","contentType":"text","content":"hi","admin":true}""", false)]
    [InlineData("""{"conversationId":"c1","contentType":"text","content":"hi","groupId":"g1"}""", false)]
    [InlineData("""{"conversationId":"c1","contentType":"text","content":"hi","membershipVersion":1}""", false)]
    [InlineData("""{"conversationId":"c1","contentType":"text","content":"hi","groupId":"g1","membershipVersion":0}""", false)]
    [InlineData("""{"conversationId":"c1","contentType":"text","content":"\uD800"}""", false)]
    public void ChatMessage_IsStrictlyValidated(string json, bool valid)
    {
        Assert.Equal(valid, ContentPayloads.TryReadChatMessage(Parse(json), out _));
    }

    [Fact]
    public void FileOffer_SizeIsLimitedTo100MB()
    {
        Assert.Null(Code(RmpMessageTypes.FileOffer, Offer(size: 0)));
        Assert.Null(Code(RmpMessageTypes.FileOffer, Offer(size: RmpContentLimits.MaxFileSizeBytes)));
        Assert.Equal(RmpErrorCodes.MessageTooLarge, Code(RmpMessageTypes.FileOffer, Offer(size: RmpContentLimits.MaxFileSizeBytes + 1)));
        Assert.Equal(RmpErrorCodes.InvalidFrame, Code(RmpMessageTypes.FileOffer, Offer(size: -1)));
    }

    [Theory]
    [InlineData("report (final).pdf", true)]
    [InlineData("रिपोर्ट.pdf", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData("../../evil.exe", false)]
    [InlineData(@"C:\Windows\evil.exe", false)]
    [InlineData("name.txt:stream", false)]
    [InlineData("bad\nname.txt", false)]
    public void FileOffer_FilenameCantBeAPath(string filename, bool valid)
    {
        Assert.Equal(valid, ContentPayloads.TryReadFileOffer(Offer(filename), out _));
    }

    [Fact]
    public void FileOffer_HashMustBeLowercaseSha256Hex()
    {
        Assert.False(ContentPayloads.TryReadFileOffer(Offer(sha256: new string('A', 64)), out _));
        Assert.False(ContentPayloads.TryReadFileOffer(Offer(sha256: new string('a', 63)), out _));
    }

    [Theory]
    [InlineData('"')]
    [InlineData('\\')]
    [InlineData('\n')]
    [InlineData('<')]
    [InlineData('न')]
    public void FullSizeChatBody_FitsAControlFrame_HoweverItEscapes(char ch)
    {
        var count = RmpContentLimits.MaxTextBodyBytes / Encoding.UTF8.GetByteCount([ch]);
        var body = new string(ch, count);
        var envelope = RmpEnvelope.Create(RmpMessageTypes.ChatMessage, Sender, Recipient, DateTimeOffset.UtcNow, Chat(body));

        Assert.True(RmpEnvelopeCodec.TryDecode(RmpEnvelopeCodec.Encode(envelope), out var decoded, out var error), error);
        Assert.True(ContentPayloads.TryReadChatMessage(decoded.Payload, out var chat));
        Assert.Equal(body, chat.Content);
    }

    [Fact]
    public void FullSizeChatBody_OfEmoji_FitsAControlFrame()
    {
        var body = string.Concat(Enumerable.Repeat("😀", RmpContentLimits.MaxTextBodyBytes / 4));
        var envelope = RmpEnvelope.Create(RmpMessageTypes.ChatMessage, Sender, Recipient, DateTimeOffset.UtcNow, Chat(body));

        Assert.True(RmpEnvelopeCodec.TryDecode(RmpEnvelopeCodec.Encode(envelope), out var decoded, out var error), error);
        Assert.True(ContentPayloads.TryReadChatMessage(decoded.Payload, out var chat));
        Assert.Equal(body, chat.Content);
    }

    [Fact]
    public void EnvelopeCreate_UsesTimeOrderedMessageIds()
    {
        var envelope = RmpEnvelope.Create(RmpMessageTypes.Heartbeat, Sender, Recipient, DateTimeOffset.UtcNow, SessionPayloads.Empty);

        Assert.Equal(7, envelope.MessageId.Version);
    }

    [Theory]
    [InlineData("\"type\":\"HEARTBEAT\"", "\"type\":\"\\uD800\"")]
    [InlineData("\"protocol\":\"RMP\"", "\"protocol\":\"\\uDC00\"")]
    [InlineData("\"timestamp\":\"2026-10-30T13:00:00.000Z\"", "\"timestamp\":\"\\uD800\"")]
    public void TryDecode_RefusesBrokenEscapesWithoutThrowing(string original, string replacement)
    {
        var json = Valid().Replace(original, replacement, StringComparison.Ordinal);

        Assert.False(RmpEnvelopeCodec.TryDecode(Encoding.UTF8.GetBytes(json), out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryDecode_RefusesInvalidUtf8WithoutThrowing()
    {
        var bytes = Encoding.UTF8.GetBytes(Valid().Replace("HEARTBEAT", "HEARTBEA#", StringComparison.Ordinal));
        bytes[Array.IndexOf(bytes, (byte)'#')] = 0xFF;

        Assert.False(RmpEnvelopeCodec.TryDecode(bytes, out _, out var error));
        Assert.NotNull(error);
    }

    private static string Valid() => Encoding.UTF8.GetString(RmpEnvelopeCodec.Encode(new RmpEnvelope(
        RmpMessageTypes.Heartbeat, Guid.Parse("11111111-2222-4333-8444-555555555555"), Sender, Recipient,
        new DateTimeOffset(2026, 10, 30, 13, 0, 0, TimeSpan.Zero), SessionPayloads.Empty)));
}

public sealed class RmpNegotiationTests
{
    private static HelloPayload Hello(int version, int minimum, params string[] capabilities) => new(version, minimum, "1.0.0", capabilities);

    [Fact]
    public void Capabilities_AreTheIntersection()
    {
        var negotiated = RmpNegotiatedSession.Negotiate(Hello(1, 1, "text", "groups", "new-thing"), Hello(1, 1, "groups", "text", "other"));

        Assert.NotNull(negotiated);
        Assert.Equal(new HashSet<string> { "text", "groups" }, negotiated.Capabilities);
    }

    [Theory]
    [InlineData(1, 1, 1, 1, 1)]
    [InlineData(2, 1, 1, 1, 1)]
    [InlineData(3, 1, 2, 2, 2)]
    public void Version_IsTheHighestBothSupport(int ours, int ourMinimum, int theirs, int theirMinimum, int expected)
    {
        Assert.Equal(expected, RmpNegotiatedSession.Negotiate(Hello(ours, ourMinimum), Hello(theirs, theirMinimum))?.ProtocolVersion);
    }

    [Theory]
    [InlineData(1, 1, 3, 2)]
    [InlineData(3, 2, 1, 1)]
    public void Version_WithoutOverlap_Fails(int ours, int ourMinimum, int theirs, int theirMinimum)
    {
        Assert.Null(RmpNegotiatedSession.Negotiate(Hello(ours, ourMinimum), Hello(theirs, theirMinimum)));
    }
}
