# Stage 1: build the React dashboard
FROM node:22-alpine AS web-build
WORKDIR /web
# .npmrc (legacy-peer-deps) must land before `npm ci` for it to take effect.
COPY web/package.json web/package-lock.json web/.npmrc ./
RUN npm ci
COPY web/ ./
RUN npm run build

# Stage 2: build the .NET server
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY LocalGameSync.sln ./
COPY src/Shared/LocalGameSync.Shared.csproj src/Shared/
COPY src/Server/LocalGameSync.Server.csproj src/Server/
RUN dotnet restore src/Server/LocalGameSync.Server.csproj

COPY src/Shared/ src/Shared/
COPY src/Server/ src/Server/

# Bake the React build into wwwroot so the server serves it as static files
COPY --from=web-build /web/dist/ src/Server/wwwroot/

RUN dotnet publish src/Server/LocalGameSync.Server.csproj -c Release -o /app /p:UseAppHost=false

# Stage 3: runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# curl is needed for the HEALTHCHECK below; keep the layer small.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Persisted state (DB + archives) lives on a mounted unRAID share.
VOLUME ["/data"]
ENV ASPNETCORE_URLS=http://+:8080
ENV Storage__DbPath=/data/localgamesync.db
ENV Storage__ArchiveRoot=/data/archives
ENV Storage__ArtRoot=/data/art
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD curl -sf http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "LocalGameSync.Server.dll"]
