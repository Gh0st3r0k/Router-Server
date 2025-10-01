#nullable enable
using System;
using SharpPcap;

namespace WANT.Service.Packets
{
    internal static class NpcapUtil
    {
        /// <summary>Найти pcap-устройство по GUID интерфейса (GUID есть в имени/описании).</summary>
        public static ICaptureDevice? FindDeviceByGuid(string guidUpper)
        {
            foreach (var dev in CaptureDeviceList.Instance)
            {
                var name  = (dev.Name ?? string.Empty).ToUpperInvariant();
                var descr = (dev.Description ?? string.Empty).ToUpperInvariant();
                if (name.Contains(guidUpper) || descr.Contains(guidUpper))
                    return dev;
            }
            return null;
        }
    }
}
