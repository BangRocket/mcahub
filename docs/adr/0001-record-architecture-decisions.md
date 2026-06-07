# ADR-0001: Record architecture decisions

- **Status:** Accepted
- **Date:** 2026-06-05

## Context

mcahub has accumulated several load-bearing decisions that aren't obvious from the
code: the zero-dependency stance, the sibling coupling to the mcadiff core, the JSON-file
account store, the single-role-source authorization model, the deliberate choice to stay a
single process. Today these live as prose in `CLAUDE.md` and `SECURITY.md`, or only in
commit history. New contributors (human or agent) re-question or accidentally undo them
because the *why* isn't written down next to the *what*.

## Decision

We will record significant architecture decisions as numbered ADRs in `docs/adr/`, using
the Nygard template (Context / Decision / Consequences). An ADR is immutable once Accepted;
a decision is changed by writing a new ADR that supersedes the old one. The index lives in
`docs/adr/README.md`.

## Consequences

- **Positive:** the rationale survives the decision; onboarding has a single place to read
  "why is it like this"; re-litigation has a document to point at.
- **Negative:** a small per-decision writing cost, and the discipline to actually do it.
- **Neutral / follow-ups:** ADRs complement, not replace, `CLAUDE.md` (how to work here) and
  `SECURITY.md` (the trust map). Reserve ADRs for the load-bearing few.
