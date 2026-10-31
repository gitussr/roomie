using System.Text;
using System.Text.Json;
using Roomie.Core.Protocol;

namespace Roomie.Tests.Protocol;

public sealed class RmpFramingTests
{
    [Fact]
    public async Task RoundTripsFrames()
    {
        using var stream = new MemoryStream();
        stream.Write(RmpFraming.Encode(RmpFrameType.Control, "{}"u8));
        stream.Write(RmpFraming.Encode(RmpFrameType.FileChunk, new byte[100]));
        stream.Position = 0;

        var first = await RmpFraming.ReadAsync(stream, default);
        var second = await RmpFraming.ReadAsync(stream, default);
        var end = await RmpFraming.ReadAsync(stream, default);

        Assert.Equal(RmpFrameType.Control, first!.Value.Type);
        Assert.Equal("{}"u8.ToArray(), first.Value.Payload.ToArray());
        Assert.Equal(RmpFrameType.FileChunk, second!.Value.Type);
        Assert.Equal(100, second.Value.Payload.Length);
        Assert.Null(end);
    }

    [Fact]
    public void Encode_MatchesTheSpecificationExample()
    {
        var frame = RmpFraming.Encode(RmpFrameType.Control, new byte[122]);

        Assert.Equal([0x00, 0x00, 0x00, 0x7B, 0x01], frame[..5]);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0, 1 }, RmpErrorCodes.InvalidFrame)]
    [InlineData(new byte[] { 0, 0, 0, 2, 9, 0 }, RmpErrorCodes.InvalidFrame)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 1 }, RmpErrorCodes.MessageTooLarge)]
    [InlineData(new byte[] { 0, 1, 0, 2, 1 }, RmpErrorCodes.MessageTooLarge)]
    public async Task RejectsBadHeaders(byte[] data, string code)
    {
        using var stream = new MemoryStream(data);

        var ex = await Assert.ThrowsAsync<RmpProtocolException>(async () => await RmpFraming.ReadAsync(stream, default));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public async Task TruncatedFrame_Throws()
    {
        using var stream = new MemoryStream(RmpFraming.Encode(RmpFrameType.Control, new byte[10])[..8]);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await RmpFraming.ReadAsync(stream, default));
    }

    [Fact]
    public void Encode_RefusesOversizedControlPayloads()
    {
        Assert.Throws<ArgumentException>(() => RmpFraming.Encode(RmpFrameType.Control, new byte[(64 * 1024) + 1]));
    }
}

public sealed class RmpEnvelopeTests
{
    private static readonly Guid Sender = Guid.Parse("7f92a31c-8d20-4b71-a6e3-0123456789ab");
    private static readonly Guid Recipient = Guid.Parse("a82f21c0-1111-4222-8333-444455556666");

    private static string Valid() => Encoding.UTF8.GetString(RmpEnvelopeCodec.Encode(new RmpEnvelope(
        "HEARTBEAT", Guid.Parse("11111111-2222-4333-8444-555555555555"), Sender, Recipient,
        new DateTimeOffset(2026, 10, 30, 13, 0, 0, TimeSpan.Zero), SessionPayloads.Empty)));

    [Fact]
    public void Encode_ProducesTheSpecifiedEnvelope()
    {
        Assert.Equal(
            """{"protocol":"RMP","version":1,"type":"HEARTBEAT","messageId":"11111111-2222-4333-8444-555555555555","senderId":"7f92a31c-8d20-4b71-a6e3-0123456789ab","recipientId":"a82f21c0-1111-4222-8333-444455556666","timestamp":"2026-10-30T13:00:00.000Z","payload":{}}""",
            Valid());
    }

    [Fact]
    public void RoundTrips()
    {
        var payload = SessionPayloads.ToElement(new HelloPayload(1, 1, "0.3.0", ["text"]));
        var envelope = RmpEnvelope.Create(RmpMessageTypes.Hello, Sender, Recipient, DateTimeOffset.UtcNow, payload);

        Assert.True(RmpEnvelopeCodec.TryDecode(RmpEnvelopeCodec.Encode(envelope), out var decoded, out var error), error);
        Assert.Equal(envelope.MessageId, decoded.MessageId);
        Assert.Equal(envelope.Type, decoded.Type);
        Assert.True(SessionPayloads.TryReadHello(decoded.Payload, out var hello));
        Assert.Equal(["text"], hello.Capabilities);
    }

    [Theory]
    [InlineData("\"protocol\":\"RMP\"", "\"protocol\":\"XMPP\"")]
    [InlineData("\"version\":1", "\"version\":2")]
    [InlineData("\"type\":\"HEARTBEAT\"", "\"type\":\"heartbeat\"")]
    [InlineData("\"senderId\":\"7f92a31c", "\"senderId\":\"7F92A31C")]
    [InlineData("\"timestamp\":\"2026-10-30T13:00:00.000Z\"", "\"timestamp\":\"yesterday\"")]
    [InlineData("\"payload\":{}", "\"payload\":[]")]
    [InlineData("\"payload\":{}", "\"payload\":{},\"extra\":true")]
    [InlineData("\"payload\":{}", "\"payload\":{},\"type\":\"CHAT_MESSAGE\"")]
    [InlineData(",\"payload\":{}", "")]
    public void TryDecode_RejectsInvalidEnvelopes(string original, string replacement)
    {
        var json = Valid().Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(Valid(), json);

        Assert.False(RmpEnvelopeCodec.TryDecode(Encoding.UTF8.GetBytes(json), out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryDecode_LimitsNesting()
    {
        var deep = new string('[', 20) + new string(']', 20);
        var json = Valid().Replace("\"payload\":{}", $"\"payload\":{{\"x\":{deep}}}", StringComparison.Ordinal);

        Assert.False(RmpEnvelopeCodec.TryDecode(Encoding.UTF8.GetBytes(json), out _, out _));
    }

    [Theory]
    [InlineData("""{"protocolVersion":1,"minimumSupportedVersion":1,"applicationVersion":"0.3.0","capabilities":[]}""", true)]
    [InlineData("""{"protocolVersion":2,"minimumSupportedVersion":1,"applicationVersion":"0.3.0","capabilities":["text","groups"]}""", true)]
    [InlineData("""{"protocolVersion":1,"minimumSupportedVersion":2,"applicationVersion":"0.3.0","capabilities":[]}""", false)]
    [InlineData("""{"protocolVersion":1,"minimumSupportedVersion":1,"applicationVersion":"0.3.0"}""", false)]
    [InlineData("""{"protocolVersion":1,"minimumSupportedVersion":1,"applicationVersion":"0.3.0","capabilities":[],"admin":true}""", false)]
    [InlineData("""{"protocolVersion":1,"minimumSupportedVersion":1,"applicationVersion":"0.3.0","capabilities":["Text"]}""", false)]
    [InlineData("""{"protocolVersion":1,"minimumSupportedVersion":1,"applicationVersion":"0.3.0","capabilities":["text","text"]}""", false)]
    [InlineData("""{"protocolVersion":1,"minimumSupportedVersion":1,"applicationVersion":null,"capabilities":[]}""", false)]
    public void Hello_IsStrictlyValidated(string json, bool valid)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(valid, SessionPayloads.TryReadHello(document.RootElement, out _));
    }

    [Fact]
    public void OnlySessionAndPairingMessages_AreAllowedBeforeTrust()
    {
        Assert.True(RmpMessageTypes.IsAllowedBeforeTrust(RmpMessageTypes.Hello));
        Assert.True(RmpMessageTypes.IsAllowedBeforeTrust(RmpMessageTypes.PairRequest));
        Assert.False(RmpMessageTypes.IsAllowedBeforeTrust(RmpMessageTypes.ChatMessage));
        Assert.False(RmpMessageTypes.IsAllowedBeforeTrust(RmpMessageTypes.FileOffer));
        Assert.False(RmpMessageTypes.IsAllowedBeforeTrust(RmpMessageTypes.GroupInvite));
    }
}
