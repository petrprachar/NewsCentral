# Entra Group-Membership Team

**Status:** Specified — not yet implemented. Extends the existing Entra device team
resolution (attribute-based) in NewsService with a second, optional dynamic-team source.

## Purpose

A machine receives an additional dynamic team — named after an **inclusion** group — when its
Entra **device object** is a transitive member of that group and is **not** a transitive member of
a separate **exclusion** group. This runs as a sibling to the attribute-based resolver; both feed
the same `resolved-teams.json` and are consumed identically (key-with-content verification,
registry-wins precedence, NewsViewer union — all unchanged).

The groups are **Entra cloud groups**, evaluated via Microsoft Graph. On-premises AD groups are out
of scope (device cloud-group membership is a directory concept reachable only through Graph).

## Activation

Active only when **all** of:

- the Entra feature is enabled (`Entra:Enabled = true`), **and**
- an inclusion group name is configured.

The exclusion group is optional — empty means no exclusion. An exclusion configured **without** an
inclusion is a no-op.

## Configuration

New values under the existing `Entra` section (registry-overridable like the rest). Both are Entra
group **display names**.

**appsettings.json (NewsService):**

```json
"Entra": {
  "Enabled": false,
  "GracePeriodMinutes": 240,
  "Mappings": { },
  "GroupTeam": {
    "InclusionGroup": "",
    "ExclusionGroup": ""
  }
}
```

**Registry:**

```
HKLM\Software\[Company]\NewsCentral\NewsService\Entra\GroupTeam\
    InclusionGroup   REG_SZ   (Entra group display name; activates the feature when non-empty)
    ExclusionGroup   REG_SZ   (optional; membership suppresses the team)
```

`Entra:GroupTeam:InclusionGroup` etc. bind automatically via the recursive registry walk — no
provider change.

## Resolution logic

Let `D` be this machine's Entra device object.

- `include` = `D` is a **transitive** member of `InclusionGroup`
- `exclude` = `ExclusionGroup` is configured **and** `D` is a **transitive** member of it
- **team present ⇔ `include ∧ ¬exclude`**
- team folder name = the canonicalized `InclusionGroup` display name

| In inclusion | In exclusion | Result |
|---|---|---|
| no | (any) | no team |
| yes | no | **team active** |
| yes | yes | no team (excluded) |

Canonicalization is the **same** transform used for attribute-derived names and
`TeamService.GenerateFolderName`: lower-case; space/underscore → `-`; strip anything outside
`[a-z0-9-]`; no prefix. So a group `NewsCentral Prague ITS` → folder `newscentral-prague-its`, and the
author must publish content under that exact folder name.

## Membership mechanics

- Membership is evaluated on the **device object** — the same one already fetched for
  `extensionAttributes`. The existing device fetch is extended to also `$select` the device's
  directory object `id`.
- Group display names are resolved to object IDs
  (`GET /groups?$filter=displayName eq '{name}'`). Resolutions are cached for the process lifetime
  (group IDs are stable).
- Both groups are evaluated transitively in **one** call:
  `POST /devices/{id}/checkMemberGroups` with `[inclusionId, exclusionId]`; the response lists which
  of the two the device transitively belongs to.
- **Ambiguous display name** (the filter returns more than one group) is treated as a
  **misconfiguration**: log a warning and produce no group team for that name. Names are never guessed.

## Graph permission

One application permission is added to the existing agent app registration (**admin consent
required**):

- **`GroupMember.Read.All`** — least privilege; covers both group-name resolution and the device
  `checkMemberGroups` call alongside the existing `Device.Read.All`.
- **`Directory.Read.All`** — broad fallback, only if `GroupMember.Read.All` proves insufficient at
  runtime (which surfaces as a `403` — see the failure model).

Reflected in `docs/azure-setup.md` (agent app registration, §1).

## Failure model — per-source, persistent vs transient

The group resolver produces its **own** tri-state outcome, independent of the attribute resolver: a
failure or misconfiguration in one must not affect the other. The classification of *why* a cycle
produced no team determines whether grace applies.

- **Transient** (answer unknown, a retry will likely succeed) → `Unreachable` → **grace** (retain
  the last-known team within `GracePeriodMinutes`).
- **Persistent / definitive** (the answer is knowable and won't change on retry) → `NoTeam` →
  **remove promptly** (no grace), logged so the cause is visible.

| Condition | Outcome | Grace | Log level |
|---|---|---|---|
| Network / DNS / timeout / connection reset | `Unreachable` | yes | Warning |
| HTTP 503 / 429 / other 5xx | `Unreachable` | yes | Warning |
| HTTP 403 (missing permission / consent) | `NoTeam` | no | **Error** |
| Group not found / name unresolved | `NoTeam` | no | Warning |
| Ambiguous display name | `NoTeam` | no | Warning |
| Device 404 | `NoTeam` | no | Warning |
| Device not a member (clean answer) | `NoTeam` | no | Info |

A `403` is **persistent** — retrying every cycle will never fix a missing consent — so it removes
the team and logs at **Error**, surfacing the configuration gap immediately rather than silently
gracing a stale team for the whole window. The same persistent-vs-transient classification should be
applied to the **existing attribute device-read** for consistency (it currently maps auth errors into
the catch-all `Unreachable`).

## Integration with the existing pipeline

- The "**one dynamic team per device**" invariant becomes "**up to two**": the attribute team and the
  group team. `resolved-teams.json` already holds a list, and each entry now carries a per-entry
  **`Source` discriminator** (`Attribute` | `Group`, default `Attribute`) so each source has an
  independent grace window. This is the only schema addition; it is back-compatible (a missing
  `Source` deserializes to `Attribute`) and **NewsViewer ignores it** (it reads only `TeamFolderName`).
- The orchestrator runs **both** resolvers each cycle and collects per-source outcomes.
- The grace **merger generalizes** from a single team to a team **set**: when reachable, the
  authoritative set = `{attribute team if any} ∪ {group team if any}`, written `Active`; a per-source
  `Unreachable` applies grace per team. The current single-team merger is the degenerate case (set
  size 0 or 1), so its tests **generalize** rather than break.
- **Consumption is entirely unchanged.** A group-derived team is just another dynamic team — the
  key-with-content / registry-wins precedence and the NewsViewer union apply as-is.

## Edge cases

- Exclusion configured, inclusion empty → feature inactive (no-op).
- Member of the exclusion group only (not inclusion) → no team (inclusion gates first).
- Inclusion name resolves to multiple groups → no team + warning (ambiguity policy).
- Group team name coincides with an attribute team or a static team → the union de-dupes; registry-wins
  verification handles any overlap.
- Group has zero published content → synced as not-found and skipped (benign), same as attribute teams.

## Implementation impact (summary)

- **Config:** `EntraOptions` gains a `GroupTeam` sub-object (`InclusionGroup`, `ExclusionGroup`);
  appsettings + registry layout extended.
- **Graph client:** the device fetch additionally returns the device object `id`; a new
  group-name→id resolution (cached) and a `checkMemberGroups` call are added, with the
  persistent/transient exception mapping.
- **Orchestrator:** runs the attribute resolver and the group resolver per cycle, producing a set of
  per-source outcomes.
- **Merger:** generalized from a single resolved team to a per-source resolved **set**; existing tests generalize.
- **Schema:** `resolved-teams.json` entries gain a `Source` discriminator (`Attribute` | `Group`,
  default `Attribute`) for per-source grace — back-compatible (missing `Source` ⇒ `Attribute`).
- **Permission / docs:** `GroupMember.Read.All` added to the agent app registration; `azure-setup.md`
  and `configuration.md` updated.
- **No change** to NewsViewer (it ignores `Source`), to verification (`VerifyWithPrecedence`), or to
  the authoring side.

## Out of scope

- On-premises AD groups (Entra cloud groups only).
- Multiple inclusion/exclusion pairs — exactly one pair (one group team) per machine. Multiple pairs
  are a possible future extension.
