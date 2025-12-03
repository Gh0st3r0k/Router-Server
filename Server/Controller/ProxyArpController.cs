using Microsoft.AspNetCore.Mvc;
using WANT.Service.Packets;

[ApiController]
[Route("api/proxy-arp")]
public class ProxyArpController : ControllerBase
{
    private readonly PacketBridgeWorker _bridge;

    public ProxyArpController(PacketBridgeWorker bridge)
    {
        _bridge = bridge;
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        var states = _bridge.GetProxyArpStates()
            .Select(s => new
            {
                interfaceId = s.InterfaceId,
                enabled = s.Enabled
            });

        return Ok(states);
    }

    public sealed class ProxyArpUpdateDto
    {
        public bool Enabled { get; set; }
    }

    [HttpPost("{interfaceId}")]
    public IActionResult Set(string interfaceId, [FromBody] ProxyArpUpdateDto body)
    {
        _bridge.SetProxyArp(interfaceId, body.Enabled);
        return NoContent();
    }
}
