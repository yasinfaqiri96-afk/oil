# Mashal Mobile

Flutter client (Android / iOS) for PTG Oil System. Talks only to the ASP.NET Core API under
`/api/mobile/v1`, never to PostgreSQL. Architecture and contracts:
[docs/INTEGRATION_PLAN.md](docs/INTEGRATION_PLAN.md).

> Status: the Flutter code has **not been compiled or tested yet** — the Flutter SDK was not
> available when it was written. Run the checklist below before using it.

## First-time setup

Platform folders are not committed. Generate them once (existing `lib/` and `test/` files are kept):

```bash
cd mobile/mashal_mobile
flutter create . --org com.saddiqi --project-name mashal_mobile --platforms=android,ios
flutter pub get
```

## Run

The server URL is required (HTTPS in release builds):

```bash
flutter run --dart-define=MASHAL_API_BASE_URL=https://your-host
```

For a local debug server over plain HTTP (debug builds only):

```bash
flutter run --dart-define=MASHAL_API_BASE_URL=http://10.0.2.2:5000   # Android emulator → host
```

The server must have `PTG_MOBILE_JWT_SIGNING_KEY` set (≥ 32 bytes) and the
`AddMobileRefreshTokens` migration applied, otherwise login returns "mobile sign-in unavailable".

## Verification checklist (not yet run)

```bash
flutter analyze
flutter test
flutter build apk --debug --dart-define=MASHAL_API_BASE_URL=https://your-host
flutter build ios --no-codesign --dart-define=MASHAL_API_BASE_URL=https://your-host   # macOS
```

## Rules

- All numbers (stock, balances, profit) come from the server. The app formats them; it never computes them.
- Tokens live only in secure storage; passwords are never stored; HTTP traffic is never logged.
- Capabilities from `/me` only hide UI. The server authorizes every request.
- Features without a backend (approvals, push notifications, write operations) are shown as unavailable.
