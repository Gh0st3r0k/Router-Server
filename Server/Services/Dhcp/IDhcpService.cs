#nullable enable
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using PacketDotNet;

namespace WANT.Service.Dhcp
{
    public interface IDhcpService
    {
        // ── Глобальные настройки ────────────────────────────────────────────
        DhcpGlobalSettings GetGlobalSettings();
        void UpdateGlobalSettings(DhcpGlobalSettings settings);

        // ── Пулы ────────────────────────────────────────────────────────────
        IReadOnlyCollection<DhcpPool> GetPools();
        DhcpPool? GetPool(string id);
        DhcpPool CreatePool(DhcpPool pool);
        DhcpPool? UpdatePool(string id, DhcpPool pool);
        bool DeletePool(string id);

        // ── Manual reservations ────────────────────────────────────────────
        bool AddOrUpdateManualReservation(
            string poolId,
            PhysicalAddress mac,
            IPAddress ip);

        bool RemoveManualReservation(string poolId, PhysicalAddress mac);

        // ── Leases ─────────────────────────────────────────────────────────
        IReadOnlyCollection<DhcpLease> GetLeases();
        IReadOnlyCollection<DhcpLease> GetLeasesByPool(string poolId);

        // ── Обработка DHCP-пакетов (вызывается из PacketBridgeWorker) ─────
        /// <summary>
        /// Возвращает Ethernet-фрейм с DHCP-ответом (OFFER/ACK/NAK),
        /// либо null, если отвечать не надо/не можем.
        /// </summary>
        EthernetPacket? HandleIncomingDhcpPacket(
            string ingressInterfaceId,
            EthernetPacket eth,
            IPv4Packet ip,
            UdpPacket udp);
    }
}
