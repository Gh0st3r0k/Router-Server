using System.Buffers.Binary;
using System.Net;
using PacketDotNet;
using WANT.Service;
using WANT.Service.Rip;
using WANT.Service.Routing;
using Xunit;

namespace Server.Tests;

public sealed class RipTests
{
    private static UdpPacket Response(int metric)
    {
        var payload = new byte[24];
        payload[0] = 2; // Response.
        payload[1] = 2; // RIPv2.
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), 2);
        IPAddress.Parse("203.0.113.0").GetAddressBytes().CopyTo(payload, 8);
        IPAddress.Parse("255.255.255.0").GetAddressBytes().CopyTo(payload, 12);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20), (uint)metric);
        return new UdpPacket(520, 520) { PayloadData = payload };
    }

    [Fact]
    public void LearnsEnabledInterfaceRouteAndWithdrawsMetricSixteen()
    {
        var table = new RoutingTable();
        using var rip = new RipService(table, new InterfaceScanner());
        rip.EnableOnInterface("{LAN}");
        var ip = new IPv4Packet(IPAddress.Parse("192.0.2.1"), IPAddress.Parse("224.0.0.9"));
        rip.HandleIncomingRipPacket("LAN", ip, Response(2));
        var route = Assert.Single(table.GetAll());
        Assert.Equal(120, route.AdministrativeDistance);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), route.NextHop);
        Assert.Equal("LAN", route.OutInterfaceName);
        rip.HandleIncomingRipPacket("LAN", ip, Response(16));
        Assert.Empty(table.GetAll());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void IgnoresInvalidMetrics(int metric)
    {
        var table = new RoutingTable();
        using var rip = new RipService(table, new InterfaceScanner());
        rip.EnableOnInterface("LAN");
        rip.HandleIncomingRipPacket("LAN",
            new IPv4Packet(IPAddress.Parse("192.0.2.1"), IPAddress.Parse("224.0.0.9")), Response(metric));
        Assert.Empty(table.GetAll());
    }

    [Fact]
    public void DisabledInterfacesDoNotLearnRoutes()
    {
        var table = new RoutingTable();
        using var rip = new RipService(table, new InterfaceScanner());
        rip.HandleIncomingRipPacket("LAN",
            new IPv4Packet(IPAddress.Parse("192.0.2.1"), IPAddress.Parse("224.0.0.9")), Response(2));
        Assert.Empty(table.GetAll());
    }
}
