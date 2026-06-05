---
name: project-test-infra
description: No test project exists yet; planned infrastructure is xUnit + WebApplicationFactory; core uses TestAnvil pattern
metadata:
  type: project
---

## Current state
- Zero test project in mcadiff-hub (CLAUDE.md confirms: "There is no test project in this repo").
- Single CI check: render smoke in .github/workflows/ci.yml — runs the render CLI against core's real 1.21 sample world, checks PNG is non-empty. Exercises MapRenderer + core RegionFile/ChunkCodec but nothing else.

## Core's test conventions (mca-git)
- Framework: xUnit (xunit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1, coverlet.collector 6.0.4)
- Net target: net10.0 (core currently at net9.0 but hub is net10.0)
- Project at: mca-git/tests/McaDiff.Tests/McaDiff.Tests.csproj
- Synthetic world builder: TestAnvil (TempDir, WriteRegion, WriteLoose, Root helpers)
- No binary fixtures — all test inputs built in code
- No sleeps — deterministic sync via in-memory fakes (InMemoryBucket) or direct calls

## Planned hub test project
- Location: tests/McadiffHub.Tests/McadiffHub.Tests.csproj
- Framework: xUnit + Microsoft.AspNetCore.Mvc.Testing (WebApplicationFactory<Program>)
- ProjectReference: src/McadiffHub/McadiffHub.csproj (which transitively pulls the core)
- Integration approach: inject temp data dir, temp cache dir, temp hub.json via env vars or IConfiguration override in WebApplicationFactory
- Auth seeding: use MCAHUB_DEV_LOGIN=1 + POST /auth/dev to get a real session cookie, then use that cookie in subsequent requests
- CSRF tokens: read form field from a GET response before POSTing (real token flow, not mocked)

## CI addition needed
- New job "test" in ci.yml: dotnet test tests/McadiffHub.Tests -c Release
- Must check out both repos (same side-by-side pattern as existing build job)
- Run on both ubuntu-latest and windows-latest (path sensitivity matters)

**Why:** Hub has zero tests; standing up the project is the unblocking step for all other test work.
**How to apply:** When writing test code, follow the xUnit + TestAnvil conventions above. Use WebApplicationFactory for HTTP-layer tests; use direct class instantiation for pure unit tests (HubDb, RepoStore.IsValidName).
