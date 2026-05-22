# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Copy the full repo context (includes src/ + deps/continuo-* submodules)
COPY . ./
RUN dotnet publish src/orchestrator/orchestrator.csproj -c Release -o /app/publish --nologo

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:80
ENTRYPOINT ["dotnet", "orchestrator.dll"]