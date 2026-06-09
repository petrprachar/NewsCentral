# Security

## Per-Component Authentication

### NewsCentral

- Detects UPN accounts and supports local accounts
- Azure Blob distribution uses `InteractiveBrowserCredential` (MSAL interactive user session); token persisted in named cache `"NewsCentral"` for non-interactive re-authentication. Config: `AzureBlob:{TenantId, ClientId, AccountName}` in appsettings.json.

### NewsService

- Azure Blob access uses `ClientCertificateCredential` or `ClientSecretCredential`; mode selected by `AzureBlob:AuthMode`
- Certificate loaded from `Cert:\LocalMachine\My` by thumbprint — Local System has access by default, no extra grants required
- Proactive token refresh via `Azure.Identity`; handles weeks-long uptime without restart

### NewsViewer

- Architecture is prepared for future authentication requirement to access `%programdata%\NewsCentral\` data
- No authentication implemented in this version

## Anti-Tamper — HMAC-SHA256

`HmacService` (in `NewsCentral.Shared/Security/`) provides end-to-end content integrity using HMAC-SHA256.

**Signing (NewsCentral):** `IndexGenerationService` calls `HmacService.Sign(index)` after `IndexHash` is computed. The signature covers the entire serialized index (excluding the `Signature` field itself) using canonical non-indented camelCase JSON.

**Signing (NewsViewer):** `TelemetryWriter` calls `HmacService.Sign(record)` on each `SessionTelemetry` before writing to disk.

**Verification (NewsService):**
- `SyncService` verifies each `index.json` before caching. `Invalid` → sync aborted for that team. `Unsigned` → warning logged, sync continues.
- `TelemetryUploader` verifies each `session-*.json` before forwarding. `Invalid` → file discarded and logged.

**Verification (NewsViewer):** `PresentationSelector` verifies each team's `index.json`. `Invalid` → team skipped entirely (no presentations displayed from that team). `Unsigned` → warning logged, team accepted. After selecting the winning assignment, `PresentationSelector` also verifies the cached image file against `Content.ImageHash` (SHA-256). A missing hash is accepted with a warning; a mismatch returns `(best, null)` so the caller exits silently. Disabled by `BypassImageIntegrityCheck`.

**Key management:** `Hmac:SecretKey` is a Base64-encoded 32-byte key configured in `appsettings.json` or overridden via registry (`Hmac\SecretKey`). An empty key disables HMAC system-wide — all content is treated as `Disabled` and passes through. This enables phased rollout: deploy the key to all machines before enabling signing in NewsCentral.

**`Signature` field** is present in: `Presentation`, `Schedule`, `Assignment`, `TeamIndexFile`, `SessionTelemetry`.

## Per-Team Asymmetric Signing — ECDSA P-256 (foundation implemented)

`EcdsaSignatureService` (in `NewsCentral.Shared/Security/`) provides ECDSA P-256 / SHA-256 signing and verification as the foundation for replacing the shared HMAC key on `index.json` with per-team asymmetric keys. Session telemetry remains on HMAC-SHA256 and is not affected.

**Algorithm:** ECDSA nistP256, SHA-256 hash, IEEE P1363 fixed-field concatenation (64-byte r‖s, Base64). Never DER.

**Canonical payload:** Identical approach to `HmacService` — `Signature` temporarily nulled, non-indented camelCase JSON with `JsonStringEnumConverter`. Signed bytes are byte-for-byte consistent across both services.

**Key material — `TeamSigningKeys`:** Stored at `{teamFolderName}/team-signing.json` on the authoring tier only. Must never be written through `IBlobDistributionService` or synced to client machines.

| Field | Type | Used by |
|---|---|---|
| `PrivateKey` | Base64 PKCS#8 | NewsCentral — signs `index.json` |
| `PublicKey` | Base64 SPKI | NewsService, NewsViewer — verify `index.json` |
| `PublicKeyPrevious` | Base64 SPKI (optional) | Both verifiers — rotation window |

**Key rotation:** `Verify<T>` accepts `params string?[] publicKeysSpkiBase64`; pass `[PublicKey, PublicKeyPrevious]` to accept content signed by either key during a rotation window. A key attempt that throws counts as that key failing; `Invalid` is only returned after all supplied keys are exhausted.

**Key management — `SigningKeyTool`:** `GenerateKeyPair()` creates a fresh nistP256 key pair and returns Base64 PKCS#8 + SPKI; `DerivePublicKey()` accepts PEM or raw Base64 PKCS#8; `Truncate()` produces a short display string for the UI.

**Current status:** Core implemented in `NewsCentral.Shared`. Wiring into `IndexGenerationService` (NewsCentral), `SyncService` (NewsService), and `PresentationSelector` (NewsViewer) is the next integration step.
