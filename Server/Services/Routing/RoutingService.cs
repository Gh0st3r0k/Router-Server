using System.Net;

namespace WANT.Service.Routing
{
    /// <summary>
    /// Resolved egress route and directly reachable ARP target.
    /// </summary>
    public class RoutingDecision
    {
        /// <summary>
        /// Final direct route at the end of recursive next-hop resolution.
        /// </summary>
        public RouteEntry? Route { get; set; }

        /// <summary>
        /// Address to resolve using ARP.
        /// For a direct destination, this is the destination host.
        /// For a recursive route, this is the final directly reachable neighbor.
        /// </summary>
        public IPAddress? NextHop { get; set; }

        /// <summary>
        /// Egress interface GUID.
        /// Taken from the final direct route, not the original recursive route.
        /// </summary>
        public string? OutInterfaceName => Route?.OutInterfaceName;
    }

    public interface IRoutingService
    {
        /// <summary>
        /// Resolve a destination through at most eight next-hop lookups.
        /// </summary>
        RoutingDecision Resolve(IPAddress finalDestination);
    }

    public class RoutingService : IRoutingService
    {
        private readonly IRoutingTable _routingTable;

        public RoutingService(IRoutingTable routingTable)
        {
            _routingTable = routingTable;
        }

        public RoutingDecision Resolve(IPAddress finalDestination)
        {
            var decision = new RoutingDecision();
            var currentTarget = finalDestination;

            const int maxHops = 8;
            int hops = 0;

            while (hops++ < maxHops)
            {
                var route = _routingTable.Lookup(currentTarget);
                if (route == null)
                {
                    // A missing route at any recursion step invalidates the decision.
                    decision.Route = null;
                    decision.NextHop = null;
                    return decision;
                }

                // A direct route terminates recursion.
                if (route.NextHop == null || route.NextHop.Equals(IPAddress.None))
                {
                    decision.Route = route;
                    // Resolve the current target, which may be the original host or a neighbor.
                    decision.NextHop = currentTarget;
                    return decision;
                }

                // Resolve the gateway recursively; its direct route determines egress.
                decision.Route = route;
                currentTarget = route.NextHop;
            }

            // Bound recursion to terminate cycles and overly long next-hop chains.
            decision.Route = null;
            decision.NextHop = null;
            return decision;
        }
    }
}
