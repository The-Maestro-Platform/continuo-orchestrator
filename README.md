# continuo-orchestrator

> Gateway / dynamic routing / endpoint manifest service. Front door that BFF and external callers hit before being routed to the right backend service.

Service namespace: `Orchestrator` (renamed from upstream `Orchestrator`).
Assembly: `orchestrator`.

Reads/writes the endpoint proxy manifest (`endpoint_proxy.json`), terminates JWT bearer tokens, distributes data-protection keys via Redis, and handles cross-service routing.

## Dependencies (4 submodules)

- `deps/continuo-shared`
- `deps/continuo-messaging`
- `deps/continuo-configuration`
- `deps/continuo-observability`

```bash
git clone --recurse-submodules https://github.com/WhiteToblack/continuo-orchestrator.git
cd continuo-orchestrator
dotnet build orchestrator.sln
```

## Endpoint manifest

`endpoint_proxy.json` at the repo root is a placeholder (revision: "placeholder", empty services). At runtime the orchestrator populates this from its catalog DB. Replace the placeholder with a real manifest once the platform's services register.

## Layout

```
src/orchestrator/
  Program.cs
  Application/      # Use-cases
  Controllers/      # MVC controllers (legacy; minimal API where new)
  Data/             # EF Core DbContext + entities
  Hosting/          # Bootstrap, endpoint registration, hosted services
  Migrations/       # EF Core migrations
  Models/           # DTOs
  Services/         # Business services
  appsettings.json
```

## NuGet

- EF Core 10 (+ SqlServer + Design + Tools)
- `Microsoft.AspNetCore.DataProtection.StackExchangeRedis` 8.0
- `StackExchange.Redis` 2.7
- `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0

## License

Proprietary — all rights reserved.
