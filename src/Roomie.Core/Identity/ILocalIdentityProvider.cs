namespace Roomie.Core.Identity;

/// <summary>Read access to this computer's current public identity, including the latest username.</summary>
public interface ILocalIdentityProvider
{
    DeviceIdentity Identity { get; }
}
