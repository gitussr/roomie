using System.Security.Cryptography;
using Roomie.Core.Identity;
using Roomie.Core.Pairing;

namespace Roomie.Tests.Pairing;

/// <summary>Runs both halves of the §3.1 exchange by hand, as two devices would.</summary>
public sealed class PairingProtocolTests : IDisposable
{
    private readonly ECDsa _deviceA = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _deviceB = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Guid _idA = Guid.NewGuid();
    private readonly Guid _idB = Guid.NewGuid();

    public void Dispose()
    {
        _deviceA.Dispose();
        _deviceB.Dispose();
    }

    private string FpA => CertificateFingerprint.FromSubjectPublicKeyInfo(_deviceA.ExportSubjectPublicKeyInfo());

    private string FpB => CertificateFingerprint.FromSubjectPublicKeyInfo(_deviceB.ExportSubjectPublicKeyInfo());

    [Fact]
    public void BothSides_DeriveTheSameCode()
    {
        // A commits.
        using var eA = PairingProtocol.CreateEphemeralKey();
        var pubA = eA.PublicKey.ExportSubjectPublicKeyInfo();
        var nA = PairingProtocol.NewNonce();
        var commitA = PairingProtocol.Commit(pubA, nA);

        // B reveals and signs.
        using var eB = PairingProtocol.CreateEphemeralKey();
        var pubB = eB.PublicKey.ExportSubjectPublicKeyInfo();
        var nB = PairingProtocol.NewNonce();
        var transcriptB = PairingProtocol.Transcript(FpA, FpB, _idA, _idB, commitA, pubB, nB);
        var sigB = Sign(_deviceB, transcriptB);

        // A verifies B, reveals and signs.
        var transcriptA = PairingProtocol.Transcript(FpA, FpB, _idA, _idB, commitA, pubB, nB);
        Assert.True(PairingProtocol.VerifySignature(_deviceB.ExportSubjectPublicKeyInfo(), transcriptA, sigB));
        var sigA = Sign(_deviceA, PairingProtocol.InitiatorSignedData(transcriptA, pubA, nA));

        // B checks the commitment and A's signature.
        Assert.True(PairingProtocol.MatchesCommitment(commitA, pubA, nA));
        Assert.True(PairingProtocol.VerifySignature(_deviceA.ExportSubjectPublicKeyInfo(), PairingProtocol.InitiatorSignedData(transcriptB, pubA, nA), sigA));

        Assert.True(PairingProtocol.TryImportEphemeralKey(pubB, out var peerOfA));
        Assert.True(PairingProtocol.TryImportEphemeralKey(pubA, out var peerOfB));
        using (peerOfA)
        using (peerOfB)
        {
            var codeA = PairingProtocol.ComputeCode(eA, peerOfA, nA, nB, transcriptA);
            var codeB = PairingProtocol.ComputeCode(eB, peerOfB, nA, nB, transcriptB);

            Assert.Equal(codeA, codeB);
            Assert.Matches(@"^\d{3} \d{3}$", codeA);
        }
    }

    [Fact]
    public void KeyChosenAfterTheCommitment_IsDetected()
    {
        using var committed = PairingProtocol.CreateEphemeralKey();
        using var substituted = PairingProtocol.CreateEphemeralKey();
        var nonce = PairingProtocol.NewNonce();
        var commitment = PairingProtocol.Commit(committed.PublicKey.ExportSubjectPublicKeyInfo(), nonce);

        Assert.False(PairingProtocol.MatchesCommitment(commitment, substituted.PublicKey.ExportSubjectPublicKeyInfo(), nonce));
        Assert.False(PairingProtocol.MatchesCommitment(commitment, committed.PublicKey.ExportSubjectPublicKeyInfo(), PairingProtocol.NewNonce()));
    }

    [Fact]
    public void SignatureFromAnotherDevice_IsRejected()
    {
        var data = "transcript"u8.ToArray();

        Assert.False(PairingProtocol.VerifySignature(_deviceA.ExportSubjectPublicKeyInfo(), data, Sign(_deviceB, data)));
        Assert.False(PairingProtocol.VerifySignature(_deviceA.ExportSubjectPublicKeyInfo(), "other"u8, Sign(_deviceA, data)));
        Assert.False(PairingProtocol.VerifySignature(_deviceA.ExportSubjectPublicKeyInfo(), data, new byte[10]));
    }

    [Fact]
    public void CodeIsBoundToEveryTranscriptField()
    {
        using var eA = PairingProtocol.CreateEphemeralKey();
        using var eB = PairingProtocol.CreateEphemeralKey();
        var pubA = eA.PublicKey.ExportSubjectPublicKeyInfo();
        var pubB = eB.PublicKey.ExportSubjectPublicKeyInfo();
        var nA = PairingProtocol.NewNonce();
        var nB = PairingProtocol.NewNonce();
        var commitA = PairingProtocol.Commit(pubA, nA);

        string Code(byte[] transcript)
        {
            Assert.True(PairingProtocol.TryImportEphemeralKey(pubB, out var peer));
            using (peer)
            {
                return PairingProtocol.ComputeCode(eA, peer, nA, nB, transcript);
            }
        }

        var baseline = PairingProtocol.Transcript(FpA, FpB, _idA, _idB, commitA, pubB, nB);
        var variants = new[]
        {
            PairingProtocol.Transcript(FpB, FpA, _idA, _idB, commitA, pubB, nB),
            PairingProtocol.Transcript(FpA, FpB, _idB, _idA, commitA, pubB, nB),
            PairingProtocol.Transcript(FpA, FpB, _idA, Guid.NewGuid(), commitA, pubB, nB),
            PairingProtocol.Transcript(FpA, FpB, _idA, _idB, PairingProtocol.Commit(pubA, PairingProtocol.NewNonce()), pubB, nB),
        };

        var codes = variants.Select(Code).ToList();
        var baselineCode = Code(baseline);

        // A 6-digit code can collide by chance (1 in a million per pair); the transcripts themselves must differ.
        Assert.All(variants, v => Assert.NotEqual(baseline, v));
        Assert.True(codes.Count(c => c == baselineCode) <= 1);
    }

    [Theory]
    [InlineData(0u, "000 000")]
    [InlineData(42u, "000 042")]
    [InlineData(482193u, "482 193")]
    public void FormatsCodesAsTwoGroupsOfThree(uint value, string expected)
    {
        Assert.Equal(expected, PairingProtocol.FormatCode(value));
    }

    [Fact]
    public void EphemeralKeysOtherThanP256_AreRejected()
    {
        using var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);

        Assert.False(PairingProtocol.TryImportEphemeralKey(p384.PublicKey.ExportSubjectPublicKeyInfo(), out _));
        Assert.False(PairingProtocol.TryImportEphemeralKey(new byte[91], out _));
        Assert.False(PairingProtocol.TryImportEphemeralKey([], out _));
    }

    private static byte[] Sign(ECDsa key, byte[] data) =>
        key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
}
