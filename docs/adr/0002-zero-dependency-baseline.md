# ADR-0002: Zero-dependency baseline (no NuGet beyond the framework)

- **Status:** Accepted
- **Date:** 2026-06-05

## Context

mcadiff-hub is a self-hostable tool whose adoption depends on being trivial to build and
run. Every third-party NuGet package is a supply-chain surface, a future-CVE vector, and a
thing a self-hoster's `dotnet restore` must fetch and a reviewer must trust. The codebase
already reflects a deliberate hand-rolled stance: the PNG encoder is written directly on
`ZLibStream` + a CRC32 (no image library), the account store is a JSON file (no ORM), and
config is read by a tiny dotenv parser (no config package). The only references are the
ASP.NET Core framework and the mcadiff core (see [ADR-0003](0003-sibling-mcadiff-core-coupling.md)).

## Decision

We will take **no NuGet dependencies beyond the .NET SDK / ASP.NET Core framework**. New
functionality is implemented against the base class library or not at all. Adding a package
requires its own ADR superseding this one, with explicit justification for why the BCL is
insufficient and why the maintenance cost of hand-rolling exceeds the cost of the dependency.

This is a posture, not dogma: meeting an external spec contract (e.g. the WebAuthn or the
Xbox/Minecraft auth chains) by hand is *in* scope precisely because it avoids a dependency.

## Consequences

- **Positive:** minimal direct supply-chain surface; trivial restore; the team understands
  every line that ships; CodeQL/Dependabot have little to chew on directly.
- **Negative:** more code to own (the PNG encoder, any future WebAuthn/CBOR reader); we
  re-implement spec contracts we could otherwise buy.
- **Neutral / follow-ups:** the **transitive** deps we do ship come entirely through the
  mcadiff core's project reference (fNbt, LZ4, cloud SDKs) and are *not* visible to the
  hub's own Dependabot/CodeQL — that gap is real and tracked separately (see the
  supply-chain issue and [ADR-0003](0003-sibling-mcadiff-core-coupling.md)).
