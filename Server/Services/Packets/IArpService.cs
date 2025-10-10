// Server/Services/Packets/IArpService.cs
using System.Net;
using System.Threading.Tasks;

namespace Server.Services.Packets
{
    public interface IArpService
    {
        Task<ArpSendResult> SendWhoHasAsync(string nicId, IPAddress targetIp, IPAddress? explicitSrcIp = null);
    }

    public sealed record ArpSendResult(
        bool Sent,
        string NicId,
        string SrcIp,
        string TargetIp,
        string SrcMac);
}
