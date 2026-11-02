using System.Net.NetworkInformation;
using Makaretu.Dns;

namespace UniversalCam.Core.Discovery;

/// <summary>
/// Advertises this PC on the LAN as "_universalcam._tcp" so the iPhone's
/// BonjourDiscovery.swift can find it automatically.
///
/// Uses Makaretu.Dns.Multicast (mDNS/DNS-SD, RFC 6762 / RFC 6763).
/// The service record points at port 7779 (QUIC transport).
/// </summary>
public sealed class BonjourService : IAsyncDisposable
{
    private const string ServiceType   = "_universalcam._tcp";
    private const int    AdvertisePort = 7779;

    private readonly MulticastService _mdns;
    private readonly ServiceDiscovery _sd;
    private ServiceProfile?           _profile;

    public BonjourService()
    {
        // Only use active, non-loopback interfaces that support multicast.
        // This prevents binding to virtual adapters, Hyper-V switches, etc.
        // which can cause mDNS packets to be sent on the wrong interface.
        _mdns = new MulticastService(ifaces =>
            ifaces.Where(nic =>
                nic.OperationalStatus == OperationalStatus.Up &&
                nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                nic.SupportsMulticast &&
                nic.GetIPProperties().UnicastAddresses
                    .Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)));
        _sd = new ServiceDiscovery(_mdns);
    }

    /// <summary>
    /// Start advertising. Call once; safe to call multiple times (idempotent).
    /// </summary>
    public void Start()
    {
        if (_profile is not null) return;

        _profile = new ServiceProfile(
            System.Net.Dns.GetHostName(),
            ServiceType,
            (ushort)AdvertisePort);

        _profile.AddProperty("version",  "1");
        _profile.AddProperty("platform", "windows");

        _sd.Advertise(_profile);
        _mdns.Start();

        Console.WriteLine($"[BonjourService] Advertising {ServiceType} on port {AdvertisePort}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_profile is not null)
        {
            _sd.Unadvertise(_profile);
            _profile = null;
        }
        _mdns.Stop();
        _sd.Dispose();
        _mdns.Dispose();
        await Task.CompletedTask;
    }
}
