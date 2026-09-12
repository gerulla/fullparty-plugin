# Regression Checks

These console checks link production source files and run without Dalamud, game access, credentials, or network requests.

```powershell
dotnet run --project tests/TerritoryDetection
dotnet run --project tests/RunPayloads
```

- `TerritoryDetection`: numeric support gates for Occult Crescent, Hydatos/BA, Delubrum Normal/Savage, unsupported territories, and BA interior maps.
- `RunPayloads`: supplied BA/DRS response fixtures through the real API mapper, configured field keys, legacy responses, role flags, holsters, empty slots, non-moderators, and full-size rosters.

These do not replace in-game verification of native party detection, UI rendering, or websocket delivery.
