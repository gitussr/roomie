using System.Security.Cryptography;
using System.Text.Json;
using Roomie.Core.Groups;
using Roomie.Core.Identity;
using Roomie.Core.Protocol;

namespace Roomie.Tests.Groups;

public sealed class GroupProtocolTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Guid _owner = Guid.NewGuid();

    public void Dispose() => _key.Dispose();

    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    private GroupState State(int members = 1) => new(
        "group-1",
        "Team",
        _owner,
        PublicKey,
        1,
        Guid.NewGuid(),
        [new GroupMember(_owner, "Owner"), .. Enumerable.Range(1, members - 1).Select(i => new GroupMember(Guid.NewGuid(), $"Member {i}"))]);

    private Signer DeviceSigner => new(_key);

    [Fact]
    public void Deletion_VerifiesOnlyForItsOwnerKey_AndCantBeMistakenForAnotherDocument()
    {
        var deletion = new GroupDeletion("group-1", _owner, 2, Guid.NewGuid());
        var signed = GroupProtocol.Sign(deletion, DeviceSigner);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.True(GroupProtocol.TryReadDeletion(signed, out var read));
        Assert.Equal(deletion, read);
        Assert.True(GroupProtocol.IsDeletionSignedBy(signed, PublicKey));
        Assert.False(GroupProtocol.IsDeletionSignedBy(signed, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
        Assert.False(GroupProtocol.IsRemovalSignedBy(signed, PublicKey));
        Assert.False(GroupProtocol.IsStateSignedBy(signed, PublicKey));
    }

    [Fact]
    public void SignedState_ReadsBackAndVerifies()
    {
        var state = State(3);
        var signed = GroupProtocol.Sign(state, DeviceSigner);

        Assert.True(GroupProtocol.TryReadState(signed, out var read));
        Assert.Equal(state.GroupId, read.GroupId);
        Assert.Equal(state.Members, read.Members);
        Assert.True(GroupProtocol.IsStateSignedBy(signed, PublicKey));
    }

    [Fact]
    public void ChangedText_OrAnotherKey_FailsVerification()
    {
        var signed = GroupProtocol.Sign(State(), DeviceSigner);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.False(GroupProtocol.IsStateSignedBy(signed with { Document = signed.Document.Replace("Team", "Evil", StringComparison.Ordinal) }, PublicKey));
        Assert.False(GroupProtocol.IsStateSignedBy(signed, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
        Assert.False(GroupProtocol.IsStateSignedBy(signed with { Signature = "not base64!" }, PublicKey));
        Assert.False(GroupProtocol.IsStateSignedBy(signed, "not base64!"));
    }

    [Fact]
    public void SignatureOnOneKindOfDocument_DoesntCountForTheOther()
    {
        var state = GroupProtocol.Sign(State(), DeviceSigner);
        var removal = GroupProtocol.Sign(new GroupRemoval("group-1", _owner, Guid.NewGuid(), 2, Guid.NewGuid()), DeviceSigner);

        Assert.True(GroupProtocol.IsRemovalSignedBy(removal, PublicKey));
        Assert.False(GroupProtocol.IsStateSignedBy(removal, PublicKey));
        Assert.False(GroupProtocol.IsRemovalSignedBy(state, PublicKey));
    }

    [Fact]
    public void OwnerKey_MustMatchTheFingerprintPinnedForTheOwner()
    {
        var state = State();
        var pinned = CertificateFingerprint.FromSubjectPublicKeyInfo(_key.ExportSubjectPublicKeyInfo());
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.True(GroupProtocol.OwnerKeyMatches(state, pinned));
        Assert.False(GroupProtocol.OwnerKeyMatches(state, CertificateFingerprint.FromSubjectPublicKeyInfo(other.ExportSubjectPublicKeyInfo())));
        Assert.False(GroupProtocol.OwnerKeyMatches(state with { OwnerPublicKey = "not base64!" }, pinned));
        Assert.False(GroupProtocol.OwnerKeyMatches(state with { OwnerPublicKey = string.Empty }, pinned));
    }

    public static TheoryData<string> InvalidStates => new()
    {
        "version 0",
        "owner not a member",
        "no members",
        "duplicate member",
        "too many members",
        "empty group ID",
        "group ID with a path in it",
        "empty name",
        "name too long",
        "control character in name",
        "control character in a username",
        "empty owner",
        "empty event",
    };

    [Theory]
    [MemberData(nameof(InvalidStates))]
    public void InvalidState_IsRefused(string problem)
    {
        var state = State(2);
        var bad = problem switch
        {
            "version 0" => state with { MembershipVersion = 0 },
            "owner not a member" => state with { Members = [state.Members[1]] },
            "no members" => state with { Members = [] },
            "duplicate member" => state with { Members = [state.Members[0], state.Members[0]] },
            "too many members" => State(GroupProtocol.MaxMembers + 1),
            "empty group ID" => state with { GroupId = string.Empty },
            "group ID with a path in it" => state with { GroupId = "..\\evil" },
            "empty name" => state with { Name = " " },
            "name too long" => state with { Name = new string('x', GroupProtocol.MaxNameLength + 1) },
            "control character in name" => state with { Name = "a\u0007b" },
            "control character in a username" => state with { Members = [state.Members[0], new GroupMember(Guid.NewGuid(), "a\nb")] },
            "empty owner" => state with { OwnerId = Guid.Empty },
            "empty event" => state with { EventId = Guid.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };

        var signed = GroupProtocol.Sign(bad, DeviceSigner);

        Assert.False(GroupProtocol.TryReadState(signed, out _));
        Assert.Equal(RmpPayloadCheck.Malformed, GroupProtocol.CheckState(SessionPayloads.ToElement(signed)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"groupId":"g"}""")]
    public void DocumentThatIsntAState_IsRefused(string document)
    {
        Assert.False(GroupProtocol.TryReadState(new SignedGroupDocument(document, string.Empty), out _));
    }

    [Fact]
    public void UnknownFieldInADocument_IsRefused()
    {
        var signed = GroupProtocol.Sign(State(), DeviceSigner);
        var extended = signed.Document.TrimEnd('}') + ",\"extra\":1}";

        Assert.False(GroupProtocol.TryReadState(signed with { Document = extended }, out _));
    }

    [Theory]
    [InlineData("""{"groupId":"g1","inviteId":"00000000-0000-0000-0000-000000000000","accepted":true}""")]
    [InlineData("""{"groupId":"","inviteId":"0192f3a0-0000-7000-8000-000000000001","accepted":true}""")]
    [InlineData("""{"groupId":"g1","inviteId":"0192f3a0-0000-7000-8000-000000000001"}""")]
    public void InvalidAnswer_IsRefused(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(RmpPayloadCheck.Malformed, GroupProtocol.CheckAccept(document.RootElement));
    }

    private sealed class Signer(ECDsa key) : IDeviceSigner
    {
        public byte[] SignData(ReadOnlySpan<byte> data) =>
            key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
