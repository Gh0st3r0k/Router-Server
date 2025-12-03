// Server/Controllers/RipController.cs
#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using WANT.Service.Rip;

namespace Server.Controllers
{
    [ApiController]
    [Route("api/rip")]
    public sealed class RipController : ControllerBase
    {
        private readonly IRipService _rip;

        public RipController(IRipService rip)
        {
            _rip = rip;
        }

        public sealed class RipTimersDto
        {
            public int UpdateSeconds { get; set; }
            public int InvalidSeconds { get; set; }
            public int HolddownSeconds { get; set; }
            public int FlushSeconds { get; set; }
        }

        public sealed class InterfaceToggleDto
        {
            public string InterfaceId { get; set; } = default!;
        }

        [HttpGet("timers")]
        public ActionResult<RipTimersDto> GetTimers()
        {
            var t = _rip.GetTimers();
            return Ok(new RipTimersDto
            {
                UpdateSeconds = (int)t.UpdateInterval.TotalSeconds,
                InvalidSeconds = (int)t.InvalidInterval.TotalSeconds,
                HolddownSeconds = (int)t.HolddownInterval.TotalSeconds,
                FlushSeconds = (int)t.FlushInterval.TotalSeconds
            });
        }

        [HttpPut("timers")]
        public IActionResult UpdateTimers([FromBody] RipTimersDto dto)
        {
            var t = new RipTimers
            {
                UpdateInterval = TimeSpan.FromSeconds(dto.UpdateSeconds),
                InvalidInterval = TimeSpan.FromSeconds(dto.InvalidSeconds),
                HolddownInterval = TimeSpan.FromSeconds(dto.HolddownSeconds),
                FlushInterval = TimeSpan.FromSeconds(dto.FlushSeconds)
            };

            _rip.UpdateTimers(t);
            return Ok();
        }

        [HttpGet("interfaces")]
        public ActionResult<IEnumerable<RipInterfaceState>> GetInterfaces()
        {
            return Ok(_rip.GetInterfaces());
        }

        [HttpPost("enable")]
        public IActionResult Enable([FromBody] InterfaceToggleDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.InterfaceId))
                return BadRequest("InterfaceId is required");

            _rip.EnableOnInterface(dto.InterfaceId);
            return Ok();
        }

        [HttpPost("disable")]
        public IActionResult Disable([FromBody] InterfaceToggleDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.InterfaceId))
                return BadRequest("InterfaceId is required");

            _rip.DisableOnInterface(dto.InterfaceId);
            return Ok();
        }
    }
}
