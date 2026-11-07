# Building and testing

## Build and run from source

You need the .NET 10 SDK (`~/.dotnet` is used if present).

```sh
scripts/run.sh                 # run from source
scripts/install-linux.sh       # build and install a launcher entry (Linux)
scripts/publish.sh             # self-contained builds for all platforms -> artifacts/<rid>/
```

Local builds report the version in `Directory.Build.props` (`3.0.0`). Test and release versions come from the release tag in CI.

## API token

The CyanLabs backend needs a bearer token. It is compiled into the app at build time from the git-ignored file `secrets/api-token.txt` (one line),
or from the `SYN3_API_TOKEN` environment variable (which wins; handy for CI). See [secrets/README.md](../secrets/README.md).
It is not a user setting and never appears in the UI or in git. Without a token the app still builds, but API calls are rejected.

## Crash report server (optional)

Crash reports go to a Sentry-compatible server (CyanLabs runs GlitchTip). The DSN is compiled in from the git-ignored `secrets/sentry-dsn.txt` (one line) or the
`SYN3_SENTRY_DSN` environment variable (which wins). It is not a secret: it only allows sending reports. Without one the app never offers to send a crash report.
The app talks to the server with plain HTTP (no Sentry SDK), and only after the user agrees on the next start. See `CrashReporter` in `Syn3Updater.Core`.

## Tests

```sh
dotnet test
```

The suite covers the core logic, the view-models driven through the real workflow with a fake API and dialogs, an end-to-end download / copy run
against a local HTTP server, and a live-API check (it runs when a token is compiled in). `scripts/coverage.sh` prints per-class line coverage.

Language files live in `src/Syn3Updater.Core/Languages/*.json`. A test keeps every language complete and in step with `en-US`, and fails on keys
nothing uses or on text hardcoded in XAML.

## UI previews (no window)

```sh
dotnet run --project tools/Shots -- out 1100 800 Home Settings --demo [--light]
```

This renders pages to PNG with Avalonia's headless backend (nothing is shown on screen). It is useful for checking layouts at different widths and themes.
