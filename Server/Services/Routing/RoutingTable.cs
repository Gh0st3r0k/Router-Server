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

    /// <summary>
    /// Простая таблица маршрутизации в памяти.
    /// </summary>
    public class RoutingTable : IRoutingTable
    {
        private readonly List<RouteEntry> _routes = new();

        public void AddRoute(RouteEntry route)
        {
            _routes.Add(route);
        }

        public void RemoveRoute(IPAddress destination, int prefixLength, string outInterfaceName)
        {
            _routes.RemoveAll(r =>
                r.Destination.Equals(destination) &&
                r.PrefixLength == prefixLength &&
                r.OutInterfaceName == outInterfaceName);
        }

        public IReadOnlyCollection<RouteEntry> GetAll() => _routes.AsReadOnly();

        /// <summary>
        /// Longest Prefix Match + administrative distance.
        /// </summary>
        public RouteEntry? Lookup(IPAddress destination)
        {
            RouteEntry? best = null;

            foreach (var r in _routes)
            {
                if (!IsMatch(destination, r))
                    continue;

                if (best == null)
                {
                    best = r;
                    continue;
                }

                // 1) Максимальная длина префикса
                if (r.PrefixLength > best.PrefixLength)
                {
                    best = r;
                }
                // 2) При одинаковой маске берём меньшую administrative distance
                else if (r.PrefixLength == best.PrefixLength &&
                         r.AdministrativeDistance < best.AdministrativeDistance)
                {
                    best = r;
                }
            }

            return best;
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
