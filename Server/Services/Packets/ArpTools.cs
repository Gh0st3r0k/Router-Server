// Server/Services/Packets/ArpTools.cs
using PacketDotNet;
using SharpPcap;
using System;
using System.Net;
using System.Net.NetworkInformation;

namespace Server.Services.Packets
{
    public static class ArpTools
    {
        public static void SendArpRequest(
            IInjectionDevice dev,            // <-- было ICaptureDevice
            PhysicalAddress srcMac,
            IPAddress srcIp,
            IPAddress targetIp)
        {
            if (dev is null) throw new ArgumentNullException(nameof(dev));
            if (srcMac is null) throw new ArgumentNullException(nameof(srcMac));
            if (srcIp is null) throw new ArgumentNullException(nameof(srcIp));
            if (targetIp is null) throw new ArgumentNullException(nameof(targetIp));

            var eth = new EthernetPacket(
                srcMac,
                PhysicalAddress.Parse("FF-FF-FF-FF-FF-FF"),
                EthernetType.Arp);

            var arp = new ArpPacket(
                ArpOperation.Request,
                PhysicalAddress.Parse("00-00-00-00-00-00"), // THA
                targetIp,                                    // TPA
                srcMac,                                      // SHA
                srcIp);                                       // SPA

            eth.PayloadPacket = arp;

            // ВАЖНО: extension SendPacket(IInjectionDevice, Packet)
            dev.SendPacket(eth);
        }
    }
}
