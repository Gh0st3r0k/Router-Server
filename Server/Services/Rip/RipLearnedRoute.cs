// WANT.Service/Rip/RipLearnedRoute.cs
using System;
using System.Net;
using WANT.Service.Routing;

namespace WANT.Service.Rip
{
    /// <summary>
    /// Внутреннее состояние маршрута, выученного от RIPv2 соседа.
    /// </summary>
    internal sealed class RipLearnedRoute
    {
        public IPAddress Destination { get; set; } = IPAddress.Any;
        public int PrefixLength { get; set; }

        /// <summary>
        /// Next-hop, полученный в RIP (обычно IP соседа).
        /// </summary>
        public IPAddress NextHop { get; set; } = IPAddress.Any;

        /// <summary>
        /// Метрика RIP (1..16).
        /// </summary>
        public int Metric { get; set; }

        /// <summary>
        /// С какого интерфейса (GUID) выучили эту сеть (для Split Horizon + таймеров).
        /// </summary>
        public string LearnedOnInterfaceId { get; set; } = string.Empty;

        /// <summary>
        /// Время последнего валидного обновления для этой сети.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; }

        /// <summary>
        /// Сейчас в состоянии hold-down?
        /// </summary>
        public bool InHolddown { get; set; }

        /// <summary>
        /// Ссылка на запись в общей RoutingTable, чтобы можно было обновить/удалить.
        /// </summary>
        public RouteEntry? InstalledRoute { get; set; }

        /// <summary>
        /// Время, когда маршрут перешёл в состояние holddown.
        /// </summary>
        public DateTime? HolddownStartUtc { get; set; }

    }
}
