# syntax=docker/dockerfile:1
#
# Multi-stage build for mcadiff-hub. The mcadiff core is vendored via the git submodule at ./mca-git,
# which must be present in the build context — clone with `--recurse-submodules` (or run
# `git submodule update --init --recursive`) before `docker build`. The runtime image has no SDK and no
# sibling-checkout requirement.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first (cached unless a project file changes), then build.
COPY mca-git/ mca-git/
COPY src/ src/
RUN dotnet publish src/McadiffHub/McadiffHub.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# Data lives on a mounted volume; the rest of the FS can be read-only. The aspnet image ships a non-root
# `app` user (UID 1654) — own the data dir to it and drop privileges.
RUN mkdir -p /data && chown app:app /data
ENV ASPNETCORE_URLS=http://0.0.0.0:5080 \
    MCAHUB_DATA=/data/repos \
    MCAHUB_CACHE=/data/cache \
    MCAHUB_MAPS=/data/maps \
    MCAHUB_DB=/data/hub.json \
    MCAHUB_AUDIT=/data/audit.jsonl
USER app
EXPOSE 5080
VOLUME /data
# Liveness: probe GET /health (200, unauthenticated, rate-limit-exempt — #32) from your orchestrator.
# (The aspnet image has no curl/wget, so the probe lives in the orchestrator, not a Docker HEALTHCHECK.)
ENTRYPOINT ["dotnet", "McadiffHub.dll"]
