using System.Net;
using WANT.Service.Routing;
using Xunit;

namespace Server.Tests;

public sealed class RoutingTests
{
    private static RouteEntry Route(string network, int prefix, int distance = 0,
        string nic = "LAN", string? nextHop = null) => new()
        {
            Destination = IPAddress.Parse(network),
            PrefixLength = prefix,
            AdministrativeDistance = distance,
            OutInterfaceName = nic,
            NextHop = nextHop is null ? null : IPAddress.Parse(nextHop)
        };

    [Theory]
    [InlineData(0, 120, false)]
    [InlineData(120, 1, true)]
    [InlineData(1, 1, true)]
    public void ExactPrefixKeepsLowerDistanceAndReplacesEqualDistance(
        int oldDistance, int newDistance, bool replaced)
    {
        var table = new RoutingTable();
        var original = Route("10.0.0.0", 24, oldDistance);
        var update = Route("10.0.0.0", 24, newDistance, "WAN");
        table.AddRoute(original);
        table.AddRoute(update);
        Assert.Same(replaced ? update : original, Assert.Single(table.GetAll()));
    }

    [Theory]
    [InlineData("10.0.0.127", "SUBNET")]
    [InlineData("10.0.0.128", "LAN")]
    [InlineData("192.0.2.1", "DEFAULT")]
    [InlineData("10.0.0.42", "HOST")]
    public void LookupUsesLongestPrefixBeforeDistance(string address, string expectedInterface)
    {
        var table = new RoutingTable();
        table.AddRoute(Route("0.0.0.0", 0, 1, "DEFAULT"));
        table.AddRoute(Route("10.0.0.0", 24, 0));
        table.AddRoute(Route("10.0.0.0", 25, 120, "SUBNET"));
        table.AddRoute(Route("10.0.0.42", 32, 120, "HOST"));
        Assert.Equal(expectedInterface, table.Lookup(IPAddress.Parse(address))?.OutInterfaceName);
        Assert.Null(table.Lookup(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void RemovalMatchesNetworkPrefixAndCaseInsensitiveInterface()
    {
        var table = new RoutingTable();
        var retained = Route("10.0.0.0", 25);
        table.AddRoute(Route("10.0.0.0", 24));
        table.AddRoute(retained);
        table.RemoveRoute(IPAddress.Parse("10.0.0.0"), 24, "lan");
        Assert.Same(retained, Assert.Single(table.GetAll()));
    }

    [Fact]
    public void RecursiveNextHopResolvesFinalNeighborAndInterface()
    {
        var table = new RoutingTable();
        table.AddRoute(Route("203.0.113.0", 24, 1, nextHop: "192.0.2.1"));
        table.AddRoute(Route("192.0.2.0", 24, 1, nextHop: "10.0.0.1"));
        table.AddRoute(Route("10.0.0.0", 24, nic: "EGRESS"));
        var decision = new RoutingService(table).Resolve(IPAddress.Parse("203.0.113.5"));
        Assert.Equal("EGRESS", decision.OutInterfaceName);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), decision.NextHop);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrCyclicNextHopReturnsNoDecision(bool cyclic)
    {
        var table = new RoutingTable();
        if (cyclic) table.AddRoute(Route("0.0.0.0", 0, 1, nextHop: "192.0.2.1"));
        var decision = new RoutingService(table).Resolve(IPAddress.Parse("203.0.113.5"));
        Assert.Null(decision.Route);
        Assert.Null(decision.NextHop);
    }
}
