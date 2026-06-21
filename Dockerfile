# syntax=docker/dockerfile:1
#
# Multi-stage build for mcahub. The hub drives the Rust `mcagit` engine out-of-process, so the
# image bundles two artifacts: the published .NET web layer and the `mcagit` binary (built here
# from source — no submodule or sibling checkout required in the build context). Pin the engine
# with `--build-arg MCAGIT_REF=<tag-or-sha>`; it defaults to mcagit main.

ARG MCAGIT_REPO=https://github.com/BangRocket/mcagit.git
ARG MCAGIT_REF=main

FROM rust:1-bookworm AS engine
ARG MCAGIT_REPO
ARG MCAGIT_REF
RUN git clone "$MCAGIT_REPO" /mcagit && git -C /mcagit checkout "$MCAGIT_REF"
WORKDIR /mcagit
RUN cargo build --release --locked && /mcagit/target/release/mcagit --version

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ src/
RUN dotnet publish src/McaHub/McaHub.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
COPY --from=engine /mcagit/target/release/mcagit /usr/local/bin/mcagit
# Data lives on a mounted volume; the rest of the FS can be read-only. The aspnet image ships a non-root
# `app` user (UID 1654) — own the data dir to it and drop privileges.
RUN mkdir -p /data && chown app:app /data
ENV ASPNETCORE_URLS=http://0.0.0.0:5080 \
    MCAGIT_BIN=/usr/local/bin/mcagit \
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
ENTRYPOINT ["dotnet", "McaHub.dll"]
