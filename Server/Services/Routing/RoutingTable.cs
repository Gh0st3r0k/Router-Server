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
                // Находим ВСЕ маршруты в эту же сеть/префикс (не важно какой next-hop)
                var sameNetwork = _routes
                    .Where(r =>
                        r.Destination.Equals(newRoute.Destination) &&
                        r.PrefixLength == newRoute.PrefixLength)
                    .ToList();

                if (sameNetwork.Count > 0)
                {
                    // Находим лучший существующий (минимальный AD)
                    var best = sameNetwork
                        .OrderBy(r => r.AdministrativeDistance)
                        .First();

                    // 1) Если существующий лучше — новый игнорируем
                    if (best.AdministrativeDistance < newRoute.AdministrativeDistance)
                        return;

                    // 2) Если AD одинаковые — заменяем существующие на новый
                    //    (ECMP и многократные дубли нам не нужны)
                    foreach (var r in sameNetwork)
                        _routes.Remove(r);
                }

                // Добавляем новый лучший маршрут
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

                    if (best == null)
                    {
                        best = r;
                        continue;
                    }

                    // 1) LPM
                    if (r.PrefixLength > best.PrefixLength)
                    {
                        best = r;
                        continue;
                    }

                    if (r.PrefixLength == best.PrefixLength)
                    {
                        // 2) Administrative Distance
                        if (r.AdministrativeDistance < best.AdministrativeDistance)
                        {
                            best = r;
                            continue;
                        }
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
