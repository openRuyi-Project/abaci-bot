# Build stage
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG GIT_COMMIT=unknown
ARG GIT_DIRTY=false
WORKDIR /src

# Copy csproj and restore dependencies
COPY *.csproj ./
RUN dotnet restore

# Copy everything else and publish self-contained for target architecture
COPY . ./
RUN case "${TARGETARCH}" in \
        "arm64")   RID="linux-arm64" ;; \
        *)         RID="linux-x64" ;; \
    esac \
    && dotnet publish -c Release -r ${RID} --self-contained -p:GitCommit=${GIT_COMMIT} -p:GitDirty=${GIT_DIRTY} -o /app/publish

# Runtime stage: Fedora 45 provides native rpm 6.x and libabigail 2.x with full v4 and v6 support
FROM registry.fedoraproject.org/fedora:45 AS runtime
ARG GIT_COMMIT=unknown
ARG GIT_DIRTY=false
WORKDIR /app

# Install native tools: abipkgdiff with RPM v4/v6 support, curl for health checks, libicu for .NET globalization, tzdata for IANA timezones
RUN dnf install -y --setopt=install_weak_deps=False \
        curl \
        libabigail \
        rpm \
        cpio \
        libicu \
        tzdata \
    && dnf clean all

# Create dedicated non-root user with home directory and pre-create libabigail cache and default state directory
RUN useradd -u 1654 -m -s /bin/bash appuser \
    && mkdir -p /home/appuser/.cache/libabigail /home/appuser/.local/share/abaci-bot \
    && chown -R 1654:1654 /home/appuser

# Default container listening port and user environment
ENV ASPNETCORE_HTTP_PORTS=3456
ENV HOME=/home/appuser
ENV ABACI_BUILD_COMMIT=${GIT_COMMIT}
ENV ABACI_BUILD_DIRTY=${GIT_DIRTY}

# Copy self-contained published application files
COPY --from=build /app/publish .

# Run as non-root user
USER 1654

# Expose default port
EXPOSE 3456

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:3456/health || exit 1

ENTRYPOINT ["./abaci-bot"]
