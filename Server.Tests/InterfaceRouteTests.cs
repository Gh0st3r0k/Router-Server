using System.Net;
using Server.Controllers;
using WANT.Service;
using WANT.Service.Interface;
using WANT.Service.Routing;
using Xunit;

namespace Server.Tests;

public sealed class InterfaceRouteTests
{
    [Fact]
    public void ConnectedRoutesUseConfiguredModelAddressesAndIgnoreDownInterfaces()
    {
        var store = new InterfaceScanner();
        var up = new NetInterface("ABC", "Adapter", "Ethernet", "LAN", NicType.Ethernet,
            "020000000001", NicStatus.Up, "LAN", null, null, null, TrackState.Unconfigured);
        InterfaceManager.Sync(new[] { up, up with { Guid = "DOWN", Status = NicStatus.Down } }, store);
        InterfaceManager.SetAddress("ABC", "192.0.2.129", "255.255.255.128", store);
        InterfaceManager.SetAddress("DOWN", "198.51.100.1", "255.255.255.0", store);
        var table = new RoutingTable();
        var controller = new RoutesController(table, store);
        controller.AddConnectedFromInterfaces();
        controller.AddConnectedFromInterfaces();
        var route = Assert.Single(table.GetAll());
        Assert.Equal(IPAddress.Parse("192.0.2.128"), route.Destination);
        Assert.Equal(25, route.PrefixLength);
        Assert.Equal("ABC", route.OutInterfaceName);
        Assert.Equal(0, route.AdministrativeDistance);
        Assert.Null(route.NextHop);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SnapshotRefreshPreservesConfigurationAndHandlesMissingInterfaces(bool hardDelete)
    {
        var store = new InterfaceScanner();
        var adapter = new NetInterface("ABC", "Adapter", "Ethernet", "LAN", NicType.Ethernet,
            "020000000001", NicStatus.Up, "LAN", null, null, null, TrackState.Unconfigured);
        InterfaceManager.Sync(new[] { adapter }, store);
        InterfaceManager.SetAddress("ABC", "192.0.2.1", "255.255.255.0", store);
        InterfaceManager.SetDisplayName("ABC", "User LAN", store);
        InterfaceManager.Sync(new[] { adapter with { Description = "Updated adapter" } }, store);
        var updated = Assert.Single(store.GetAll());
        Assert.Equal("User LAN", updated.UserName);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), updated.Ip);
        Assert.Equal("Updated adapter", updated.Description);
        InterfaceManager.Sync(Array.Empty<NetInterface>(), store, hardDeleteMissing: hardDelete);
        if (hardDelete)
        {
            Assert.Empty(store.GetAll());
        }
        else
        {
            var removed = Assert.Single(store.GetAll());
            Assert.Null(removed.Ip);
            Assert.Null(removed.Mask);
            Assert.Null(removed.PrefixLen);
            Assert.Equal(TrackState.Removed, removed.TrackState);
            Assert.Equal(NicStatus.Unknown, removed.Status);
        }
    }

}
