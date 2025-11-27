using System.Net;

namespace WANT.Service.Routing
{
    /// <summary>
    /// Одна запись в таблице маршрутизации.
    /// </summary>
    public class RouteEntry
    {
        /// <summary>
        /// Сеть-назначение (например 10.0.0.0).
        /// </summary>
        public IPAddress Destination { get; set; } = IPAddress.Any;

        /// <summary>
        /// Длина префикса (например 24 для /24).
        /// </summary>
        public int PrefixLength { get; set; }

        /// <summary>
        /// Next-hop (IP следующего роутера).
        /// null или IPAddress.None => сеть напрямую подключена.
        /// </summary>
        public IPAddress? NextHop { get; set; }

        /// <summary>
        /// GUID исходящего интерфейса (как у тебя в InterfaceScanner/PacketBridgeWorker).
        /// Может быть пустой строкой для чисто next-hop статиков.
        /// </summary>
        public string OutInterfaceName { get; set; } = string.Empty;

        /// <summary>
        /// Административная дистанция.
        /// Меньше число = больший приоритет.
        /// Например:
        /// 0 - directly connected
        /// 1 - static route
        /// 100+ - dynamic и т.д.
        /// </summary>
        public int AdministrativeDistance { get; set; }
    
    }
}
