# Future Extensions & NewsTester

## NewsTester

**Status:** Future requirement. Not yet designed.

- Independent standalone application
- Runs at standard interactive user level (not elevated)
- Shows prepared presentations as they will appear in NewsViewer
- Enables content authors and approvers to evaluate content before publishing

## Future Extensions

| Item | Notes |
|---|---|
| ~~NewsService Azure mode~~ | Implemented — `AzureBlobRepositoryReader` with Certificate / ClientSecret auth. |
| ~~NewsCentral Azure distribution~~ | Implemented — `AzureBlobDistributionService` uses `InteractiveBrowserCredential` (MSAL interactive, token cached as `"NewsCentral"`). Config: `AzureBlob:{TenantId,ClientId,AccountName}`. |
| NewsCentral web application | May be rewritten as a web application or replaced by an existing portal |
| AI-assisted content generation | Folder structure (`original\`, `generated\`) in place. Poster UI collects headline, body, and CTA text fields (stored as component state); `PosterGenerationService` stores the original image as-is for now. AI text-overlay call is the planned next step — no external AI API keys are configured at this time. |
| NativeAOT for NewsViewer | Migration path preserved; Win32 P/Invoke usage kept compatible |
| NewsTester | Independent preview application for content authors and approvers |
| ~~HMAC anti-tamper~~ | Implemented — `HmacService` in `NewsCentral.Shared/Security/`; `index.json` signed by NewsCentral, verified by NewsService and NewsViewer; session telemetry signed by NewsViewer, verified by NewsService. Key configured via `Hmac:SecretKey`; empty key disables HMAC. |
| ECDSA per-team index signing | Phases A, B1 & C implemented: signing core in `NewsCentral.Shared`; `IndexGenerationService` (NewsCentral) signs with team ECDSA key; `SyncService` (NewsService) and `PresentationSelector` (NewsViewer) verify via `SigningKeyConfigurationReader` + `EcdsaSignatureService` using per-team registry public keys. Remaining: Key Management page in NewsCentral (Phase B2); `Set-RegistryOverrides.ps1` per-team signing params (Phase D). Session telemetry stays on HMAC. |
| Extended presentation selection logic | Current selection (most recent by timestamp) designed as an extensible function |
| NewsViewer authentication | Architecture prepared; not implemented in this version |
