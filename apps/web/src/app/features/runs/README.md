# Runs feature — evidence anchors

The run detail page is the truth surface for evidence, attempts and the live decision. Other
surfaces (failure inbox, healing review) send people back into it, so the fragment contract below
is shared, not local to this folder.

## URL shape

```
/projects/:projectId/runs/:runId?evidence_revision=<revision_no>#<anchor>
```

`evidence_revision` pins the viewed revision (positive integer server revision number). The
fragment names one row inside the page and never changes the pin.

## Anchor shapes

| Target            | DOM id                        | Example             |
| ----------------- | ----------------------------- | ------------------- |
| One attempt row   | `attempt-<execution_key>-<n>` | `attempt-exec_42-2` |
| One producer slot | `producer-<shard>`            | `producer-shard-3`  |

`<n>` is the server attempt number (`attempt_no`), never an array index, a retry counter or a
screen page. `<execution_key>` and `<shard>` are the server identifiers.

Identity is case-sensitive and locale-independent. Never slugify, lowercase, truncate, translate or
derive it from rendered text: the same value must reach the DOM id and the link. For identifiers
containing URI-reserved characters each component is percent-encoded inside the DOM id
(`encodeURIComponent`), and the complete id is URI-encoded again when the fragment is built, so a
space or `#` cannot change navigation. Ordinary identifiers therefore produce exactly the
fragments in the table.

## Building and resolving

`detail/evidence/run-evidence-anchors.ts` is the only implementation; import it instead of
repeating the string patterns:

- `attemptAnchorId(executionKey, attemptNo)` / `producerAnchorId(shard)` — DOM id for a target.
- `evidenceHref(target)` — `#…` fragment for a link, safe to use with `[href]` (same route, so
  unrelated query parameters and the evidence pin survive).
- `resolveEvidenceAnchor(fragment, document)` — the target element, or `null`. It decodes the
  fragment once, checks the documented prefix/suffix and then looks the id up exactly with
  `getElementById`. A fragment is data: never pass it to `querySelector`, a URL, a filesystem path
  or an authorization decision.

## How reason links resolve

A `missing[]` entry is a literal string, not a per-reason identity, so a reason code never decides
its own target. `detail/evidence/missing-reasons.ts` renders an explanation for each literal and a
link only for a relation the caller confirmed from run data (a manifest-backed producer slot, an
attempt row). Several confirmed targets are all listed; none means the explanation plus
"Target details unavailable" and no link.

## Who provides each target

| Anchor              | Host                                                                 | State                                                                                                                                  |
| ------------------- | -------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------- |
| `#attempt-…`        | `detail/attempts/attempts-table.component.html`, row header (`<th>`) | Present: `id` + `tabindex="-1"`; the destination scrolls clear of the sticky header.                                                   |
| `#producer-<shard>` | Producer table (09.3.1-T6)                                           | Not built yet: no producer row exists, so a producer link is only emitted with a confirmed identity and resolves once that host lands. |

The manifest panel's "Declared producers" list is not a target host: it names declared slots, not
the producer evidence of the viewed revision.

## Consumers

Failure inbox (`09.4.1-T4`) and healing review (`09.5.1-T3`) link into the run detail with these
fragments. Keep the locale prefix and the project/run identity; no `#row-N`, `#test-…` or
translated anchor aliases.

## Landing behaviour

A destination row carries `tabindex="-1"` (focusable, out of the sequential tab order) and the
sticky-header scroll margin from the existing spacing tokens. A target that is not rendered yet
(attempt on an unfetched cursor page) keeps the fragment and offers the existing "Load more"
action; resolution is retried after each accepted page and never scans unbounded pages.
