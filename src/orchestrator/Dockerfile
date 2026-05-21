FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
WORKDIR /src/services/TechOrchestrator
RUN dotnet restore "TechOrchestrator.csproj"
RUN dotnet publish "TechOrchestrator.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:4000
EXPOSE 4000

# 2026-05-18: Sentinel auto-heal'in app-level crash'leri yakalayabilmesi icin
# HEALTHCHECK eklendi (eskiden compose'da disable, kendi yorumuna gore "aspnet
# image curl yok"). Debian-based aspnet:10.0 → apt mevcut, curl ~5MB overhead
# kabul edilebilir. Healthcheck URL env-driven: compose/runtime tarafindan
# ASPNETCORE_URLS :5000'e override edildigi icin HEALTHCHECK_URL default :5000.
# Standalone :4000 run icin docker run -e HEALTHCHECK_URL=http://localhost:4000/...
# veya compose'da `healthcheck: { disable: true }` ile override.
RUN apt-get update && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
ENV HEALTHCHECK_URL=http://localhost:5000/orchestrator/health
HEALTHCHECK --interval=30s --timeout=5s --retries=3 --start-period=60s \
  CMD curl -fsS "$HEALTHCHECK_URL" || exit 1

ENTRYPOINT ["dotnet", "TechOrchestrator.dll"]
