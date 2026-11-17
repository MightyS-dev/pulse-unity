# Changelog

All notable changes to the Pulse Unity SDK (`dev.mightys.pulse`) are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.9.7] - 2026-11-17

### Added

- The built-in update and maintenance screens are not counted as app screens or taps.
- Errors and crashes: unhandled exceptions are sent as `pulse.error` (kind `error`, or `crash` when the runtime is
  ending; a crash is saved at once and sent at the next launch). Pulse wraps `Debug.unityLogger.logHandler` and always
  forwards to the previous handler; `Application.logMessageReceived` is only a fallback for an exception that reached
  Unity as text (its type and frames are read from the text), never a second copy; `AppDomain.UnhandledException` and
  `TaskScheduler.UnobservedTaskException` cover other threads. The hooks are installed after `Initialize` and removed at shutdown.
- `Pulse.TrackError(exception, props)` for handled errors.
- `PulseOptions.CaptureErrors` (default true) and `PulseOptions.ErrorDetail` (`Full` or `TypeOnly`).
- Heatmaps: taps and clicks are sent as `pulse.heatmap` (screen name, position, kind `tap` or `rage`), off until the
  portal switches them on. Option `PulseOptions.CaptureHeatmaps` (default true, can only switch off) and the
  `PulseHeatmapIgnore` component. Nothing is captured while the keyboard is visible or on text input fields. In
  development builds one picture per screen is uploaded; release builds never send pictures. The package now lists the
  built-in modules `com.unity.modules.screencapture` and `com.unity.modules.imageconversion`.
- Personal data in messages and frames (emails, addresses, long numbers, UUIDs, URL query strings, folder paths) is
  replaced with `<filtered>` on the device. At most 10 errors per type and session.
- App Control: required updates, blocked versions, maintenance windows, suggested updates and short notices set in the
  dashboard. It checks once after `Initialize` and whenever the app returns to the front (at most once in 60 seconds), keeps
  the last known state when a check fails, and draws its own screens (uGUI from code, no assets, no TextMeshPro, 12 languages,
  light and dark, safe area). It never changes `Time.timeScale` or the audio and never closes the app. The request carries no
  app key and no identity. `Pulse.Control` (`State`, `Changed`, `CheckNowAsync()`, `OpenStore()`, `Dismiss()`,
  `OnForeground()`), option `PulseOptions.Control` (`Automatic`, `Manual`, `Off`), `PulseControlPreview` and the
  "App Control demo" sample. uGUI is optional (no package dependency): without `com.unity.ugui` the screen is left out and `Automatic` acts as `Manual`. The screen language comes from `Application.systemLanguage` first.
- Each batch carries an id (header `X-Pulse-Batch`), kept for every retry of that batch, so a batch sent again after a
  lost answer is stored only once.
- Automatic screen views: `pulse.screen_view` with `screen` (scene name, at most 128 characters) and `previousScreen` when
  the active scene changes. On by default; `PulseOptions.AutomaticScreenViews = false` turns it off. `Pulse.Screen(name)`
  records a screen by hand.
- Settings from the portal: screen views, automatic signals and groups of details can be switched off per app without an
  update. The SDK reads them at start (GET with the app key, kept between launches), so changes apply from the next
  launch. `PulseOptions.AutomaticSignals = false` turns the automatic signals off in code. Code options only turn things
  off.
- Default device and app fields that identify no one: device type, brand, architecture, Android API level, Unity
  version, scripting backend, product name, install source, simulator, development build, screen size, scale, dpi and
  count, orientation, color scheme, time zone and UTC offset, language, preferred language, region, connection type and
  low power mode. Values that can change (screen, orientation, color scheme, connection, low power mode, time zone) are
  read on the main thread at start, on focus and every 10 seconds. A value the platform cannot give is left out.
- Usage statistics with every signal: first session date, session count, distinct days used (lifetime and last 30
  days), previous, average and current session length.
- Automatic signals `pulse.new_install`, `pulse.app_updated` and `pulse.session_started`, sent before the first signal
  of the session that triggers them.
- Users are counted without any setup: the SDK sends a random anonymous install id (hashed) until you call setUser.

### Changed

- A session ends after 30 minutes without a signal, measured from the last signal; flushes and timers do not extend it,
  and a signal dropped as invalid neither starts nor extends a session.
- `Flush` with nothing to deliver completes at once, also when storage fails.
- The scene hook is installed only after `Initialize`, and only when screen views are on for that run.
- The bundle id is sent by default. The Android signing certificate digest stays opt-in.
- Opt-out also deletes the usage statistics file.

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
