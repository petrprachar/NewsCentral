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
