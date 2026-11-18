# MightyS Pulse for Unity

Privacy-first analytics for Unity games and apps. No device IDs, offline queue.

Docs: https://pulse.mightys.dev/docs/unity/

MightyS Pulse lets a Unity game or app send named signals (events) with an optional number and a few properties, and
see them in a dashboard. The package has no external dependencies and needs no scene component: you call it from your
own startup code.

## Install

In Unity, open Window > Package Manager > + > Add package from git URL and enter:

```text
https://github.com/MightyS-dev/pulse-unity.git#0.9.8
```

Or add this line to the `dependencies` of `Packages/manifest.json` (the package name must be exactly `dev.mightys.pulse`):

```json
"dev.mightys.pulse": "https://github.com/MightyS-dev/pulse-unity.git#0.9.8"
```

The part after `#` pins the version; change it to move to a newer release. Use Unity 2022.3 LTS or newer and the
.NET Standard 2.1 API compatibility level in Player settings.

## Quick start

```csharp
using System.Collections.Generic;
using Mightys.Pulse;

Pulse.Initialize("YOUR_APP_KEY");           // the app key from your Pulse dashboard
Pulse.Track("level_complete", 42);          // a named signal with an optional number
Pulse.Track("purchase", 4.99, new Dictionary<string, object> { ["sku"] = "gems_100" });
```

That is all: users are counted without any further setup. Optionally, call `Pulse.SetUser("your-user-id")` to use your
own user id instead of the anonymous install id.

Call `Initialize` once from your startup script. `Track` is thread safe. Public calls never throw into your game and
never wait for disk or network. To try it without writing code, import the Basic sample from Package Manager.

## See your first signal

1. Sign in at [app.pulse.mightys.dev](https://app.pulse.mightys.dev) and open the app whose key you used.
2. A debug or development build sends test data (see Test mode below). Turn on the **Test mode** switch in the bar
   above the figures to see it; turn it off again for real users.
3. Open **Live** in the app menu: signals appear there as they arrive. The Overview and the other screens follow shortly.

## What is sent and what is never sent

Sent:

- The signals you track: name (up to 200 characters), optional number, your properties and a UTC time. Properties may
  be strings, finite numbers and booleans (sent as `"true"` or `"false"`); other values are dropped.
- A random session id that rotates after 30 minutes without a signal.
- Platform, OS name and version, app version and build number, device model, locale, bundle id, and the SDK name and
  version.
- Device and app details that identify no one, each only where the platform can tell: device type (phone, tablet,
  desktop, tv), device brand, CPU architecture, Android API level, Unity version (`Unity 6000.5.3f1`), scripting backend
  (il2cpp or mono), product name, install source (App Store, TestFlight, Play Store, Galaxy Store, Amazon, sideload or
  debug), simulator or emulator, development build, screen size in points and pixels, screen scale and dpi, number of
  displays, orientation, light or dark mode, time zone and UTC offset, app and system language, region, connection type
  (Wi-Fi, cellular, none) and low power mode. A value the platform cannot give is left out, never guessed.
- Usage statistics of this install, kept in a small file next to the install id: first session date, number of
  sessions, distinct days used (lifetime and last 30 days), previous and average session length, and the length of the
  current session.
- Three automatic signals, at most once per session: `pulse.session_started` when a session starts,
  `pulse.new_install` on the first start after install and `pulse.app_updated` (with the previous version and build)
  on the first start of a new version. They come before the first signal of that session.
- A user hash: the SHA-256 hash of the anonymous install id (below), or of your own user id after
  `Pulse.SetUser("id")`. The raw id is never sent. Your user id is not kept across restarts, so call `SetUser` again at
  startup; `SetUser(null)` goes back to the anonymous install id.

Anonymous install id: a random id made on the first start of the app. It is stored only in the app's own storage, is
deleted when the app is uninstalled, and is replaced by a new one after opt-out. It is sent only as a SHA-256 hash. It
is not derived from any device or advertising identifier and is not shared between apps.

Never sent: an advertising id, a device identifier, the device's name, a location, contacts, the network carrier, or
automatic screen or gameplay events.

The SDK does not read or send the device's IP address as data. As with any HTTP request, the server sees the connection
address; it uses it only to derive a two-letter country code and does not store it.

Keep personal data out of signal names and properties. Queued signals are stored on the device without encryption.

## Options

Pass options to `Initialize`:

```csharp
Pulse.Initialize("YOUR_APP_KEY", new PulseOptions { FlushIntervalSeconds = 30, MaxQueueSize = 5000 });
```

| Option | Default | Meaning |
| --- | --- | --- |
| `Host` | `https://in.mightys.dev` | Base URL of the ingest host (HTTP or HTTPS, no user info, query or fragment). |
| `FlushIntervalSeconds` | 20 | Seconds between batches. Maximum 3600. |
| `MaxBatchSize` | 100 | Signals per batch, 1 to 1000. |
| `MaxQueueSize` | 1000 | Signals kept queued, 1 to 1000000. The oldest are dropped first. |
| `MaxQueueAgeDays` | 7 | Age after which a queued signal is dropped. Maximum 29. |
| `Gzip` | `true` | Compress request bodies larger than 1024 bytes. |
| `Debug` | from the build type | Marks signals as test data (see Test mode) and writes diagnostic warnings to the Unity log. |
| `AppVersion` | `Application.version` | Overrides the app version that is sent. |
| `AutomaticScreenViews` | `true` | Sends `pulse.screen_view` when the active scene changes. `false` turns it off. |
| `AutomaticSignals` | `true` | Sends the automatic signals (`pulse.session_started`, `pulse.new_install`, `pulse.app_updated`, `pulse.screen_view`). `false` turns them off. |
| `CaptureErrors` | `true` | Sends unhandled exceptions and crashes as `pulse.error`. `false` installs no handler; `Pulse.TrackError` still works. |
| `ErrorDetail` | `Full` | `Full` sends the type, the filtered message and the stack frames; `TypeOnly` sends the type only. |
| `CaptureHeatmaps` | `true` | Follows the portal for heatmaps of taps and clicks (`pulse.heatmap`), which are off until you switch them on there. `false` never captures them; `true` never forces them on. See Heatmaps. |
| `Control` | `ControlMode.Automatic` | App Control: `Automatic` checks by itself and draws its own screens (when uGUI is in the project, otherwise it acts as `Manual`), `Manual` checks and keeps the state but draws nothing, `Off` makes no request at all. See App Control. |

Options are read once at `Initialize`; changing the object afterwards has no effect.

## Screen views

Screen views are on by default: each time the active scene changes, the SDK sends `pulse.screen_view` with the
property `screen` (the scene name, at most 128 characters) and `previousScreen` when there was one. Activating the same
scene again without leaving it sends nothing. Only the scene name is sent, never a path or anything a player typed.
Opt-out sends nothing.

Turn them off in code:

```csharp
Pulse.Initialize("YOUR_APP_KEY", new PulseOptions { AutomaticScreenViews = false });
```

Name screens yourself (menus, panels inside one scene) with `Pulse.Screen("Settings")`; it works whatever the option.

## Errors and crashes

Unhandled exceptions are sent as `pulse.error` by default, with the exception type, the message and the stack frames
(file name and line, no directory). Emails, addresses, long numbers, UUIDs, URL query strings and folder paths are
replaced with `<filtered>` before anything leaves the device, and the server filters again. At most 10 errors of one
type are sent per session, so a loop cannot use up your quota. Opt-out sends nothing.

- Exceptions logged by Unity (for example from `Update`) are read from the logger. Pulse wraps the current log handler
  and always passes every message on to it, so the Console and other tools see exactly what they saw before.
- Exceptions on other threads and faulted tasks nobody awaited are caught too. When the runtime is ending, the error is
  saved as a crash and sent at the next launch.
- Send a handled error yourself: `Pulse.TrackError(exception)`, optionally with props.

Turn automatic capture off with `new PulseOptions { CaptureErrors = false }`, or send the type only with
`ErrorDetail = PulseErrorDetail.TypeOnly`. Errors appear on the Errors screen of the portal.

## Automatic signals

Besides your own signals, the SDK sends a few automatic ones. Each can be switched in the portal (app settings,
Automatic signals); the full list with what each one costs is at https://pulse.mightys.dev/docs/automatic-signals/.

| Signal or field | What it is | Default | Uses signals |
| --- | --- | --- | --- |
| `pulse.session_started`, `pulse.new_install`, `pulse.app_updated` | sessions, installs and updates | on | free (do not count toward your plan) |
| `pulse.screen_view` | the active scene changed (see Screen views); carries `previousScreenSeconds` | on | one per scene change |
| `launchMs` | time from engine start to the first frame, on the cold start `pulse.session_started` | on | no |
| `foregroundsToday`, `previousForegroundSeconds` | how often and how long the app was in front | on | no |
| `notificationPermission`, `trackingPermission` | the notification permission status (Android and iOS) and the iOS App Tracking Transparency status | on | no |
| `pulse.permission_changed` | one of those statuses changed since the last launch | on | rarely |
| `pulse.deep_link` | the app was opened by a link: scheme, host and path only | on | one per link |
| `pulse.app_opened` | the app came to the front | off | free (does not count toward your plan) |
| `pulse.app_backgrounded` | the app went to the background; carries `foregroundSeconds` and `sessionSeconds` | on | free (does not count toward your plan) |
| `pulse.heatmap` | taps and clicks per screen, see Heatmaps; switch off with `PulseOptions.CaptureHeatmaps` | off | none (own allowance) |

- Statuses are only read, never asked: the SDK never shows a permission prompt, and it reads the tracking status
  without linking App Tracking Transparency, so a game without ATT or without its Info.plist key works as before. The
  advertising identifier is never read.
- Deep links come from `Application.deepLinkActivated` and, once at start, `Application.absoluteURL` on Android and
  iOS. The query, the fragment and every parameter except the three `utm_` campaign tags are never sent.
- Front and back follow pause and resume on every platform, and focus on desktop and in the editor, one report per real change.
  Going to the background saves the session end and flushes the queue immediately. A background signal after 30 minutes
  without a signal is dropped and never starts a session.
- Caps on the device keep automatic signals cheap: per session at most 20 `pulse.app_opened` and
  `pulse.app_backgrounded`, 10 `pulse.deep_link`, 5 `pulse.permission_changed` and 100 `pulse.screen_view`, and at
  most 300 automatic signals per device and day. Your own signals are never capped. When the plan reaches 90%, the
  portal can pause counted automatic signals and screen views until the next month. The five free lifecycle types
  above do not count toward your plan; above 50 per app, user and UTC day they count like other signals.

## Heatmaps

Heatmaps are off until you switch them on for the app in the portal (they have their own allowance). Taps and clicks are
collected per screen, folded on the server into a grid, and shown over a picture of the screen.

- What is collected: the screen name (the same as `pulse.screen_view`), where the tap was (a fraction of the width and
  logical points from the top), and its kind (`tap`, or `rage` for three or more taps within one second and 30 points).
  The position uses points (pixels divided by `Screen.dpi / 160`; where the dpi is unknown, pixels).
- What is never collected: element names or text, recordings, what was typed, the keyboard. Nothing is recorded while the
  on-screen keyboard is visible, on a `UnityEngine.UI.InputField` or TextMeshPro input field (or while one is selected),
  or on an object marked with the `PulseHeatmapIgnore` component (put it on an object or any parent to keep its taps out).
- Switch off for good with `PulseOptions.CaptureHeatmaps = false`. Taps are read from the legacy `Input` class or, when
  only the Input System is active, from its primary pointer. The SDK never consumes or delays an input event.
- Screenshots: only in a development build (`Debug.isDebugBuild`) with heatmaps on, one picture per screen, orientation
  and app version, taken about a second after the screen appears and sent once. Release builds never send pictures.

## App Control

App Control lets you require an update, suggest one, put the app into maintenance or show a short notice, all from the dashboard.
It works by itself after `Pulse.Initialize`: no extra code, no scene object, no assets. When a rule applies, the package draws a
screen on a canvas of its own (uGUI, created from code, highest sorting order, `DontDestroyOnLoad`) and removes it again when the
rule ends. Nothing exists while there is nothing to show.

- **Full screen, cannot be closed:** a required update, a blocked version, maintenance. The button opens the store page
  (Android: the Play page of the package when the dashboard has no link); maintenance has "Try again".
- **Dialog:** a suggested update ("Update" and "Not now"; a closed suggestion is not shown again for 24 hours).
- **Card:** an announcement, or what is new in the version the app was just updated to.
- **Strip:** a notice that maintenance starts soon, and in a development build the line "Test build: App Control preview"
  (the rules apply, the lock can be continued).

The screens follow the system light or dark theme where Unity can tell (Android and iOS; dark elsewhere), use the safe area,
and show the built-in texts in 12 languages (en, de, fr, es, it, pt-BR, ja, ko, zh-Hans, tr, ru, sr-Latn) chosen from the
device language (`Application.systemLanguage` first, because mobile players often report no culture, then the culture), English otherwise. Texts you set in the dashboard replace the built-in ones. The screen never changes
`Time.timeScale`, never pauses audio and never closes the app. The mouse cursor is shown while a full screen is up and put back
afterwards. If the scene has no `EventSystem`, one is created for as long as a full screen is up (the new Input System is used
when it is active, with no hard dependency on it).

**uGUI is optional.** The built-in screen is drawn with uGUI (`com.unity.ugui`), which this package does not require, so a game without
UI pays nothing for it (the engine's UI modules added about 6.7 MiB to an empty Android IL2CPP build when they were required). When the
project has no `com.unity.ugui` the package compiles without the screen: `ControlMode.Automatic` then behaves like `Manual`, nothing is
drawn, and `Pulse.Control.State` and `Pulse.Control.Changed` still work. A development build logs one line the first time a screen would
have been shown. Add `com.unity.ugui` to the project to get the built-in screens (the package detects it by itself, with no setting).

### Your own screens

```csharp
Pulse.Initialize("YOUR_APP_KEY", new PulseOptions { Control = ControlMode.Manual });
Pulse.Control.Changed += (sender, state) =>
{
    // Runs on a thread pool thread: hand over to the main thread before touching Unity objects.
    myUi.Show(state.Kind, state.Title, state.Body, state.ButtonText, state.AllowContinue);
};
// "Update": Pulse.Control.OpenStore();   "Not now": Pulse.Control.Dismiss();   "Try again": _ = Pulse.Control.CheckNowAsync();
```

`Pulse.Control.State` and `Pulse.Control.Changed` work in every mode. With `ControlMode.Off` the package makes no request for
App Control and the state is always `Normal`. Opting out of analytics does not switch App Control off, and the request carries
no app key, no install id and no user id.

To see the built-in screens without a server (any screen, language and theme), call
`PulseControlPreview.Show(ControlKind.UpdateRequired, "de", dark: false)` and `PulseControlPreview.Hide()`, or import the
"App Control demo" sample.

For the notes you send with a store submission: "The app checks on launch for required updates and maintenance windows."

### How it fails safely

1. Starting the SDK never waits for the network, and a check never runs on the main thread.
2. One request per check, 3 seconds to the response headers and 5 seconds in all, at most 64 KiB of answer, no retry loop and
   at most one automatic check in 60 seconds (when the app returns to the front).
3. Any failure (no network, a server error, an answer that cannot be read) keeps the last known state, or `Normal`. Nothing is
   ever locked because of something unknown.
4. A lock older than 30 days is ignored; a maintenance window from the saved answer ends at its end time and never lasts more than 72 hours.
5. There is always a way out: a required update turns into a suggestion when the store version is not newer than the installed one,
   after three returns from the store on the same version, or when there is no store link. The package never closes the app.

## Settings in the portal

The portal can switch screen views, the automatic signals and groups of device and app details off for an app, without
an update of your game. The SDK reads these settings at start, so a change applies from the next launch. Code options
can only turn things off: `false` in code wins, `true` in code never turns on what the portal switched off. The
platform, OS, app version and build, device model, locale, SDK and bundle id are always sent.

## Test mode

Every signal carries a test flag, so test traffic never mixes with real data.

- Default in Unity: a signal is marked as test when `Debug.isDebugBuild` is true, which means play mode in the Unity
  editor and any player built with Development Build ticked. A release player (Development Build not ticked) sends
  real data.
- Override: set `Debug` in `PulseOptions`. An explicit value always wins over the default, in both directions:
  `new PulseOptions { Debug = true }` marks everything as test, `Debug = false` sends real data even from the editor.
- One app key serves both. You do not need a second app or key for testing.
- In the dashboard, the Test mode switch chooses which of the two you see. Test signals are never counted in the
  normal view.

## Delivery and offline behavior

Each batch carries an id, so a batch sent again after a lost answer is stored only once.

- Signals are batched and sent in the background, every `FlushIntervalSeconds` or when a batch is full.
- While the device is offline, signals wait in a queue on disk. The queue survives restarts and is sent when the
  connection is back.
- When the game is paused or loses focus, the SDK sends what is queued. On quit it saves the queue (waiting at most two
  seconds) and does not wait for the network; the rest goes out at the next start.
- Failed sends are retried with exponential backoff (1 second up to 5 minutes), and a server `Retry-After` is
  respected. If the app key is rejected (HTTP 401), sending stops until the next start and the queue is kept. Signals
  the server rejects as invalid are dropped.
- Delivery is at least once: a batch can arrive twice, so treat duplicates as normal.
- A signal can still be lost if the process is killed before it is saved, when the queue limits are reached, or on
  opt-out.

`Flush` completes when the signals queued at the call have been sent. While offline it stays pending, and the
cancellation token ends only your wait. Requests are made on the Unity main thread, so never block that thread on
`Flush` with `.Wait()` or `.Result`; use `await` or ignore the task.

## Requirements

| Requirement | Value |
| --- | --- |
| Unity | 2022.3 LTS or newer |
| API compatibility level | .NET Standard 2.1 |
| WebGL | Not supported: the SDK needs threads and a file system |

The offline queue is stored under `Application.persistentDataPath`, in the folder `mightys-pulse`.

## Opt-out

```csharp
Pulse.OptOut = true;   // stops collection, clears the queue, forgets the user id and the install id
```

The choice is remembered across restarts. Set it back to `false` to resume: a new anonymous install id is made at the
next signal. Call `SetUser` again if you use it.

## Licence

MIT. See `LICENSE.md`.

## Reference

| Call | What it does |
| --- | --- |
| `Pulse.Initialize(appKey, options)` | Starts the SDK. Call once; the options are optional. |
| `Pulse.Track(type, value, props)` | Queues a signal. `value` and `props` are optional. |
| `Pulse.SetUser(id)` | Optional. Uses the SHA-256 hash of your `id` instead of the anonymous install id. Pass `null` to go back. |
| `Pulse.Flush(cancellationToken)` | Sends what is queued now and returns a task. |
| `Pulse.OptOut` | Gets or sets the saved opt-out choice. |
