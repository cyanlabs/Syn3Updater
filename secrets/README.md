# Build-time secrets

`api-token.txt` holds the CyanLabs API bearer token (one line). It is **git-ignored** and never committed.

At build time `Syn3Updater.Core.csproj` reads it (or the `SYN3_API_TOKEN` environment variable, which wins - handy for CI)
and generates `ApiSecret.g.cs` in `obj/`, so the token is compiled into the binary instead of being a user setting.
Without a token the app still builds, but API calls will be rejected.

`sentry-dsn.txt` (optional) holds the crash report server's DSN (one line), read the same way (`SYN3_SENTRY_DSN` wins). It is not a secret, but it stays out of git too.
Without it the app never offers to send a crash report.
