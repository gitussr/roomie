using Roomie.Core.Identity;

namespace Roomie.Infrastructure.Identity;

public enum IdentityLoadOutcome
{
    /// <summary>The existing identity was loaded unchanged.</summary>
    Loaded,

    /// <summary>No identity existed (first launch), so a new one was generated.</summary>
    Created,

    /// <summary>
    /// The stored private key could not be decrypted by this Windows user on this computer, which means
    /// the identity was copied from elsewhere. It was moved aside and a new identity generated; paired
    /// devices will see an identity change and must re-pair.
    /// </summary>
    RegeneratedForeignIdentity,
}

public sealed record IdentityLoadResult(DeviceIdentity Identity, IdentityLoadOutcome Outcome, string? OrphanedDirectory = null);
