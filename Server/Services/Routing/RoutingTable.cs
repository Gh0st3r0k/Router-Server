using System.Net;

namespace WANT.Service.Routing
{
    public interface IRoutingTable
    {
        void AddRoute(RouteEntry route);
        void RemoveRoute(IPAddress destination, int prefixLength, string outInterfaceName);
        RouteEntry? Lookup(IPAddress destination);
        IReadOnlyCollection<RouteEntry> GetAll();
    }

    public class RoutingTable : IRoutingTable
    {
        private readonly List<RouteEntry> _routes = new();
        private readonly object _lock = new();

        public void AddRoute(RouteEntry newRoute)
        {
            lock (_lock)
            {
                // Keep one route per exact network/prefix. Lower administrative
                // distance wins; an equal-distance update replaces the old route.
                bool hasPreferredRoute = _routes.Any(route =>
                    route.Destination.Equals(newRoute.Destination) &&
                    route.PrefixLength == newRoute.PrefixLength &&
                    route.AdministrativeDistance < newRoute.AdministrativeDistance);
                if (hasPreferredRoute)
                    return;

                _routes.RemoveAll(route =>
                    route.Destination.Equals(newRoute.Destination) &&
                    route.PrefixLength == newRoute.PrefixLength);
                _routes.Add(newRoute);
            }
        }

        public void RemoveRoute(IPAddress destination, int prefixLength, string outInterfaceName)
        {
            lock (_lock)
            {
                _routes.RemoveAll(r =>
                    r.Destination.Equals(destination) &&
                    r.PrefixLength == prefixLength &&
                    r.OutInterfaceName.Equals(outInterfaceName, StringComparison.OrdinalIgnoreCase));
            }
        }

        public IReadOnlyCollection<RouteEntry> GetAll()
        {
            lock (_lock)
                return _routes.ToList().AsReadOnly();
        }

        public RouteEntry? Lookup(IPAddress destination)
        {
            lock (_lock)
            {
                RouteEntry? best = null;

                foreach (var r in _routes)
                {
                    if (!IsMatch(destination, r))
                        continue;

                    // Longest prefix wins before administrative distance.
                    if (best is null || r.PrefixLength > best.PrefixLength ||
                        (r.PrefixLength == best.PrefixLength &&
                         r.AdministrativeDistance < best.AdministrativeDistance))
                    {
                        best = r;
                    }
                }

                return best;
            }
        }

        private static bool IsMatch(IPAddress ip, RouteEntry route)
        {
            var ipBytes = ip.GetAddressBytes();
            var netBytes = route.Destination.GetAddressBytes();
            if (ipBytes.Length != netBytes.Length) return false;

            int fullBytes = route.PrefixLength / 8;
            int remainingBits = route.PrefixLength % 8;

            for (int i = 0; i < fullBytes; i++)
                if (ipBytes[i] != netBytes[i]) return false;

            if (remainingBits > 0)
            {
                int mask = 0xFF << (8 - remainingBits) & 0xFF;
                int ipPart = ipBytes[fullBytes] & mask;
                int netPart = netBytes[fullBytes] & mask;
                if (ipPart != netPart) return false;
            }

            return true;
        }
    }
}
