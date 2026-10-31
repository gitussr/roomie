using System.Text;
using Roomie.Core.Discovery;

namespace Roomie.Tests.Discovery;

public sealed class DiscoveryPacketCodecTests
{
    private const string ValidJson =
        """{"protocol":"roomie","protocolVersion":1,"type":"DISCOVERY_ANNOUNCE","deviceId":"7f92a31c-8d20-4b71-a6e3-0123456789ab","username":"Subhashish","tcpPort":45821,"appVersion":"1.0.0"}""";

    [Fact]
    public void Encode_ProducesSpecifiedShape()
    {
        var packet = new DiscoveryPacket(DiscoveryPacketType.Announce, Guid.Parse("7f92a31c-8d20-4b71-a6e3-0123456789ab"), "Subhashish", 45821, "1.0.0");

        Assert.Equal(ValidJson, Encoding.UTF8.GetString(DiscoveryPacketCodec.Encode(packet)));
    }

    [Theory]
    [InlineData(DiscoveryPacketType.Announce)]
    [InlineData(DiscoveryPacketType.Request)]
    [InlineData(DiscoveryPacketType.Response)]
    public void RoundTrips(DiscoveryPacketType type)
    {
        var packet = new DiscoveryPacket(type, Guid.NewGuid(), "Office PC ☕", 45821, "0.2.0-beta+abc");

        Assert.True(DiscoveryPacketCodec.TryDecode(DiscoveryPacketCodec.Encode(packet), out var decoded, out var error));
        Assert.Equal(DiscoveryPacketError.None, error);
        Assert.Equal(packet, decoded);
    }

    [Fact]
    public void TryDecode_ToleratesUnknownProperties()
    {
        var json = ValidJson.Replace("}", ""","future":{"x":[1,2]}}""", StringComparison.Ordinal);

        Assert.True(DiscoveryPacketCodec.TryDecode(Encoding.UTF8.GetBytes(json), out _, out _));
    }

    [Theory]
    [InlineData("", DiscoveryPacketError.Malformed)]
    [InlineData("not json", DiscoveryPacketError.Malformed)]
    [InlineData("[]", DiscoveryPacketError.Malformed)]
    [InlineData("\"protocol\":\"roomie\"", DiscoveryPacketError.Malformed)]
    public void TryDecode_RejectsMalformedInput(string input, DiscoveryPacketError expected)
    {
        Assert.False(DiscoveryPacketCodec.TryDecode(Encoding.UTF8.GetBytes(input), out var packet, out var error));
        Assert.Null(packet);
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData("\"protocol\":\"roomie\"", "\"protocol\":\"other\"", DiscoveryPacketError.WrongProtocol)]
    [InlineData("\"protocolVersion\":1", "\"protocolVersion\":2", DiscoveryPacketError.UnsupportedVersion)]
    [InlineData("\"protocolVersion\":1", "\"protocolVersion\":\"1\"", DiscoveryPacketError.Malformed)]
    [InlineData("DISCOVERY_ANNOUNCE", "CHAT_MESSAGE", DiscoveryPacketError.UnknownType)]
    [InlineData("7f92a31c-8d20-4b71-a6e3-0123456789ab", "7F92A31C-8D20-4B71-A6E3-0123456789AB", DiscoveryPacketError.InvalidDeviceId)]
    [InlineData("7f92a31c-8d20-4b71-a6e3-0123456789ab", "{7f92a31c-8d20-4b71-a6e3-0123456789ab}", DiscoveryPacketError.InvalidDeviceId)]
    [InlineData("7f92a31c-8d20-4b71-a6e3-0123456789ab", "00000000-0000-0000-0000-000000000000", DiscoveryPacketError.InvalidDeviceId)]
    [InlineData("\"Subhashish\"", "\" Subhashish\"", DiscoveryPacketError.InvalidUsername)]
    [InlineData("\"Subhashish\"", "\"\"", DiscoveryPacketError.InvalidUsername)]
    [InlineData("\"Subhashish\"", "\"Evil\\u202Egnp.exe\"", DiscoveryPacketError.InvalidUsername)]
    [InlineData("\"Subhashish\"", "\"ThisUsernameIsWayTooLongToBeAccepted\"", DiscoveryPacketError.InvalidUsername)]
    [InlineData("45821", "0", DiscoveryPacketError.InvalidTcpPort)]
    [InlineData("45821", "65536", DiscoveryPacketError.InvalidTcpPort)]
    [InlineData("\"1.0.0\"", "\"1.0.0 <script>\"", DiscoveryPacketError.InvalidAppVersion)]
    public void TryDecode_RejectsInvalidFields(string original, string replacement, DiscoveryPacketError expected)
    {
        var json = ValidJson.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(ValidJson, json);

        Assert.False(DiscoveryPacketCodec.TryDecode(Encoding.UTF8.GetBytes(json), out _, out var error));
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("deviceId")]
    [InlineData("username")]
    [InlineData("tcpPort")]
    [InlineData("appVersion")]
    public void TryDecode_RejectsMissingFields(string name)
    {
        var json = System.Text.RegularExpressions.Regex.Replace(ValidJson, $"\"{name}\":(\"[^\"]*\"|\\d+),?", string.Empty).Replace(",}", "}", StringComparison.Ordinal);

        Assert.False(DiscoveryPacketCodec.TryDecode(Encoding.UTF8.GetBytes(json), out _, out _));
    }

    [Fact]
    public void TryDecode_RejectsDuplicateProperties()
    {
        // A second deviceId could make two validators disagree about who sent the packet.
        var json = ValidJson.Replace("}", ""","deviceId":"11111111-2222-3333-4444-555555555555"}""", StringComparison.Ordinal);

        Assert.False(DiscoveryPacketCodec.TryDecode(Encoding.UTF8.GetBytes(json), out _, out var error));
        Assert.Equal(DiscoveryPacketError.Malformed, error);
    }

    [Fact]
    public void TryDecode_RejectsOversizedPackets()
    {
        var json = ValidJson.Replace("}", $",\"pad\":\"{new string('x', 1024)}\"}}", StringComparison.Ordinal);

        Assert.False(DiscoveryPacketCodec.TryDecode(Encoding.UTF8.GetBytes(json), out _, out var error));
        Assert.Equal(DiscoveryPacketError.TooLarge, error);
    }

    [Fact]
    public void TryDecode_RejectsDeepNesting()
    {
        var json = ValidJson.Replace("}", ""","x":[[[[[[1]]]]]]}""", StringComparison.Ordinal);

        Assert.False(DiscoveryPacketCodec.TryDecode(Encoding.UTF8.GetBytes(json), out _, out _));
    }

    [Fact]
    public void Encode_RefusesInvalidPackets()
    {
        Assert.Throws<ArgumentException>(() =>
            DiscoveryPacketCodec.Encode(new DiscoveryPacket(DiscoveryPacketType.Announce, Guid.NewGuid(), "", 45821, "1.0.0")));
        Assert.Throws<ArgumentException>(() =>
            DiscoveryPacketCodec.Encode(new DiscoveryPacket(DiscoveryPacketType.Announce, Guid.NewGuid(), "A", 45821, "not valid")));
    }

    [Fact]
    public void Encode_LongestUsernameFitsInLimit()
    {
        // Every non-ASCII character is escaped to six bytes; 32 of them must still fit in 1 KB.
        var packet = new DiscoveryPacket(DiscoveryPacketType.Response, Guid.NewGuid(), new string('é', 32), 65535, new string('9', 32));

        Assert.True(DiscoveryPacketCodec.Encode(packet).Length <= 1024);
    }
}
