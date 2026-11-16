# MightyS Pulse for Unity

Privacy-first analytics for Unity games and apps. No device IDs, offline queue.

Docs: https://pulse.mightys.dev/docs/unity/

MightyS Pulse lets a Unity game or app send named signals (events) with an optional number and a few properties, and
see them in a dashboard. The package has no external dependencies and needs no scene component: you call it from your
own startup code.

## Install

In Unity, open Window > Package Manager > + > Add package from git URL and enter:

```text
https://github.com/MightyS-dev/pulse-unity.git#0.9.5
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
automatic gameplay events.

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

Name screens yourself (menus, panels inside one scene) with `Pulse.Screen("Settings")`; it works whatever the code option, while a portal switch still applies.

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
