using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Roomie.Core.Protocol;

namespace Roomie.Core.Pairing;

/// <summary><c>PAIR_REQUEST</c>: the initiator's commitment. Its ephemeral key is not sent yet.</summary>
public sealed record PairRequestPayload(Guid PairingId, byte[] Commitment);

/// <summary><c>PAIR_RESPONSE</c> (responder) and <c>PAIR_REVEAL</c> (initiator): an ephemeral key, nonce and device signature.</summary>
public sealed record PairKeyPayload(Guid PairingId, byte[] EphemeralKey, byte[] Nonce, byte[] Signature);

/// <summary><c>PAIR_APPROVE</c>: the user confirmed that the codes match.</summary>
public sealed record PairApprovePayload(Guid PairingId);

/// <summary><c>PAIR_REJECT</c>.</summary>
public sealed record PairRejectPayload(Guid PairingId, string Reason);

public static class PairRejectReasons
{
    /// <summary>The user declined, or said the codes don't match.</summary>
    public const string Declined = "DECLINED";

    /// <summary>The user cancelled before comparing codes.</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>Another pairing is already in progress.</summary>
    public const string Busy = "BUSY";

    public const string Timeout = "TIMEOUT";

    /// <summary>A commitment or signature didn't verify, or a message was out of order.</summary>
    public const string VerificationFailed = "VERIFICATION_FAILED";

    /// <summary>Too many failed attempts; a new session is required.</summary>
    public const string TooManyAttempts = "TOO_MANY_ATTEMPTS";
}

/// <summary>Strict parsing of pairing payloads, including the exact length of every binary field.</summary>
public static class PairingMessages
{
    public static bool TryReadRequest(JsonElement payload, [NotNullWhen(true)] out PairRequestPayload? request)
    {
        request = SessionPayloads.TryDeserialize<PairRequestPayload>(payload);
        if (request is not null && (request.PairingId == Guid.Empty || request.Commitment.Length != PairingProtocol.CommitmentLength))
        {
            request = null;
        }

        return request is not null;
    }

    public static bool TryReadKey(JsonElement payload, [NotNullWhen(true)] out PairKeyPayload? key)
    {
        key = SessionPayloads.TryDeserialize<PairKeyPayload>(payload);
        if (key is not null && (key.PairingId == Guid.Empty
            || key.EphemeralKey.Length != PairingProtocol.EphemeralKeyLength
            || key.Nonce.Length != PairingProtocol.NonceLength
            || key.Signature.Length != PairingProtocol.SignatureLength))
        {
            key = null;
        }

        return key is not null;
    }

    public static bool TryReadApprove(JsonElement payload, [NotNullWhen(true)] out PairApprovePayload? approve)
    {
        approve = SessionPayloads.TryDeserialize<PairApprovePayload>(payload);
        if (approve is not null && approve.PairingId == Guid.Empty)
        {
            approve = null;
        }

        return approve is not null;
    }

    public static bool TryReadReject(JsonElement payload, [NotNullWhen(true)] out PairRejectPayload? reject)
    {
        reject = SessionPayloads.TryDeserialize<PairRejectPayload>(payload);
        if (reject is not null && (reject.PairingId == Guid.Empty || reject.Reason.Length is 0 or > SessionPayloads.MaxReasonLength))
        {
            reject = null;
        }

        return reject is not null;
    }
}
