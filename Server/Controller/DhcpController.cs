#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Mvc;
using WANT.Service.Dhcp;

namespace WANT.Controller
{
    [ApiController]
    [Route("api/dhcp")]
    public class DhcpController : ControllerBase
    {
        private readonly IDhcpService _dhcp;

        public DhcpController(IDhcpService dhcp)
        {
            _dhcp = dhcp;
        }

        // ───────────── Глобальные настройки ─────────────

        /// <summary>
        /// Получить глобальные настройки DHCP (lease time, cleanup interval).
        /// </summary>
        [HttpGet("settings")]
        public ActionResult<DhcpSettingsDto> GetSettings()
        {
            var s = _dhcp.GetGlobalSettings();

            var dto = new DhcpSettingsDto
            {
                DynamicLeaseSeconds    = (int)s.DefaultDynamicLeaseTime.TotalSeconds,
                CleanupIntervalSeconds = (int)s.CleanupInterval.TotalSeconds
            };

            return Ok(dto);
        }

        /// <summary>
        /// Обновить глобальные настройки DHCP.
        /// </summary>
        [HttpPut("settings")]
        public IActionResult UpdateSettings([FromBody] DhcpSettingsDto dto)
        {
            if (dto == null)
                return BadRequest("settings is null");

            if (dto.DynamicLeaseSeconds <= 0 || dto.CleanupIntervalSeconds <= 0)
                return BadRequest("Both values must be > 0 seconds");

            var settings = new DhcpGlobalSettings
            {
                DefaultDynamicLeaseTime = TimeSpan.FromSeconds(dto.DynamicLeaseSeconds),
                CleanupInterval         = TimeSpan.FromSeconds(dto.CleanupIntervalSeconds)
            };

            _dhcp.UpdateGlobalSettings(settings);
            return NoContent();
        }

        // ───────────── Пулы ─────────────

        [HttpGet("pools")]
        public ActionResult<IReadOnlyCollection<DhcpPool>> GetPools()
        {
            return Ok(_dhcp.GetPools());
        }

        [HttpGet("pools/{id}")]
        public ActionResult<DhcpPool> GetPool([FromRoute] string id)
        {
            var pool = _dhcp.GetPool(id);
            if (pool is null) return NotFound();
            return Ok(pool);
        }

        [HttpPost("pools")]
        public ActionResult<DhcpPool> CreatePool([FromBody] DhcpPool pool)
        {
            if (pool == null) return BadRequest("pool is null");

            var created = _dhcp.CreatePool(pool);
            return CreatedAtAction(nameof(GetPool), new { id = created.Id }, created);
        }

        [HttpPut("pools/{id}")]
        public ActionResult<DhcpPool> UpdatePool([FromRoute] string id, [FromBody] DhcpPool pool)
        {
            if (pool == null) return BadRequest("pool is null");

            var updated = _dhcp.UpdatePool(id, pool);
            if (updated is null) return NotFound();

            return Ok(updated);
        }

        [HttpDelete("pools/{id}")]
        public IActionResult DeletePool([FromRoute] string id)
        {
            var ok = _dhcp.DeletePool(id);
            if (!ok) return NotFound();
            return NoContent();
        }

        // ───────────── Manual reservations ─────────────

        public sealed class DhcpSettingsDto
        {
            public int DynamicLeaseSeconds { get; set; }
            public int CleanupIntervalSeconds { get; set; }
        }


        public sealed class ManualReservationDto
        {
            public string? Mac { get; set; }
            public string? Ip { get; set; }
        }

        [HttpPost("pools/{poolId}/reservations")]
        public IActionResult AddOrUpdateReservation(
            [FromRoute] string poolId,
            [FromBody] ManualReservationDto dto)
        {
            if (dto == null) return BadRequest("body is null");
            if (string.IsNullOrWhiteSpace(dto.Mac) || string.IsNullOrWhiteSpace(dto.Ip))
                return BadRequest("Mac and Ip are required");

            if (!PhysicalAddress.TryParse(dto.Mac, out var mac))
                return BadRequest("Invalid MAC format");

            if (!IPAddress.TryParse(dto.Ip, out var ip))
                return BadRequest("Invalid IP address");

            var ok = _dhcp.AddOrUpdateManualReservation(poolId, mac, ip);
            if (!ok) return NotFound($"Pool '{poolId}' not found");

            return NoContent();
        }

        [HttpDelete("pools/{poolId}/reservations/{mac}")]
        public IActionResult RemoveReservation(
            [FromRoute] string poolId,
            [FromRoute] string mac)
        {
            if (!PhysicalAddress.TryParse(mac, out var macParsed))
                return BadRequest("Invalid MAC format");

            var ok = _dhcp.RemoveManualReservation(poolId, macParsed);
            if (!ok) return NotFound();

            return NoContent();
        }

        // ───────────── Leases ─────────────

        [HttpGet("leases")]
        public ActionResult<IReadOnlyCollection<DhcpLease>> GetLeases()
        {
            return Ok(_dhcp.GetLeases());
        }

        [HttpGet("pools/{poolId}/leases")]
        public ActionResult<IReadOnlyCollection<DhcpLease>> GetLeasesByPool([FromRoute] string poolId)
        {
            return Ok(_dhcp.GetLeasesByPool(poolId));
        }
    }
}
