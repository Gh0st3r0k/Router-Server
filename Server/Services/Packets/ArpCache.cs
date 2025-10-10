// WANT.Service.Packets/ArpCache.cs
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;

namespace WANT.Service.Packets
{
    public sealed class ArpCache : IDisposable
    {
        public sealed record Entry(
            IPAddress Ip,
            PhysicalAddress Mac,
            string NicId,
            DateTime LearnedAt,
            DateTime ExpiresAt,
            bool IsStatic = false
        );

        private readonly ConcurrentDictionary<IPAddress, Entry> _map = new();
        private TimeSpan _ttl = TimeSpan.FromSeconds(120);            // дефолтный TTL
        private readonly Timer _janitor;                               // фоновая очистка

        public ArpCache()
        {
            // запускаем уборщика каждые 5s
            _janitor = new Timer(_ => PurgeExpired(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        /// установить TTL (применяется ко всем **новым** и обновляемым записям)
        public void SetTtl(TimeSpan ttl)
        {
            if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
            _ttl = ttl;
        }

        public TimeSpan GetTtl() => _ttl;

        /// сохранить/обновить запись (вызывается при получении ARP Reply)
        public void Learn(IPAddress ip, PhysicalAddress mac, string nicId, bool isStatic = false)
        {
            var now = DateTime.UtcNow;
            var exp = isStatic ? DateTime.MaxValue : now.Add(_ttl);

            _map.AddOrUpdate(ip,
                _ => new Entry(ip, mac, nicId, now, exp, isStatic),
                (_, old) => new Entry(ip, mac, nicId, now, exp, old.IsStatic || isStatic));
        }

        /// «снимок» живых записей (без протухших)
        public IReadOnlyCollection<Entry> Snapshot()
        {
            var now = DateTime.UtcNow;
            return _map.Values.Where(e => e.ExpiresAt > now).ToList();
        }

        /// полная очистка (кнопка Flush)
        public void FlushAll() => _map.Clear();

        /// убрать только протухшие (вызывается таймером)
        public int PurgeExpired()
        {
            var now = DateTime.UtcNow;
            var removed = 0;
            foreach (var kv in _map.ToArray())
            {
                var e = kv.Value;
                if (!e.IsStatic && e.ExpiresAt <= now)
                {
                    if (_map.TryRemove(kv.Key, out _)) removed++;
                }
            }
            return removed;
        }

        public void Dispose() => _janitor.Dispose();
    }
}
