#nullable enable
using System.Net.NetworkInformation;
using System.Net;
using PacketDotNet;


namespace WANT.Service.Dhcp
{
    public interface IDhcpService
    {

        DhcpGlobalSettings GetGlobalSettings();
        void UpdateGlobalSettings(DhcpGlobalSettings settings);

        IReadOnlyCollection<DhcpPool> GetPools();
        DhcpPool? GetPool(string id);
        DhcpPool CreatePool(DhcpPool pool);
        DhcpPool? UpdatePool(string id, DhcpPool pool);
        bool DeletePool(string id);

        bool AddOrUpdateManualReservation(
            string poolId,
            PhysicalAddress mac,
            IPAddress ip);

        bool RemoveManualReservation(string poolId, PhysicalAddress mac);

        IReadOnlyCollection<DhcpLease> GetLeases();
        IReadOnlyCollection<DhcpLease> GetLeasesByPool(string poolId);

        /// <summary>
        /// Returns an OFFER or ACK Ethernet frame for injection,
        /// or null when no response is appropriate.
        /// </summary>
        EthernetPacket? HandleIncomingDhcpPacket(
            string ingressInterfaceId,
            EthernetPacket eth,
            IPv4Packet ip,
            UdpPacket udp);
    }
}
