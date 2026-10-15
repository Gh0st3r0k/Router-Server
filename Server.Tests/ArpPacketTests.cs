using System.Net;
using System.Net.NetworkInformation;
using PacketDotNet;
using Server.Services.Packets;
using Xunit;

namespace Server.Tests;

public sealed class ArpPacketTests
{
    [Fact]
    public void SharedBuilderProducesBroadcastRequestWithCorrectWireAddresses()
    {
        var sourceMac = PhysicalAddress.Parse("02-00-00-00-00-01");
        var sourceIp = IPAddress.Parse("192.0.2.1");
        var targetIp = IPAddress.Parse("192.0.2.2");
        var built = ArpTools.BuildArpRequest(sourceMac, sourceIp, targetIp);
        // Parse serialized bytes so the assertion covers packet layout as well.
        var ethernet = (EthernetPacket)Packet.ParsePacket(LinkLayers.Ethernet, built.Bytes);
        var arp = ethernet.Extract<ArpPacket>();
        Assert.Equal(EthernetType.Arp, ethernet.Type);
        Assert.Equal(sourceMac, ethernet.SourceHardwareAddress);
        Assert.Equal(PhysicalAddress.Parse("FF-FF-FF-FF-FF-FF"), ethernet.DestinationHardwareAddress);
        Assert.Equal(ArpOperation.Request, arp.Operation);
        Assert.Equal(sourceMac, arp.SenderHardwareAddress);
        Assert.Equal(sourceIp, arp.SenderProtocolAddress);
        Assert.Equal(targetIp, arp.TargetProtocolAddress);
        Assert.Equal(PhysicalAddress.Parse("00-00-00-00-00-00"), arp.TargetHardwareAddress);
    }
}
