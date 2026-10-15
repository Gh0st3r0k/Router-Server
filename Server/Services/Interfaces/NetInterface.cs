#nullable enable
using System.Net;


namespace WANT.Service.Interface
{
    /// <summary>Тип интерфейса (сильно упрощённо для наших задач).</summary>
    public enum NicType { Ethernet, Wireless, Loopback, Other }

    /// <summary>Статус линка по данным ОС.</summary>
    public enum NicStatus { Up, Down, Unknown }

    /// <summary>Состояние учёта в нашей системе.</summary>
    public enum TrackState { Active, Unconfigured, Removed }

    /// <summary>Модель сетевого интерфейса в нашей системе.</summary>
    public sealed record NetInterface(
        string Guid,
        string Description,
        string OSName,
        string FriendlyName,
        NicType Type,
        string Mac,
        NicStatus Status,
        string UserName,        // по умолчанию = Description
        IPAddress? Ip,
        IPAddress? Mask,
        int? PrefixLen,
        TrackState TrackState   // Active/Unconfigured/Removed
    )
    {

        public bool IsUp => Status == NicStatus.Up;
    }
}
