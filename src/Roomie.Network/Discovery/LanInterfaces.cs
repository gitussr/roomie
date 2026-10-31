using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Roomie.Core.Discovery;

namespace Roomie.Network.Discovery;

/// <summary>One IPv4 address on a network adapter that Roomie is allowed to use for discovery.</summary>
/// <param name="Id">The adapter's stable ID.</param>
/// <param name="Name">The adapter's friendly name, e.g. <c>Wi-Fi</c>.</param>
public sealed record LanInterface(string Id, string Name, Ipv4Subnet Subnet)
{
    public IPAddress Address => Subnet.Address;

    /// <summary>Identifies this address on this adapter; an adapter can carry several addresses.</summary>
    public string Key => $"{Id}|{Subnet}";

    public override string ToString() => $"{Name} ({Subnet})";
}

public interface ILanInterfaceProvider
{
    /// <summary>Raised when adapters or addresses may have changed. Can fire in bursts, on any thread.</summary>
    event EventHandler? InterfacesChanged;

    IReadOnlyList<LanInterface> GetEligibleInterfaces();
}

/// <summary>
/// Lists active adapters with private or link-local IPv4 addresses (architecture.md §2.1, "Network
/// scope"). Loopback and tunnel adapters, public addresses and IPv6 are never used.
/// </summary>
public sealed class SystemLanInterfaceProvider : ILanInterfaceProvider, IDisposable
{
    // /31 and /32 have no broadcast address; anything wider than /8 is not a LAN.
    private const int MinPrefixLength = 8;
    private const int MaxPrefixLength = 30;

    public SystemLanInterfaceProvider()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
    }

    public event EventHandler? InterfacesChanged;

    public IReadOnlyList<LanInterface> GetEligibleInterfaces()
    {
        var result = new List<LanInterface>();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = adapter.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                // The adapter disappeared while we were looking at it.
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork
                    || !Ipv4Subnet.IsLanAddress(address)
                    || unicast.PrefixLength is < MinPrefixLength or > MaxPrefixLength)
                {
                    continue;
                }

                result.Add(new LanInterface(adapter.Id, adapter.Name, new Ipv4Subnet(address, unicast.PrefixLength)));
            }
        }

        return result;
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => InterfacesChanged?.Invoke(this, EventArgs.Empty);
}
