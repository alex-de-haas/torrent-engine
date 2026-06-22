# syntax=docker/dockerfile:1
# Build context: repo root.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/TorrentEngine.Api/TorrentEngine.Api.csproj src/TorrentEngine.Api/
RUN dotnet restore src/TorrentEngine.Api/TorrentEngine.Api.csproj

COPY src/ src/
RUN dotnet publish src/TorrentEngine.Api/TorrentEngine.Api.csproj -c Release -o /app/publish --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# OpenVPN for the tunnel, iptables for the killswitch. The container needs NET_ADMIN and
# /dev/net/tun at runtime (granted via the Hosty manifest's capabilities/devices).
RUN apt-get update \
    && apt-get install -y --no-install-recommends openvpn iptables iproute2 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

# The .NET control API binds all interfaces inside the container; the killswitch confines
# what may actually leave (control API on the bridge, everything else over the tunnel).
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
