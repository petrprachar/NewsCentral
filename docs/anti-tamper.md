# Anti-Tamper — ECDSA Index Signing

This is the single-page reference for the content-integrity system: the integrity values, the
configuration/registry that governs enforcement, the per-module behavior modes, and the
static-vs-dynamic (Entra) key-trust model. For the phased implementation history see
`docs/security.md`; for the full registry layout see `docs/configuration.md`; for dynamic-team
resolution see `docs/entra-dynamic-teams.md`.

## 1. Overview

Each team's `index.json` is signed with a **per-team ECDSA P-256** private key by NewsCentral at
publish time. NewsService and NewsViewer each verify that signature **independently** before
consuming the index — a rejected index aborts the team's sync (NewsService) or skips the team
(NewsViewer). The signed index also carries a **SHA-256** hash for each content image, which
NewsViewer re-checks against the cached image file before display. Session telemetry
(`session-*.json`) uses a **separate** HMAC-SHA256 mechanism and is unaffected by index signing.
An empty / unconfigured key makes verification a pass-through (`Disabled`) unless fail-closed
enforcement is turned on — see §4.

## 2. Integrity values

Three independent integrity values protect different artifacts:

| Value | Algorithm / format | Computed by | Stored in | Verified by |
|---|---|---|---|---|
| **Index signature** | ECDSA P-256, SHA-256, IEEE P1363 fixed r‖s (64-byte), Base64 | NewsCentral `IndexGenerationService` → `EcdsaSignatureService.Sign` | `index.json` top-level `signature` field | NewsService `SyncService`, NewsViewer `PresentationSelector` (each via `SignatureGate`) |
| **Image hash** | SHA-256, lower-case hex, `sha256:{hex}` prefix | NewsCentral `IndexGenerationService.BuildContentInfoAsync` | `content.imageHash` **inside** the signed index (so the hash is itself signature-covered) | NewsViewer `PresentationSelector.VerifyImageHash` at display time |
| **Session telemetry** | HMAC-SHA256, Base64 | NewsViewer `TelemetryWriter` → `HmacService.Sign` | `session-*.json` `signature` field | NewsService `TelemetryUploader` → `HmacService.Verify` |

**Index signature — canonical payload.** `EcdsaSignatureService.Canonicalize` temporarily nulls the
`Signature` field — and, for key-carrying indexes, the delivered `SigningPublicKey` (via
`IDeliveredKeyCarrier`) — then serializes with **non-indented, camelCase, enums-as-strings** JSON
(`CanonicalOptions`). Nulling `SigningPublicKey` combined with `[JsonIgnore(WhenWritingNull)]` keeps
the canonical bytes bit-identical whether or not a delivered key is present, so the signature never
covers the delivered key and every pre-key static signature still verifies. **Whole-second
timestamps** are not produced by the canonical serializer itself — before signing, the index is
round-tripped through `JsonConfiguration.GetIndexJsonOptions()` (which uses `SmartDateTimeConverter`
to truncate `DateTime` to whole seconds), so the signed payload matches the persisted-form bytes
exactly (see §6).

**Image hash.** `BuildContentInfoAsync` streams each image through SHA-256 and stores
`sha256:{hex}` in `content.imageHash`. Because that field lives inside the signed index, tampering
with either the recorded hash or the index is caught by the signature; tampering with the cached
image file alone is caught by NewsViewer's display-time re-hash. NewsService's sync loop compares
the stored sidecar hash against `content.imageHash` for **change detection** (decide whether to
re-download) — it does not perform a separate display-time image integrity check; that check is
NewsViewer's.

## 3. Configuration & registry

Every anti-tamper-relevant value. Registry paths are under
`HKLM\Software\[Company]\NewsCentral\<Component>\` (see `docs/configuration.md` for the full layout;
`[Company]` is the build-time constant `SolutionConstants.Company` (authored in `Directory.Build.props`) and `NewsCentral` is `SolutionConstants.SolutionName`; neither is registry-overridable).

| Value | Registry path (per component) | Type | Default | Read by | Effect |
|---|---|---|---|---|---|
| Team public key | `…\<Component>\Signing\{teamFolderName}\PublicKey` | `REG_SZ` (Base64 SPKI) | — | NewsService, NewsViewer | Verification key for the team's `index.json`. Surfaced as `Signing:{team}:PublicKey`; read by `SigningKeyConfigurationReader.GetPublicKeys`. |
| Previous public key | `…\<Component>\Signing\{teamFolderName}\PublicKeyPrevious` | `REG_SZ` (Base64 SPKI) | — | NewsService, NewsViewer | Rotation window — verification accepts a signature made by either key (current tried first, then previous). |
| Require signed index | `…\<Component>\Signing\RequireSignedIndex` | `REG_SZ` `"true"`/`"false"` | `false` | NewsService, NewsViewer | Fail-closed switch — when `true`, `Unsigned` and `Disabled` are rejected in addition to `Invalid` (§4). |
| Bypass image integrity | `…\NewsViewer\BypassImageIntegrityCheck` | `DWORD` (`0`/`1`) | `0` (false) | NewsViewer | `1` skips the display-time image SHA-256 re-check (dev/test). |
| HMAC secret key | `…\<Component>\Hmac\SecretKey` | `REG_SZ` (Base64 32-byte) | `""` | NewsViewer (sign), NewsService (verify) | Telemetry HMAC key. Empty = HMAC disabled (telemetry treated as `Disabled` and passed through). Not used for index signing. |

**Separate `Signing\` subtrees.** NewsService and NewsViewer each read their **own**
`…\NewsService\Signing\…` and `…\NewsViewer\Signing\…` subtrees — the public keys and
`RequireSignedIndex` are configured per component and do not cross over. Deploy a team's public key
(and the flag) to **both** components if both must verify that team.

**Authoring-side key material.** The **private** key (and the current/previous public keys) lives in
`{teamFolderName}/team-signing.json` (`TeamSigningKeys`) on the **authoring tier only** — never
written through `IBlobDistributionService`, never synced to client machines. NewsCentral loads it via
`IndexGenerationService.LoadTeamSigningKeysAsync` to sign; the Key Management page manages it (see
`docs/newscentral-spec.md`). The public key is what gets distributed — to the registry (static teams)
or with the content as `SigningPublicKey` (dynamic teams, §5).

## 4. Enforcement behavior

**`SignatureGate.ShouldReject(result, requireSignedIndex, out reason)`** — the accept/reject matrix,
identical across both verifiers:

| `VerifyResult` | `RequireSignedIndex = false` (default) | `RequireSignedIndex = true` |
|---|---|---|
| `Valid` | accept | accept |
| `Invalid` | **reject** | **reject** |
| `Unsigned` | accept (warning) | **reject** |
| `Disabled` | accept | **reject** |

`Disabled` means no verification key was available at all (no registry key, and — for a static team —
no delivered key is ever consulted). Default `false` preserves phased-rollout pass-through: only a
present-but-wrong signature (`Invalid`) is rejected.

**Per-module reject action:**

- **NewsService `SyncService.SyncTeamAsync`** — on reject, logs `Error` with the gate reason and
  **returns for that team** (aborts its sync; no images downloaded, cached index left as-is). Accept
  paths: `Unsigned` → `Warning`, everything else → `Information`.
- **NewsViewer `PresentationSelector.EnumerateVerifiedAssignments`** — on reject, logs the reason and
  **`continue`s past the team** (its assignments are never yielded, so nothing from it is shown or
  applied as wallpaper). Accepted teams' assignments are yielded normally.

**NewsCentral signer modes** (`IndexGenerationService.GenerateIndexForTeamAsync`):

- **Sign** — `team-signing.json` has a `PrivateKey`: the index is signed and the team's current
  `PublicKey` is embedded as `SigningPublicKey` (key-with-content, §5).
- **Unsigned-with-warning** — no signing key: `Signature = null`, published unsigned with a warning
  (phased-rollout safe; consumers treat it as `Unsigned`).
- **Self-verify guard aborts publish** — after signing, the index is serialized with the persisted
  options and re-deserialized with the client-verifier options (case-insensitive, enum converter, **no**
  `SmartDateTimeConverter`), then re-verified against the team's own public key. Any result other than
  `Valid` throws `InvalidOperationException` and **aborts the publish before any file is written or
  pushed** — catching canonical/persisted-form drift at the authoring tier instead of shipping an index
  every client would reject.

## 5. Static vs dynamic (Entra) key-trust model

A team's verification key is chosen by **precedence** in
`SignatureGate.VerifyWithPrecedence(index, registryPublicKeys, isDynamicTeam)`:

| Condition | Key used | Result |
|---|---|---|
| Registry public key configured (either current or previous non-empty) | Registry key(s), dual-key rotation | **Registry always wins** — delivered key never consulted |
| No registry key, **dynamic** team, `SigningPublicKey` present | Delivered key | verified against the delivered key |
| No registry key, **dynamic** team, `SigningPublicKey` absent | — | `Unsigned` |
| No registry key, **static** team | — | `Disabled` (delivered key never consulted) |

Callers do `VerifyWithPrecedence(...)` → `ShouldReject(...)`; the §4 matrix then applies uniformly to
whatever result precedence produced (so `RequireSignedIndex = true` rejects a `Disabled` static team
with no registry key and a dynamic team carrying no delivered key alike).

**Why dynamic teams use key-with-content.** Entra-resolved teams (attribute- or group-derived; see
`docs/entra-dynamic-teams.md`) appear at runtime and cannot be pre-provisioned with a registry public
key. Instead NewsCentral emits the team's public key **with the content** in
`TeamIndexFile.SigningPublicKey` (Base64 SPKI, the same form as the registry key) for **all** teams —
a static consumer ignores it via precedence, a dynamic consumer verifies against it. Whether a team is
static or dynamic is a property of the **consuming machine** (does `resolved-teams.json` list it?),
not of the team, which is why every index carries the key.

**Weaker anchoring, and its mitigation.** A delivered key is only as trustworthy as the distribution
channel that carried it — authenticity for dynamic teams rests on **distribution-tier RBAC** (only
authorized publishers can write a team's `index.json` + `SigningPublicKey`). The model is **not**
protective against an attacker who can write the distribution channel or holds local admin on the
consuming machine. Anti-downgrade guarantees limit the blast radius: (1) the delivered key is **never**
used for a static team, so a key cannot be attached to downgrade a statically-trusted team; (2) a
present registry key **always** overrides the delivered key. To harden a dynamic team, **deploy a
registry `PublicKey` for it** — precedence makes the registry key win, pinning trust exactly as for a
static team.

**`resolved-teams.json`.** The list of dynamic teams NewsService resolves each cycle carries **no
signatures** — it is a local-tier file trusted via **`%programdata%` cache ACLs**, holding only team
identity + grace state. NewsViewer reads only `TeamFolderName` from it and unions those with its
registry static team list (`EffectiveTeams.Union`); it never writes the file. See
`docs/data-model.md` (ResolvedTeams) and `docs/entra-dynamic-teams.md`.

## 6. Operational notes

**Dual-key rotation sequence** (static teams — dynamic teams are rotation-free because the key travels
with the content):

1. Generate a new key pair (Key Management page or `SigningKeyTool.GenerateKeyPair`). Applying it sets
   `PublicKeyPrevious = old PublicKey` in `team-signing.json`.
2. Deploy the **new** `PublicKey` to each verifier's registry, keeping the **old** key as
   `PublicKeyPrevious` — verification then accepts a signature from either key during the window.
3. Wait for registry propagation to reach all consuming machines.
4. Republish the index (now signed with the new key) — a deliberate action on the Key Management page.
5. Once every consumer has the new key and the republished index, clear `PublicKeyPrevious`.

**Persisted-form signing.** `index.json` is serialized with `JsonConfiguration.GetIndexJsonOptions()`
(`SmartDateTimeConverter`, which truncates timestamps to whole seconds). The index is normalized
through those options **before** signing so the signed bytes equal the on-disk bytes. Verifiers parse
with plain ISO (`JsonDefaults.Options`, no `SmartDateTimeConverter`), which faithfully round-trips
whole-second timestamps. Changing the serializer options on either side without re-signing makes every
verifier return `Invalid` — the publish-time self-verify guard (§4) is the tripwire for exactly this
drift.

**Change detection vs. signature.** `IndexHash` (SHA-256 over the index's content fields) is what
drives per-cycle change detection — an unchanged `IndexHash` short-circuits the sync with no I/O. The
ECDSA **signature** governs trust, not change detection; the two are independent.

**Cache ACLs.** The `%programdata%\NewsCentral\` cache — the `index.json` copies, the cached images,
and `resolved-teams.json` — is protected by filesystem ACLs. That protection is the trust anchor for
the local consumption leg (especially the unsigned `resolved-teams.json` and, for dynamic teams, the
delivered-key path); it is a deployment prerequisite. A future anchored-root upgrade (Option B in
`docs/future.md`) would harden the delivered-key leg beyond ACLs.
