// WANT.Service.Packets/ArpService.cs
using System;
using System.Net;
using System.Net.NetworkInformation;
using SharpPcap;
using WANT.Service;           // InterfaceScanner
using WANT.Service.Packets;   // PacketBridgeWorker, ArpTools
using Server.Services.Packets;

namespace WANT.Service.Packets
{
    public interface IArpService
    {
        // explicitSrcIp – опционально; по умолчанию берём IP порта из store
        ArpSendResult SendWhoHas(string nicId, IPAddress targetIp, IPAddress? explicitSrcIp = null);
    }

    public sealed record ArpSendResult(
        bool Sent,
        string NicId,
        string SrcIp,
        string TargetIp,
        string SrcMac);

    public sealed class ArpService : IArpService
    {
        private readonly PacketBridgeWorker _bridge;
        private readonly InterfaceScanner _store;

        public ArpService(PacketBridgeWorker bridge, InterfaceScanner store)
        {
            _bridge = bridge;
            _store = store;
        }

        public ArpSendResult SendWhoHas(string nicId, IPAddress targetIp, IPAddress? explicitSrcIp = null)
        {
            // 1) устройство для инжекта
            if (!_bridge.TryGetDevice(nicId, out var dev))
                throw new InvalidOperationException($"NIC '{nicId}' is not opened.");
            var injector = dev as IInjectionDevice
                ?? throw new NotSupportedException($"Device '{nicId}' does not support packet injection.");

            // 2) данные интерфейса из твоего store
            var ni = _store.GetByGuid(nicId)
                ?? throw new InvalidOperationException($"NIC '{nicId}' not found in store.");

            var srcMac = ParseMac(ni.Mac);               // PhysicalAddress
            var srcIp = explicitSrcIp ?? ni.Ip
                ?? throw new InvalidOperationException($"Source IP not configured for NIC '{nicId}' in store.");

            // 3) отправка ARP-запроса
            ArpTools.SendArpRequest(injector, srcMac, srcIp, targetIp);

            return new ArpSendResult(
                Sent: true,
                NicId: nicId,
                SrcIp: srcIp.ToString(),
                TargetIp: targetIp.ToString(),
                SrcMac: srcMac.ToString());
        }

        private static PhysicalAddress ParseMac(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac))
                throw new InvalidOperationException("MAC is empty.");

            // допускаем "00:11:22:33:44:55", "00-11-22-33-44-55", "0011.2233.4455", "001122334455"
            var cleaned = mac.Replace(":", "")
                            .Replace("-", "")
                            .Replace(".", "")
                            .Trim();

            return PhysicalAddress.Parse(cleaned);
        }
    }
}
