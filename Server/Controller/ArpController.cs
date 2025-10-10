// Server/Controller/ArpController.cs
using Microsoft.AspNetCore.Mvc;
using System.Net;
using WANT.Service.Packets;

namespace Server.Controller
{
    [ApiController]
    [Route("api/[controller]")]
    public sealed class ArpController : ControllerBase
    {
        private readonly IArpService _arp;
        public ArpController(IArpService arp) => _arp = arp;

        public sealed class ArpRequestDto
        {
            public string NicId { get; set; } = default!;
            public string TargetIp { get; set; } = default!;
            public string? SrcIp { get; set; }
        }

        [HttpPost("request")]
        public IActionResult SendRequest([FromBody] ArpRequestDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.NicId))
                return BadRequest("NicId is required.");
            if (!IPAddress.TryParse(dto.TargetIp, out var target))
                return BadRequest("TargetIp is invalid.");

            IPAddress? src = null;
            if (!string.IsNullOrWhiteSpace(dto.SrcIp) && !IPAddress.TryParse(dto.SrcIp, out src))
                return BadRequest("SrcIp is invalid.");

            var res = _arp.SendWhoHas(dto.NicId, target, src);
            return Ok(res);
        }
    }
}
