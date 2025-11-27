using System.Net;

namespace WANT.Service.Routing
{
    /// <summary>
    /// Результат полного разрешения маршрута.
    /// </summary>
    public class RoutingDecision
    {
        /// <summary>
        /// Маршрут, который в итоге выбрали (последний в цепочке next-hop-ов).
        /// </summary>
        public RouteEntry? Route { get; set; }

        /// <summary>
        /// IP next-hop-а, по которому нужно сделать ARP.
        /// - Для directly connected: это конечный IP назначения (host).
        /// - Для статиков через next-hop: конечный IP соседа.
        /// </summary>
        public IPAddress? NextHop { get; set; }

        /// <summary>
        /// Исходящий интерфейс (GUID).
        /// Берётся из Route.OutInterfaceName последнего маршрута в цепочке.
        /// </summary>
        public string? OutInterfaceName => Route?.OutInterfaceName;
    }

    public interface IRoutingService
    {
        /// <summary>
        /// Полное разрешение маршрута до конечного назначения с учётом цепочки next-hop-ов.
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
                    // ничего не нашли
                    decision.Route = null;
                    decision.NextHop = null;
                    return decision;
                }

                // directly connected: дальше не идём
                if (route.NextHop == null || route.NextHop.Equals(IPAddress.None))
                {
                    decision.Route = route;
                    // ARP делаем по конечному IP (host-адресу)
                    decision.NextHop = currentTarget;
                    return decision;
                }

                // иначе: next-hop сам должен быть достижим – переходим на него
                decision.Route = route;
                currentTarget = route.NextHop;
            }

            // слишком длинная цепочка – считаем, что что-то не так
            decision.Route = null;
            decision.NextHop = null;
            return decision;
        }
    }
}
