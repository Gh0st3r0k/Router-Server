// WANT.Service/Rip/RipTimers.cs
using System;

namespace WANT.Service.Rip
{
    /// <summary>
    /// Конфиг таймеров RIPv2 (в секундах по умолчанию как у Cisco).
    /// </summary>
    public sealed class RipTimers
    {
        public TimeSpan UpdateInterval { get; set; } = TimeSpan.FromSeconds(30);   // update
        public TimeSpan InvalidInterval { get; set; } = TimeSpan.FromSeconds(180); // invalid
        public TimeSpan HolddownInterval { get; set; } = TimeSpan.FromSeconds(180); // holddown
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(240);   // flush
    }
}
