using System.Net.NetworkInformation;
using System.Net;
using PacketDotNet;
using SharpPcap;

namespace Server.Services.Packets
{
    public static class ArpTools
    {
        public static void SendArpRequest(
            IInjectionDevice dev,
            PhysicalAddress srcMac,
            IPAddress srcIp,
            IPAddress targetIp)
        {
            ArgumentNullException.ThrowIfNull(dev);
            dev.SendPacket(BuildArpRequest(srcMac, srcIp, targetIp));
        }

        /// <summary>Builds a broadcast Ethernet ARP request without accessing a capture device.</summary>
        public static EthernetPacket BuildArpRequest(
            PhysicalAddress srcMac, IPAddress srcIp, IPAddress targetIp)
        {
            ArgumentNullException.ThrowIfNull(srcMac);
            ArgumentNullException.ThrowIfNull(srcIp);
            ArgumentNullException.ThrowIfNull(targetIp);

            var ethernet = new EthernetPacket(
                srcMac, PhysicalAddress.Parse("FF-FF-FF-FF-FF-FF"), EthernetType.Arp);
            ethernet.PayloadPacket = new ArpPacket(
                ArpOperation.Request,
                PhysicalAddress.Parse("00-00-00-00-00-00"),
                targetIp, srcMac, srcIp);
            return ethernet;
        }
    }
}
