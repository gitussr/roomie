namespace Roomie.Infrastructure.Identity;

/// <summary>
/// The identity on disk is incomplete or inconsistent. Unlike a foreign (undecryptable) identity this is
/// never repaired automatically: regenerating would silently break every existing pairing.
/// </summary>
public sealed class IdentityStoreException : Exception
{
    public IdentityStoreException()
    {
    }

    public IdentityStoreException(string message)
        : base(message)
    {
    }

    public IdentityStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
