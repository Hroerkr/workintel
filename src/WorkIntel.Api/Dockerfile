# syntax=docker/dockerfile:1.7

# ── Build stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csprojs first for layer-cache friendly restore.
COPY src/WorkIntel.Contracts/WorkIntel.Contracts.csproj src/WorkIntel.Contracts/
COPY src/WorkIntel.Data/WorkIntel.Data.csproj           src/WorkIntel.Data/
COPY src/WorkIntel.Api/WorkIntel.Api.csproj             src/WorkIntel.Api/
RUN dotnet restore src/WorkIntel.Api/WorkIntel.Api.csproj

# Now copy sources and publish.
COPY src/WorkIntel.Contracts/ src/WorkIntel.Contracts/
COPY src/WorkIntel.Data/      src/WorkIntel.Data/
COPY src/WorkIntel.Api/       src/WorkIntel.Api/
RUN dotnet publish src/WorkIntel.Api/WorkIntel.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ── Runtime stage ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 5000
ENV ASPNETCORE_ENVIRONMENT=Production
# Kestrel binding is set in Program.cs via ConfigureKestrel.

USER app

ENTRYPOINT ["dotnet", "WorkIntel.Api.dll"]
