#nullable enable

namespace WANT.Service.Interface
{
    public static class InterfaceManager
    {
        // Preserve model configuration while refreshing OS metadata.
        public static void Sync(IEnumerable<NetInterface> latest, WANT.Service.InterfaceScanner store, bool hardDeleteMissing = true)
        {
            var current = store.GetAll().ToDictionary(x => x.Guid, StringComparer.OrdinalIgnoreCase);
            var latestByGuid = latest.ToDictionary(x => x.Guid, StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, NetInterface>(StringComparer.OrdinalIgnoreCase);

            // Preserve user names and configured addresses on existing interfaces.
            foreach (var (guid, cur) in current)
            {
                if (latestByGuid.TryGetValue(guid, out var l))
                {
                    var updated = cur with
                    {
                        Description = l.Description,
                        OSName = l.OSName,
                        FriendlyName = l.FriendlyName,
                        Type = l.Type,
                        Mac = l.Mac,
                        Status = l.Status,
                        // Router-model addressing is independent of OS discovery.
                        TrackState = cur.Ip is null ? TrackState.Unconfigured : TrackState.Active
                    };
                    result[guid] = updated;
                }
            }

            // New interfaces start without router-model addresses.
            foreach (var (guid, l) in latestByGuid)
            {
                if (!current.ContainsKey(guid))
                {
                    var added = new NetInterface(
                        Guid: l.Guid,
                        Description: l.Description,
                        OSName: l.OSName,
                        FriendlyName: l.FriendlyName,
                        Type: l.Type,
                        Mac: l.Mac,
                        Status: l.Status,
                        UserName: l.FriendlyName,
                        Ip: null,
                        Mask: null,
                        PrefixLen: null,
                        TrackState: TrackState.Unconfigured
                    );
                    result[guid] = added;
                }
            }

            // Optionally retain missing interfaces as removed snapshots.
            foreach (var (guid, cur) in current)
            {
                if (!hardDeleteMissing && !latestByGuid.ContainsKey(guid))
                {
                    result[guid] = cur with
                    {
                        Ip = null,
                        Mask = null,
                        PrefixLen = null,
                        TrackState = TrackState.Removed,
                        Status = NicStatus.Unknown
                    };
                }
            }

            store.ReplaceAll(result.Values);
        }

        public static void SetDisplayName(string guid, string name, WANT.Service.InterfaceScanner store)
        {
            var all = store.GetAll().ToDictionary(x => x.Guid, StringComparer.OrdinalIgnoreCase);
            if (!all.TryGetValue(guid, out var ni)) return;
            all[guid] = ni with { UserName = name };
            store.ReplaceAll(all.Values);
        }

        public static void SetAddress(string guid, string ip, string mask, WANT.Service.InterfaceScanner store)
        {
            var all = store.GetAll().ToDictionary(x => x.Guid, StringComparer.OrdinalIgnoreCase);
            if (!all.TryGetValue(guid, out var ni)) return;

            if (!System.Net.IPAddress.TryParse(ip, out var ipAddr)) return;
            if (!System.Net.IPAddress.TryParse(mask, out var maskAddr)) return;

            var prefix = TryMaskToPrefix(maskAddr);

            // Routes and ARP state are managed separately; this updates only the model.
            all[guid] = ni with { Ip = ipAddr, Mask = maskAddr, PrefixLen = prefix, TrackState = TrackState.Active };
            store.ReplaceAll(all.Values);
        }

        private static int? TryMaskToPrefix(System.Net.IPAddress mask)
        {
            try
            {
                var b = mask.GetAddressBytes();
                var bits = 0;
                foreach (var x in b)
                {
                    bits += x switch
                    {
                        255 => 8,
                        254 => 7,
                        252 => 6,
                        248 => 5,
                        240 => 4,
                        224 => 3,
                        192 => 2,
                        128 => 1,
                        0 => 0,
                        _ => throw new InvalidOperationException("Bad mask")
                    };
                    if (x != 255) break;
                }
                return bits;
            }
            catch { return null; }
        }
    }
}
