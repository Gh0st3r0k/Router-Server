using Microsoft.AspNetCore.Mvc;
using WANT.Service.Packets;

namespace Server.Controller
{
    [ApiController]
    [Route("api/arp/cache")]
    public sealed class ArpCacheController : ControllerBase
    {
        private readonly ArpCache _cache;
        public ArpCacheController(ArpCache cache) => _cache = cache;

        public sealed record ArpView(
            string Ip,
            string Mac,
            string Nic,
            double AgeSeconds,
            double? ExpiresInSeconds // null для статических
        );

        /// GET /api/arp/cache
        [HttpGet]
        public IActionResult Get()
        {
            var now = DateTime.UtcNow;
            var items = _cache.Snapshot()
                              .OrderBy(e => e.Ip.ToString())
                              .Select(e => new ArpView(
                                  e.Ip.ToString(),
                                  e.Mac.ToString(),
                                  e.NicId,
                                  (now - e.LearnedAt).TotalSeconds,
                                  e.ExpiresAt == DateTime.MaxValue ? null : (double?)(e.ExpiresAt - now).TotalSeconds
                              ));
            return Ok(items);
        }

        /// POST /api/arp/cache/flush
        [HttpPost("flush")]
        public IActionResult Flush()
        {
            _cache.FlushAll();
            return Ok(new { flushed = true });
        }

        public sealed record TtlDto(int Seconds);

        /// PUT /api/arp/cache/ttl  { "seconds": 180 }
        [HttpPut("ttl")]
        public IActionResult SetTtl([FromBody] TtlDto dto)
        {
            if (dto.Seconds <= 0 || dto.Seconds > 24 * 3600)
                return BadRequest("seconds must be in (0, 86400].");

            _cache.SetTtl(TimeSpan.FromSeconds(dto.Seconds));
            return Ok(new { ttlSeconds = dto.Seconds });
        }

        /// GET /api/arp/cache/ttl
        [HttpGet("ttl")]
        public IActionResult GetTtl()
            => Ok(new { ttlSeconds = (int)_cache.GetTtl().TotalSeconds });
    }
}
