# Management API

The default base URL is `http://localhost:5104`. Request and response bodies use JSON. ASP.NET Core emits camelCase property names; IP addresses are strings, enum values are numeric unless a DTO exposes a textual route type, and durations on DHCP model objects use .NET `TimeSpan` strings. Settings endpoints instead use integer seconds.

The live reference is `/swagger` and the OpenAPI document is `/swagger/v1/swagger.json`. It is generated from the actual controllers. There is no authentication, authorization, persistence, or API version prefix.

## Interfaces

| Method | Path | Body / result |
| --- | --- | --- |
| GET | `/api/interface` | Array of router interface snapshots. |
| GET | `/api/interface/{guid}` | One snapshot; 404 if absent. |
| PUT | `/api/interface/{guid}/address` | `{ "ip": "192.0.2.1", "mask": "255.255.255.0" }`; returns updated snapshot. |
| PUT | `/api/interface/{guid}/display-name` | `{ "name": "LAN" }`; returns updated snapshot. |

Snapshots include `guid`, `description`, `osName`, `friendlyName`, `type`, `mac`, `status`, `userName`, `ip`, `mask`, `prefixLen`, `trackState`, and `isUp`. A new discovered adapter starts unconfigured. Use the GUID returned by discovery, normally uppercase without braces. Address changes affect the router model only; they do not modify OS addresses or automatically rebuild routes. The address controller accepts contiguous IPv4 masks, including `/0` and `/32`.

## Routes

| Method | Path | Body / result |
| --- | --- | --- |
| GET | `/api/routes` | Array of `{ destination, nextHop, outInterfaceName, administrativeDistance, type }`. |
| POST | `/api/routes/add-direct` | `{ "network": "192.0.2.0", "prefixLength": 24, "outInterfaceName": "GUID", "administrativeDistance": 0 }`. |
| POST | `/api/routes/add-via` | `{ "network": "198.51.100.0", "prefixLength": 24, "nextHop": "192.0.2.2", "outInterfaceName": "", "administrativeDistance": 1 }`. |
| POST | `/api/routes/add-connected-from-interfaces` | No body; adds networks from configured, up Ethernet interfaces. |
| DELETE | `/api/routes` | `{ "network": "198.51.100.0", "prefixLength": 24, "outInterfaceName": "" }`. |

`administrativeDistance` is optional on additions. Direct routes default to 0 and next-hop routes to 1. `outInterfaceName` is required for direct routes and optional for next-hop routes. The recursive resolver follows the next hop to a directly reachable route; the final resolved route supplies the egress interface even if the original next-hop route has an explicit interface.

`destination` is returned as a CIDR string. `type` is `C` for a direct route with distance 0, `R` for distance 120, and `S` otherwise. Successful route mutations return an empty 200 response. Network addresses should be supplied canonically, e.g. `192.0.2.0/24`, because route identity compares the stored network and prefix exactly.

## ARP and Proxy ARP

| Method | Path | Body / result |
| --- | --- | --- |
| GET | `/api/arp/cache` | Live entries: `ip`, `mac`, `nic`, `ageSeconds`, `expiresInSeconds`; null expiration denotes static entries. |
| GET | `/api/arp/cache/ttl` | `{ "ttlSeconds": 120 }` by default. |
| PUT | `/api/arp/cache/ttl` | `{ "seconds": 180 }`; accepted range 1–86400. Applies to new/refreshed entries. |
| POST | `/api/arp/cache/flush` | No body; returns `{ "flushed": true }`. |
| POST | `/api/arp/request` | `{ "nicId": "GUID", "targetIp": "192.0.2.2", "srcIp": null }`; uses configured source IP unless explicitly supplied. |
| GET | `/api/proxy-arp` | Array of explicit `{ "interfaceId": "GUID", "enabled": true }` states. |
| POST | `/api/proxy-arp/{interfaceId}` | `{ "enabled": false }`; returns 204. |

Explicit ARP requests require an opened injectable capture device, a configured source address, and a valid source MAC. Proxy ARP defaults to enabled when no explicit state exists; an empty state list does not mean that it is disabled globally.

## RIPv2

| Method | Path | Body / result |
| --- | --- | --- |
| GET | `/api/rip/interfaces` | Per-interface enable state. |
| POST | `/api/rip/enable` | `{ "interfaceId": "GUID" }`. |
| POST | `/api/rip/disable` | `{ "interfaceId": "GUID" }`. |
| GET | `/api/rip/timers` | Timer values in seconds. |
| PUT | `/api/rip/timers` | Full timer object shown below. |

```json
{
  "updateSeconds": 30,
  "invalidSeconds": 180,
  "holddownSeconds": 180,
  "flushSeconds": 240
}
```

RIP is opt-in per interface. Use positive, sensible timer intervals; the current controller does not fully validate timer relationships.

## DHCP

| Method | Path | Body / result |
| --- | --- | --- |
| GET | `/api/dhcp/settings` | `{ "dynamicLeaseSeconds": 300, "cleanupIntervalSeconds": 5 }`. |
| PUT | `/api/dhcp/settings` | Same shape; both values must be positive. Returns 204. |
| GET | `/api/dhcp/pools` | Array of pools. |
| GET | `/api/dhcp/pools/{id}` | Pool or 404. |
| POST | `/api/dhcp/pools` | Pool object; returns 201 with the created pool. |
| PUT | `/api/dhcp/pools/{id}` | Pool object; replaces configuration, preserves the path ID. |
| DELETE | `/api/dhcp/pools/{id}` | Removes the pool and its leases; 204 or 404. |
| POST | `/api/dhcp/pools/{poolId}/reservations` | `{ "mac": "020000000001", "ip": "192.0.2.20" }`; upserts a reservation, returns 204. |
| DELETE | `/api/dhcp/pools/{poolId}/reservations/{mac}` | Removes reservation; 204 or 404. |
| GET | `/api/dhcp/leases` | All current leases. |
| GET | `/api/dhcp/pools/{poolId}/leases` | Leases belonging to one pool. |

Example pool:

```json
{
  "name": "LAN",
  "interfaceId": "GUID",
  "network": "192.0.2.0",
  "mask": "255.255.255.0",
  "rangeStart": "192.0.2.50",
  "rangeEnd": "192.0.2.100",
  "gateway": "192.0.2.1",
  "enabled": true,
  "manualEnabled": true,
  "automaticEnabled": false,
  "dynamicEnabled": true,
  "dynamicLeaseTimeOverride": "00:05:00",
  "manualReservations": {}
}
```

Omit `id` on creation to use the generated ID. If `dynamicLeaseTimeOverride` is null, the global dynamic duration applies. All three allocation flags default to true, and automatic allocation takes precedence over dynamic allocation; disable automatic allocation when testing expiring dynamic leases. Reservations map normalized MAC strings to IP strings.

Lease objects include `id`, `poolId`, `address`, `macAddress`, `macString`, `allocationType`, `interfaceId`, `createdAt`, `expiresAt`, and `remaining`. `allocationType` is 0 (manual), 1 (automatic), or 2 (dynamic); `expiresAt` and `remaining` are null for permanent allocations. Pool validation is limited: use a valid contiguous IPv4 mask, a range within the pool network, and an appropriate gateway/interface combination.

## Error handling

Validation usually returns 400; missing resources return 404. Error bodies vary between plain strings, `{ "error": "..." }`, and ASP.NET Core problem details. Successful mutations may return JSON, an empty 200, or 204. The Electron HTTP adapter deliberately supports empty successful mutation responses while requiring JSON for reads.
