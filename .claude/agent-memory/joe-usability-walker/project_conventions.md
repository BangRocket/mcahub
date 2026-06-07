---
name: product-conventions
description: Recurring UI patterns in mcahub — good, bad, and jargon-heavy — discovered during walkthroughs
metadata:
  type: project
---

## Screens verified as genuinely clear
- CSRF error copy (Pages.cs:127): plain English "Invalid or expired form token — go back, reload the page, and retry." — good.
- "nouser" error on collaborator add: plain English "That user hasn't signed in to the hub yet." — good.
- Map spinner "Generating map…" with loading state — good UX pattern.
- Token flash banner on account page: clearly says "copy it now, it won't be shown again." — good.

## Recurring jargon that confuses Joe
- Role ladder: owner / admin / maintain / write / read — "maintain" means nothing to a non-dev. "write" sounds like text editing, not push access. Covered broadly by #28 but specific capabilities never explained inline.
- "commit" used as noun for a backup snapshot — a git term Joe doesn't know.
- "branch" used throughout — Joe has no mental model for branches on a Minecraft world backup.
- "hash" (short commit SHA) shown in the timeline as the primary identifier for a backup.
- "clone" instruction shown on every repo page — irrelevant to Joe's web-only usage.

## Layout/color notes
- Fixed max-width 960px (style.css:24) — no responsive breakpoints, clips on mobile.
- Red (#f85149) and green (#3fb950) used for: destroyed/placed grief counts, diff lines (ch-added/ch-removed), visibility badges (public/private), token flash border, file status badges. No shape/pattern/label backup beyond the text label already present in some places.
- Role badges use purple (admin), teal (maintain), green (write), muted gray (read), blue (owner) — these are distinguishable without red/green so role ladder is colorblind-safe.
- Grief box: g-d = red, g-b = green, g-r = amber — text labels present ("destroyed", "placed", "replaced") so colorblind users can still read it. But the colors still reinforce the wrong mental model (green = "placed" feels neutral/good, red = "destroyed" feels bad — fine for sighted users).

## Navigation gaps
- No nav link to /teams from anywhere except the header (only shown when logged in).
- No breadcrumb on the compare page beyond "← worldname" — no way to know which two backups are being compared without reading the hash in the h1.
- Time machine (/timeline) is linked from the repo page with emoji link "🕑 time machine — scrub the map across backups" but there is no back link from time machine to the specific backup being viewed.
- The world explorer (/world/{ref}) back-link goes to "← backup {hash}" not "← worldname" — two hops to get back to the world list.

## When timestamp format matters
- When() helper (Pages.cs:676) formats as "yyyy-MM-dd HH:mm" — this is a programmer-friendly ISO-ish format. Joe reads "2026-06-04 21:32" and has to mentally subtract to figure out "that was last night." No relative-time anchors ("yesterday", "3 days ago").
