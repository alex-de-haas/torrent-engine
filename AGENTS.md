# Agent Instructions for Torrent Engine project

## Documentation
- Planning and reference docs live in [`docs/`](docs/root.md); start at
  `docs/root.md`. Each subsystem has a feature doc under `docs/features/`.
- A feature doc opens with a `Status:` / `Created:` / `Updated:` header, then a
  `## Description`, and ends with `## Testing Expectations`. Keep it in sync with the
  code it documents.

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
