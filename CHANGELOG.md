# Changelog

All notable changes to the Pulse Unity SDK (`dev.mightys.pulse`) are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.9.0] - 2026-11-15

Initial public release.

### Added

- `Pulse.Initialize`, `Pulse.Track`, `Pulse.SetUser`, `Pulse.Flush` and `Pulse.OptOut`. No scene component is needed.
- Offline queue on disk. Queued signals survive restarts and are sent when the connection is back.
- Signals are sent in batches in the background, compressed when large, and retried automatically after a failure.
- Privacy by default: no advertising identifier, no device identifier and no user identity unless `SetUser` is
  called, and then only its SHA-256 hash is kept. `OptOut` stops collection and clears queued signals.
- System details with each batch: platform, OS name and version, app version and build, device model, locale, SDK name
  and version.
- Test mode: signals from the editor and from development builds are marked as test data; `PulseOptions.Debug`
  overrides it.
- Sample "Basic": three buttons for tracking and flushing.
