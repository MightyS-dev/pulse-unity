# Changelog

All notable changes to the Pulse Unity SDK (`dev.mightys.pulse`) are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.9.5] - 2026-11-16

### Added

- Each batch carries an id (header `X-Pulse-Batch`), kept for every retry of that batch, so a batch sent again after a
  lost answer is stored only once.

## [0.9.4] - 2026-11-16

### Added

- Automatic screen views: `pulse.screen_view` with `screen` (scene name, at most 128 characters) and `previousScreen` when
  the active scene changes. On by default; `PulseOptions.AutomaticScreenViews = false` turns it off. `Pulse.Screen(name)`
  records a screen by hand.
- Settings from the portal: screen views, automatic signals and groups of details can be switched off per app without an
  update. The SDK reads them at start and keeps them between launches, so changes apply from the next launch.
  `PulseOptions.AutomaticSignals = false` turns the automatic signals off in code. Code options only turn things off.
- Default device and app details that identify no one: device type, brand, architecture, Android API level, Unity
  version, scripting backend, product name, install source, simulator, development build, screen size, scale, dpi and
  count, orientation, color scheme, time zone and UTC offset, language, preferred language, region, connection type and
  low power mode. A value the platform cannot give is left out.
- Usage statistics with every signal: first session date, session count, distinct days used (lifetime and last 30
  days), previous, average and current session length.
- Automatic signals `pulse.new_install`, `pulse.app_updated` and `pulse.session_started`, sent before the first signal
  of the session that triggers them.

### Changed

- The bundle id is sent by default. The Android signing certificate digest stays opt-in.
- Opt-out also deletes the usage statistics and the saved settings.

## [0.9.2] - 2026-11-15

### Added

- Users are counted without any setup: the SDK sends a random anonymous install id (hashed) until you call `Pulse.SetUser`.

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
