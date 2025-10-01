#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace WANT.Service
{
    /// <summary>
    /// Потокобезопасное хранилище текущего состояния сетевых интерфейсов.
    /// Здесь лежит «переменная», где держим список интерфейсов.
    /// </summary>
    public sealed class InterfaceScanner
    {
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
        private Dictionary<string, Interface.NetInterface> _byGuid = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Событие для GUI/логики: интерфейсы обновились (после Sync).</summary>
        public event Action<IReadOnlyCollection<Interface.NetInterface>>? InterfacesUpdated;

        /// <summary>Получить snapshot (глубокая копия объектов не делается — предполагается иммутабельность моделей).</summary>
        public IReadOnlyCollection<Interface.NetInterface> GetAll()
        {
            _lock.EnterReadLock();
            try { return _byGuid.Values.ToList(); }
            finally { _lock.ExitReadLock(); }
        }

        /// <summary>Получить по GUID (или null, если нет).</summary>
        public Interface.NetInterface? GetByGuid(string guid)
        {
            _lock.EnterReadLock();
            try { return _byGuid.TryGetValue(guid, out var ni) ? ni : null; }
            finally { _lock.ExitReadLock(); }
        }

        /// <summary>Полная замена состояния (используется синхронизатором).</summary>
        internal void ReplaceAll(IEnumerable<Interface.NetInterface> items)
        {
            _lock.EnterWriteLock();
            try
            {
                _byGuid = items.ToDictionary(x => x.Guid, x => x, StringComparer.OrdinalIgnoreCase);
            }
            finally { _lock.ExitWriteLock(); }
            InterfacesUpdated?.Invoke(GetAll());
        }
    }
}
