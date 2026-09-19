# Mashal Mobile — Backend Integration

Scope: how the Flutter app (`mobile/mashal_mobile`) talks to `src/PTGOilSystem.Web` without
changing web behavior or business rules. Phase 1 findings are kept at the bottom; Phase 3 status
is first.

## 1. Status (Phase 3)

| Item | Status | Evidence |
|---|---|---|
| Dual auth: web cookie (unchanged, default) + JWT Bearer for `/api/mobile` | Done | `Security/AuthenticationSetup.cs`; tests `Web_Cookie_Still_Authenticates_Web_Endpoints_But_Not_The_Mobile_Api`, `Web_Endpoints_Keep_Cookie_Redirects_And_Ignore_Bearer_Tokens` |
| Refresh-token table + migration | Done | `Models/Entities/MobileRefreshToken.cs`, migration `20260915011942_AddMobileRefreshTokens`; `has-pending-model-changes` = none |
| Refresh rotation, replay detection, logout, per-device revocation | Done | `Security/Mobile/MobileTokenService.cs`; `MobileApiAuthTests` |
| API navigation authorization (403 JSON, no redirect) | Done | `RoleNavigationAuthorizationFilter` API branch + `[ApiNavigation]` |
| ProblemDetails error contract | Done | `Infrastructure/Api/*`; `Api_Errors_Map_To_ProblemDetails_Without_Leaking_Internal_Details` |
| Dev auto sign-in never authenticates `/api` | Done | `DevAutoSignInMiddleware`; `Development_Auto_Sign_In_Never_Authenticates_Api_Requests` |
| Idempotency infrastructure (no write endpoint exposed) | Done | `RequireIdempotencyKeyAttribute`; `Idempotency_Key_Is_Required_Validated_And_Duplicates_Are_Rejected` |
| `GET /api/mobile/v1/me` | Done | `MobileMeController`, `MobileUserProfileService` |
| `GET /api/mobile/v1/dashboard` | Done | `MobileDashboardController`, `MobileDashboardService`; `MobileDashboardServiceTests` |
| Stock discrepancy investigation | Documented | [STOCK_RECONCILIATION.md](STOCK_RECONCILIATION.md) |
| Flutter login / secure tokens / refresh / logout / 401-403-offline handling | Code written, **not compiled** | Flutter SDK is not installed on the build machine |
| Flutter `/me` capability gating | Code written, **not compiled** | same |
| Flutter live dashboard (sample data removed) | Code written, **not compiled** | same |
| Real PostgreSQL run of the migration | **Not run** | no database configured in this session |

## 2. Endpoints

All under `/api/mobile/v1`. JSON is camelCase. Timestamps are UTC ISO-8601.

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `auth/login` | anonymous, `login` rate limit | Username/password → access + refresh token + profile |
| POST | `auth/refresh` | anonymous (refresh token in body) | Rotate refresh token, new access token |
| POST | `auth/logout` | anonymous (bearer and/or refresh token) | Revoke this device's session; always 204 |
| GET | `me` | Bearer | Safe profile, navigation keys, capabilities |
| GET | `dashboard` | Bearer, `heavy-report` rate limit | Home KPIs, shipments, alerts, pending counters |

## 3. Authentication architecture

```text
Browser ──cookie PTGOilSystem.Auth──▶ MVC pages          (default scheme, unchanged)
Phone ───Authorization: Bearer ─────▶ /api/mobile/v1/*  (policy MobileApi = Bearer only)
                                        │
                   JwtBearer validates signature, issuer, audience, lifetime (15 min, 30 s skew)
                                        │
                   OnTokenValidated: load User (IsActive) + check session not revoked
                                        │
                   claims rebuilt by UserClaimsFactory  ◀── same factory as web login
                                        │
                   same RoleAccessRules / policies / RoleNavigationAuthorizationFilter
```

- **One user model.** Web login (`AuthController`) and mobile both use `UserClaimsFactory.Build(user)`;
  there is no second permission system.
- **JWT contents:** only `sub` (user id), `sid` (session id), `jti`, `iss`, `aud`, `iat`, `nbf`, `exp`.
  No role, permission, name or financial data. Role and permissions are reloaded from the database on
  every request, so a disabled user or a changed role takes effect immediately.
- **Cookie is not accepted on the mobile API** (policy scheme = Bearer), so there is no CSRF surface.
  A bearer token is not accepted on web pages (default scheme = cookie).
- **Credentials** are checked by the existing `IUserService.VerifyPasswordAsync` (active users only).
  Lockout uses the existing `ILoginAttemptGuard`, and audit uses the same `LoginAuditActions`, so failed
  web and mobile attempts share one counter. Inactive users get the same generic 401 as a wrong password.
- **Users have no company/branch link** in the current model; `/me` returns the single owner company
  from `ISystemCompanyProvider`.
- **Signing key:** `MobileAuth:SigningKey` or env `PTG_MOBILE_JWT_SIGNING_KEY`, minimum 32 bytes, never in
  appsettings. Without it, production keeps the web running and mobile login returns
  `503 mobile_auth_unavailable`. Development uses a random temporary key (tokens die on restart).
- Options (`MobileAuth` section): `Issuer`, `Audience`, `AccessTokenMinutes` (15), `RefreshTokenDays` (30),
  `ClockSkewSeconds` (30).

## 4. Refresh-token lifecycle and revocation

Table `MobileRefreshTokens` (new migration only; no existing table touched):
`Id, UserId (FK Users, cascade), TokenHash (SHA-256 hex, unique), SessionId (uuid, indexed), ExpiresAtUtc,
LastUsedAtUtc, RevokedAtUtc, RevokedReason, ReplacedByTokenId (self FK), DeviceId, DeviceName, CreatedByIp,
RevokedByIp` + the standard `BaseEntity` audit columns. Index `(UserId, RevokedAtUtc)`.

1. **Login** creates a session (`SessionId`) and its first token. The raw token (64 random bytes,
   base64url) is returned once and never stored or logged.
2. **Refresh** atomically claims the current row (`UPDATE … WHERE RevokedAtUtc IS NULL`, inside a
   transaction), marks it `Rotated`, inserts a replacement in the same session, and links
   `ReplacedByTokenId`. The session expiry is absolute: rotation does not extend it.
3. **Replay:** presenting a `Rotated` token, or losing the atomic claim to a concurrent request, revokes
   the whole session (`ReuseDetected`) and writes a `Security` audit entry. The phone must sign in again.
   A client retry after a lost refresh response therefore ends the session; this is intentional.
4. **Logout** revokes the session identified by the bearer token and/or the refresh token.
   Other devices of the same user stay signed in.
5. **Automatic revocation:** user deactivated (on next refresh; access tokens rejected immediately),
   password change or reset (`UserService`, same save).
6. Revoking one lost phone later = revoke its `SessionId` (`IMobileTokenService.RevokeSessionAsync`).
   No device-management UI yet.

## 5. `/me` contract

```json
{
  "userId": 7, "username": "operator1", "displayName": "…", "role": "Operator",
  "permissions": ["ManageData"],
  "navigation": ["Dashboard", "Contracts", "Operations", "…"],
  "capabilities": { "dashboard": true, "operations": true, "inventory": true, "sales": true,
                    "finance": true, "reports": true, "manageData": true },
  "company": { "id": 1, "name": "…", "namePersian": "…" },
  "language": "fa-AF"
}
```

`navigation` uses the web `RoleNavigationKeys`. `finance` = CashAccounts or Payments. Capabilities only
drive the UI; every endpoint is authorized on the server.

## 6. Dashboard contract

`GET /api/mobile/v1/dashboard` → `MobileDashboardResponse`. A section is `null` when the user cannot open
its source report on the web.

| Field | Source (no new formula) | Visible with nav key |
|---|---|---|
| `inventory.totalMt` | `IStockService.GetTotalFreeQuantityMtAsync()` | Dashboard |
| `inventory.lowStockTankCount` | `IDashboardService` (web dashboard value) | Dashboard |
| `todaySales` | `IDashboardService` (`TodaySalesUsd/Count`) | Dashboard |
| `activeShipments`, `alerts`, `pendingActions` | `IDashboardService` | Dashboard |
| `goodsInTransit` | `IGoodsInTransitReader` (code moved verbatim from `ReportsController.GoodsInTransit`) | Reports |
| `receivables` | `IPartyBalanceReadService`, external customers; positive closing = customer owes; advances separate | Reports |
| `todayProfit` | `IProfitAndLossService.BuildCompanyAsync(today..today)` sales gross profit + `confidence` | Reports |
| `cashPosition` | `ICashPositionReader` (query moved verbatim from `PaymentsController`) | Payments |

Two web files were touched only to share code, with no formula change: `ReportsController.GoodsInTransit.cs`
now calls `IGoodsInTransitReader`, and `PaymentsController.BuildSummaryCoreAsync` calls `CashPositionReader`.
Existing `GoodsInTransitReportTests` and `PaymentsController*` tests cover both.

Inventory and goods in transit are shown side by side and must never be added together in the app.

## 7. Error contract

RFC 7807 `application/problem+json` with `status`, `title`, `detail` (Dari/Persian), `code`, `traceId`,
and `errors` for validation. Stack traces, SQL and exception text are only logged.

| Status | `code` | App reaction |
|---|---|---|
| 400 | `validation`, `idempotency_key_required`, `idempotency_key_invalid` | show field errors |
| 401 | `unauthorized`, `invalid_credentials` | login error |
| 401 | `token_expired` | refresh once, retry |
| 401 | `session_revoked`, `refresh_token_invalid`, `refresh_token_reused` | sign out |
| 403 | `forbidden` | no-access view, no retry |
| 404 / 405 | `not_found`, `method_not_allowed` | not found |
| 409 | `conflict`, `duplicate_request` | reload / treat as already done |
| 422 | `business_rule` (+ `ruleCode`) | show server message |
| 429 | `rate_limited`, `account_locked` | wait |
| 500 | `server_error` | generic error |
| 503 | `mobile_auth_unavailable` | server not configured |

## 8. Idempotency (for future write endpoints)

No write endpoint is exposed in Phase 3. Every future mobile write (loading, receipt, sale, payment,
expense, inventory mutation) must:

1. Be marked `[RequireIdempotencyKey("Mobile.<Entity>.<Action>")]`. The header `Idempotency-Key` must be a
   UUID; missing → 400, reused → 409 `duplicate_request` with `referenceType/referenceId`.
2. Call `MobileIdempotency.Stamp(HttpContext, formTokenGuard, "<Entity>")` before `SaveChanges`, in the same
   transaction as the record, so the existing unique index `IX_ProcessedFormTokens_Token` is the final
   guarantee (no new table).
3. Catch `IFormTokenGuard.IsDuplicate` → 409 `duplicate_request`.
4. Set `ReferenceId` on the processed token after save, so a retry can return the original record (not
   implemented yet).
5. Go through the same service/posting path as the web action (stock, ledger, audit, period lock,
   concurrency `version`), never a mobile-only copy.

Flutter: generate one `newUuidV4()` per user action and reuse it for every retry of that action
(`ApiClient.postJson(..., idempotencyKey:)`).

## 9. Security behavior

- HTTPS required in release builds (`AppConfig.requireHttps`); tokens in Keychain / Android Keystore-backed
  encrypted preferences (`flutter_secure_storage`). Passwords are never stored. No HTTP logging.
- Access 15 min; refresh single-use; replay revokes the session; logout per device.
- Mobile login failures count toward the shared lockout; login is behind the existing `login` rate limit
  (429 JSON on `/api`).
- **Open issue (unchanged, pre-existing):** `ForwardedHeaders` clears `KnownProxies/KnownNetworks`, so
  `X-Forwarded-For` can be spoofed and IP-based lockout/rate limiting can be bypassed. Must be fixed before
  exposing the token endpoint to the internet. Not changed in this phase.

## 10. Not implemented (need a business decision first)

Approval workflows (payments, expenses, loss adjustments), customer credit limits and overrides, sale due
dates / overdue rules, push notifications, and any mobile write endpoint. The app shows Approvals and
Notifications as clearly labelled unavailable placeholders.

## 11. Verification

Backend (run in this session): web build, `has-pending-model-changes`, targeted tests and the full test suite.
Results are in the Phase 3 implementation report.

Flutter (**not run — Flutter SDK is not installed**). Required once it is installed:

```bash
cd mobile/mashal_mobile
flutter create . --org com.saddiqi --project-name mashal_mobile --platforms=android,ios
flutter pub get
flutter analyze
flutter test
flutter build apk --debug --dart-define=MASHAL_API_BASE_URL=https://<host>
flutter build ios --no-codesign --dart-define=MASHAL_API_BASE_URL=https://<host>   # macOS only
```

Server before first mobile login: set `PTG_MOBILE_JWT_SIGNING_KEY` (≥ 32 random bytes) and apply migration
`AddMobileRefreshTokens` (auto-migrate on startup, or `dotnet ef database update`) after a verified backup.

## 12. Next phase (Phase 4)

Read-only Operations, Sales and Finance lists/details (loading, transport legs, receipts, tanks, losses,
sales, customer balance, receivables, cash) under `/api/mobile/v1`, each on an existing reader/service with
an explicit `[ApiNavigation]` key and pagination; plus the `ForwardedHeaders` hardening above.

---

## Appendix — Phase 1 findings (backend as found)

- One ASP.NET Core MVC project (.NET 8), Razor views, no `[ApiController]` before this work.
- Cookie auth (`PTGOilSystem.Auth`, 12 h sliding); roles Admin/Manager/Operator/Viewer; navigation claims;
  policies `ManageData`, `AdminOnly`, `BackupAdmin`, `OperationalPeriodAdmin`.
- Global filters: `AutoGeneratedCodeFilter`, `RoleNavigationAuthorizationFilter`, `QuickCreateResultFilter`,
  `ClosedPeriodOverrideFilter` (form POST only), `BusinessRuleExceptionFilter`.
- Write safety already present: `FormTokenGuard` + `ProcessedFormToken`, `IVersionedEntity.Version`,
  `OperationalPeriodGuard`, `AuditService`.
- Existing JSON actions (`Sales/SuggestedPrice`, `Payments/PartyBalance`, …) are Razor-form helpers, not
  API contracts, and are not used by the app.
