using PacketDotNet;

namespace WANT.Service.Rip
{
    public interface IRipService
    {
        RipTimers GetTimers();
        void UpdateTimers(RipTimers timers);

        IReadOnlyCollection<RipInterfaceState> GetInterfaces();

        void EnableOnInterface(string interfaceId);
        void DisableOnInterface(string interfaceId);

        /// <summary>
        /// Вызывается при получении UDP-пакета на порт 520 (RIPv2).
        /// </summary>
        void HandleIncomingRipPacket(string ingressInterfaceId, IPv4Packet ip, UdpPacket udp);
    }
}
