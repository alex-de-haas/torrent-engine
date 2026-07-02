# syntax=docker/dockerfile:1
# Build context: repo root.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Native AOT compiles to a platform-native binary, so the SDK image also needs a C toolchain
# (clang) and the zlib headers at build time.
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

# AOT publish is RID-specific: map the Docker build arch (TARGETARCH, set by buildx; falls back to the
# SDK image's own arch for a plain `docker build`) to the matching .NET runtime identifier.
RUN arch="${TARGETARCH:-$(dpkg --print-architecture)}"; \
    case "$arch" in \
      amd64) echo linux-x64 > /tmp/rid ;; \
      arm64) echo linux-arm64 > /tmp/rid ;; \
      *) echo "unsupported architecture: $arch" >&2; exit 1 ;; \
    esac

COPY src/TorrentEngine.Api/TorrentEngine.Api.csproj src/TorrentEngine.Api/
RUN dotnet restore src/TorrentEngine.Api/TorrentEngine.Api.csproj -r "$(cat /tmp/rid)"

COPY src/ src/
RUN dotnet publish src/TorrentEngine.Api/TorrentEngine.Api.csproj \
    -c Release -r "$(cat /tmp/rid)" --no-restore -o /app/publish

# ---- runtime ----
# runtime-deps carries only the native libraries a self-contained/AOT binary links against (no managed
# runtime), so the image is much smaller than the aspnet base. OpenVPN provides the tunnel and iptables
# the killswitch; the container needs NET_ADMIN and /dev/net/tun at runtime (granted via the Hosty
# manifest's capabilities/devices).
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
WORKDIR /app

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
