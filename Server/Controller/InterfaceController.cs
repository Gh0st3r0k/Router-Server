#nullable enable
using Microsoft.AspNetCore.Mvc;
using System.Net;
using WANT.Controller.Dto;
using WANT.Service;
using WANT.Service.Interface;

namespace WANT.Controller
{
    [ApiController]
    [Route("api/[controller]")]
    public sealed class InterfaceController : ControllerBase
    {
        private readonly InterfaceScanner _store;

        public InterfaceController(InterfaceScanner store)
        {
            _store = store;
        }

        // GET /api/interface
        [HttpGet]
        public IActionResult GetAll()
        {
            var list = _store.GetAll();
            return Ok(list);
        }

        // GET /api/interface/{guid}
        [HttpGet("{guid}")]
        public IActionResult GetByGuid(string guid)
        {
            var ni = _store.GetByGuid(guid);
            if (ni is null) return NotFound(new { error = "Interface not found." });
            return Ok(ni);
        }

        // PUT /api/interface/{guid}/address
        // Устанавливаем адресацию ТОЛЬКО в нашей модели (ОС не трогаем)
        [HttpPut("{guid}/address")]
        public IActionResult SetAddress(string guid, [FromBody] SetAddressRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Ip) || string.IsNullOrWhiteSpace(req.Mask))
                return BadRequest(new { error = "Ip and Mask are required." });

            if (!IsValidIpv4(req.Ip))
                return BadRequest(new { error = "Ip must be a valid IPv4 address." });

            if (!IsValidNetmask(req.Mask, allowSlash0: true, allowSlash32: true))
                return BadRequest(new { error = "Mask must be a valid contiguous IPv4 netmask." });


            var existing = _store.GetByGuid(guid);
            if (existing is null) return NotFound(new { error = "Interface not found." });

            InterfaceManager.SetAddress(guid, req.Ip, req.Mask, _store);

            var updated = _store.GetByGuid(guid);
            return Ok(updated);
        }

        // PUT /api/interface/{guid}/display-name
        [HttpPut("{guid}/display-name")]
        public IActionResult SetDisplayName(string guid, [FromBody] SetDisplayNameRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return BadRequest(new { error = "Name is required." });

            var existing = _store.GetByGuid(guid);
            if (existing is null) return NotFound(new { error = "Interface not found." });

            InterfaceManager.SetDisplayName(guid, req.Name, _store);

            var updated = _store.GetByGuid(guid);
            return Ok(updated);
        }

        // ── helpers ──────────────────────────────────────────────────────────────
        private static bool IsValidIpv4(string ip)
        {
            return IPAddress.TryParse(ip, out var addr)
                   && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        }

        // Разрешаем только корректные «сплошные» маски (255.255.255.0 и т.п.)
        private static bool IsValidNetmask(string mask, bool allowSlash0 = true, bool allowSlash32 = true)
        {
            if (!IPAddress.TryParse(mask, out var addr)) return false;
            if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

            var b = addr.GetAddressBytes();
            uint m = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

            // Разрешать /0 и /32?
            if (!allowSlash0  && m == 0x00000000) return false; // 0.0.0.0
            if (!allowSlash32 && m == 0xFFFFFFFF) return false; // 255.255.255.255

            // Условие «все единицы слева, все нули справа»: (m | (m - 1)) == 0xFFFFFFFF
            // Работает для всех валидных масок, включая неполные байты (254, 252, 248, …)
            return m == 0x00000000         // /0 (если разрешили)
                || m == 0xFFFFFFFF         // /32 (если разрешили)
                || ((m | (m - 1)) == 0xFFFFFFFF);
        }
    }
}
