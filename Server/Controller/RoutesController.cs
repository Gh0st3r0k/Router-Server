#nullable enable
using System.Net;
using Microsoft.AspNetCore.Mvc;
using WANT.Service.Interface;
using WANT.Service.Routing;
using WANT.Service;


namespace Server.Controllers
{
    [ApiController]
    [Route("api/routes")]
    public class RoutesController : ControllerBase
    {
        private readonly IRoutingTable _routingTable;
        private readonly InterfaceScanner _interfaceScanner;

        public RoutesController(IRoutingTable routingTable, InterfaceScanner interfaceScanner)
        {
            _routingTable = routingTable;
            _interfaceScanner = interfaceScanner;
        }

        // DTO для возвращаемых маршрутов
        public sealed class RouteDto
        {
            public string Destination { get; set; } = default!;  // "10.0.0.0/24"
            public string? NextHop { get; set; }                 // "192.168.1.1" или null
            public string OutInterfaceName { get; set; } = default!;
            public int AdministrativeDistance { get; set; }

            /// <summary>
            /// Typ trasy v tabulke – S = Static, C = Connected.
            /// </summary>
            public string Type { get; set; } = "S";
        }

        // DTO для добавления маршрута ПО ИНТЕРФЕЙСУ (directly connected)
        public sealed class AddDirectRouteDto
        {
            /// <summary>
            /// Sieť v tvare "10.0.0.0"
            /// </summary>
            public string Network { get; set; } = default!;

            /// <summary>
            /// Prefix length, napr. 24 pre /24.
            /// </summary>
            public int PrefixLength { get; set; }

            /// <summary>
            /// GUID rozhrania (rovnaký ako v InterfaceScanner/PacketBridgeWorker).
            /// </summary>
            public string OutInterfaceName { get; set; } = default!;

            /// <summary>
            /// Administratívna vzdialenosť.
            /// Pre priamo pripojené siete typicky 0.
            /// </summary>
            public int? AdministrativeDistance { get; set; }
        }

        // DTO для добавления маршрута ПО NEXT-HOP (next-hop alebo kombinovaný variant)
        public sealed class AddViaRouteDto
        {
            /// <summary>
            /// Sieť v tvare "10.0.0.0"
            /// </summary>
            public string Network { get; set; } = default!;

            /// <summary>
            /// Prefix length, napr. 24 pre /24.
            /// </summary>
            public int PrefixLength { get; set; }

            /// <summary>
            /// IP adresa next-hopu (napr. 192.168.1.1).
            /// </summary>
            public string NextHop { get; set; } = default!;

            /// <summary>
            /// GUID rozhrania – voliteľné.
            /// Ak ho poznáš, zadáš kombinovaný variant (interface + next-hop).
            /// Ak ponecháš null/empty → čistý “only next-hop” static.
            /// </summary>
            public string? OutInterfaceName { get; set; }

            /// <summary>
            /// Administratívna vzdialenosť.
            /// Pre statické trasy typicky 1.
            /// </summary>
            public int? AdministrativeDistance { get; set; }
        }

        // DTO для удаления маршрута
        public sealed class RemoveRouteDto
        {
            public string Network { get; set; } = default!;
            public int PrefixLength { get; set; }
            public string OutInterfaceName { get; set; } = default!;
        }

        /// <summary>
        /// GET /api/routes
        /// Vracia aktuálnu smerovaciu tabuľku (statické + connected, ak ich tam pridáš).
        /// </summary>
        [HttpGet]
        public ActionResult<IEnumerable<RouteDto>> GetAll()
        {
            var routes = _routingTable
                .GetAll()
                .Select(r => new RouteDto
                {
                    Destination = $"{r.Destination}/{r.PrefixLength}",
                    NextHop = (r.NextHop == null || r.NextHop.Equals(IPAddress.None))
                                ? null
                                : r.NextHop.ToString(),
                    OutInterfaceName = r.OutInterfaceName,
                    AdministrativeDistance = r.AdministrativeDistance,
                    Type = r.AdministrativeDistance == 0 && (r.NextHop == null || r.NextHop.Equals(IPAddress.None))
                                ? "C"                  // connected-like
                                : r.AdministrativeDistance == 120
                                    ? "R"              // RIP
                                    : "S"             // static / прочие

                })
                .ToList();

            return Ok(routes);
        }

        /// <summary>
        /// POST /api/routes/add-direct
        /// Pridanie trasy podľa rozhrania (priamo pripojená sieť).
        /// NextHop = null => typ "directly connected".
        /// </summary>
        [HttpPost("add-direct")]
        public IActionResult AddDirect([FromBody] AddDirectRouteDto dto)
        {
            if (!IPAddress.TryParse(dto.Network, out var netIp))
                return BadRequest("Invalid Network IP address.");

            if (dto.PrefixLength < 0 || dto.PrefixLength > 32)
                return BadRequest("Invalid PrefixLength.");

            if (string.IsNullOrWhiteSpace(dto.OutInterfaceName))
                return BadRequest("OutInterfaceName is required.");

            var route = new RouteEntry
            {
                Destination = netIp,
                PrefixLength = dto.PrefixLength,
                NextHop = null, // priamo pripojená
                OutInterfaceName = dto.OutInterfaceName.Trim('{', '}').ToUpperInvariant(),
                AdministrativeDistance = dto.AdministrativeDistance ?? 0 // DC = 0
            };

            _routingTable.AddRoute(route);
            return Ok();
        }

        /// <summary>
        /// POST /api/routes/add-via
        /// Pridanie trasy podľa next-hopu alebo kombinovaný variant (interface + next-hop).
        /// </summary>
        [HttpPost("add-via")]
        public IActionResult AddVia([FromBody] AddViaRouteDto dto)
        {
            if (!IPAddress.TryParse(dto.Network, out var netIp))
                return BadRequest("Invalid Network IP address.");

            if (!IPAddress.TryParse(dto.NextHop, out var nhIp))
                return BadRequest("Invalid NextHop IP address.");

            if (dto.PrefixLength < 0 || dto.PrefixLength > 32)
                return BadRequest("Invalid PrefixLength.");

            var outIf = dto.OutInterfaceName?.Trim('{', '}').ToUpperInvariant() ?? string.Empty;

            var route = new RouteEntry
            {
                Destination = netIp,
                PrefixLength = dto.PrefixLength,
                NextHop = nhIp,
                OutInterfaceName = outIf,           // môže byť prázdne – čisto next-hop varianta
                AdministrativeDistance = dto.AdministrativeDistance ?? 1 // statická trasa = 1
            };

            _routingTable.AddRoute(route);
            return Ok();
        }

        /// <summary>
        /// POST /api/routes/add-connected-from-interfaces
        /// Prejde všetky UP ethernet rozhrania z InterfaceScanner
        /// a pridá priamo pripojené siete do routing table (C).
        /// </summary>
        [HttpPost("add-connected-from-interfaces")]
        public IActionResult AddConnectedFromInterfaces()
        {
            var all = _interfaceScanner.GetAll();

            foreach (var ni in all)
            {
                if (ni.Type != NicType.Ethernet || ni.Status != NicStatus.Up)
                    continue;

                // Универсальный парсер
                var ip = ni.Ip;
                var mask = ni.Mask;

                if (ip == null || mask == null)
                    continue;

                var network = CalculateNetwork(ip, mask, out var prefixLength);
                if (network == null)
                    continue;

                var guid = (ni.Guid ?? string.Empty).Trim('{', '}').ToUpperInvariant();

                var route = new RouteEntry
                {
                    Destination = network,
                    PrefixLength = prefixLength,
                    NextHop = null,
                    OutInterfaceName = guid,
                    AdministrativeDistance = 0 // connected
                };

                _routingTable.AddRoute(route);
            }

            return Ok();
        }

        /// <summary>
        /// DELETE /api/routes
        /// Odstránenie trasy (podľa siete + prefixu + rozhrania).
        /// </summary>
        [HttpDelete]
        public IActionResult Remove([FromBody] RemoveRouteDto dto)
        {
            if (!IPAddress.TryParse(dto.Network, out var netIp))
                return BadRequest("Invalid Network IP address.");

            var outIf = dto.OutInterfaceName.Trim('{', '}').ToUpperInvariant();

            _routingTable.RemoveRoute(netIp, dto.PrefixLength, outIf);
            return Ok();
        }

        private static IPAddress? CalculateNetwork(IPAddress ip, IPAddress mask, out int prefixLength)
        {
            prefixLength = 0;

            var ipBytes = ip.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            if (ipBytes.Length != maskBytes.Length) return null;

            var netBytes = new byte[ipBytes.Length];
            for (int i = 0; i < ipBytes.Length; i++)
            {
                netBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
            }

            // počítame prefix length z masky
            foreach (var b in maskBytes)
            {
                for (int bit = 7; bit >= 0; bit--)
                {
                    if ((b & (1 << bit)) != 0)
                        prefixLength++;
                    else
                        // когда дошли до первого нулевого бита – остаток должен быть нули
                        // (предpokladáme validnú masku)
                        continue;
                }
            }

            return new IPAddress(netBytes);
        }
    }
}
