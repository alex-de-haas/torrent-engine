# Agent Instructions for Torrent Engine project

## Versioning

This app uses semantic versioning `major.minor.patch`, bumped per release. When a
change ships, bump the app version in the **same commit**:

- **patch** — bug fix or small enhancement to existing functionality.
- **minor** — new functionality, or a large/breaking change (while the app is in `0.x`).
- **major** — reserved until the app declares a stable `1.0.0`; after that, breaking
  changes for the app's users.

Documentation-only changes (`docs/`, `README.md`, `AGENTS.md`) are the exception —
merge them without a version bump.

Where the version lives:

- `version` in `manifest.json` is the app's release version and the source of truth —
  bump it in the same change that ships the work.
- Do **not** bump `schemaVersion` (`app.0.1`) for ordinary changes — it tracks the Hosty
  manifest *contract* format, not this app.

Each runtime app versions independently from Hosty Core/CLI and from the other apps.

## Pull Requests

- **Do not squash-merge PRs.** Parallel PRs are common, and squash merges rewrite the
  merged branch's history — the other in-flight branches can no longer rebase cleanly
  onto main. Use a regular merge commit instead.
- **One PR per feature, not per phase.** When a feature plan is split into phases,
  implement all phases on one branch and open a single PR. Individual phases rarely
  deliver complete functionality on their own, and under the versioning rules above
  each per-phase PR would pointlessly bump the version.

## Documentation
- Planning and reference docs live in [`docs/`](docs/root.md); start at
  `docs/root.md`. Each subsystem has a feature doc under `docs/features/`.
- A feature doc opens with a `Status:` / `Created:` / `Updated:` header, then a
  `## Description`, and ends with `## Testing Expectations`. Keep it in sync with the
  code it documents.
- When a feature finishes development, update its doc's `Status:` (and the `Updated:`
  date) in the same PR that ships the work — a shipped feature must not stay marked
  as planned or in progress.

## Unit Testing
- Use `xUnit` for backend unit tests.
- Use `Imposter` for mocking dependencies in tests.
- Endpoint tests host `MapTorrentEndpoints` on an in-memory
  `Microsoft.AspNetCore.TestHost` server.
- Ensure all new features have corresponding unit tests.

## Native AOT
- The app publishes as Native AOT (`PublishAot=true`). Avoid runtime reflection /
  dynamic code: serialize through the source-generated `AppJsonSerializerContext`,
  and root any assembly the trimmer can't see (as `MonoTorrent.Client` is rooted for
  MonoTorrent state serialization).
