# LockTune

LockTune is a macOS menu bar app (Swift + SwiftUI, macOS 14+) that shows an iPhone-style "lock screen" with a now-playing player over every display. It has a large clock and date, the album art, a blurred and slowly moving artwork background, glass playback controls, and Apple Music-style synced lyrics.

It does not include a notch, Dynamic Island or menu-bar pill widget. The app is only the full-screen overlay plus a small menu bar icon.

---

## Install (ready-made app)

1. Open the repo's **Releases** page and download **LockTune.dmg** from the release named *latest*. GitHub Actions builds it on a Mac on every push to `main`.
2. Open the .dmg and drag **LockTune** onto **Applications**.
3. On first launch, macOS blocks it because the app isn't notarized (that needs a paid Apple Developer account). Fix it once:
   - **macOS 15+:** try opening the app, then go to System Settings → Privacy & Security, scroll down and click **Open Anyway**.
   - **macOS 14:** right-click the app → **Open** → **Open**.
4. A ♪ icon appears in the menu bar. Play music and press **⌃⌘L**.

## Building from source

1. Open `LockTune.xcodeproj` in Xcode 15 or later.
2. Select the **LockTune** target, open **Signing & Capabilities** and choose your **Team**.
   - The project uses "Sign to Run Locally" by default. That works, but macOS may ask for the Automation permission again after each rebuild, because the ad-hoc signature changes every time. A real team signature stops that.
3. Run it with ⌘R. A ♪ icon appears in the menu bar. The app has no Dock icon (`LSUIElement`).
4. Start playing something in **Music** or **Spotify** and press **⌃⌘L**.

To use launch at login, copy the built app to `/Applications` first, then turn on **Settings → General → Launch at login**. macOS may ask you to approve it under System Settings → General → Login Items.

### Permissions

| Permission | Why | When it's asked |
|---|---|---|
| **Automation → Music / Spotify** | Read the track, position and artwork, and send play/pause/next/previous/seek | The first time LockTune talks to each player. You can also use **Settings → General → Request Access**. If you click "Don't Allow", re-enable it in System Settings → Privacy & Security → Automation. |
| Accessibility | **Not needed.** The global shortcut uses Carbon `RegisterEventHotKey`, which works without it. | – |
| Network | LRCLIB lyrics and the iTunes Search artwork fallback | No prompt (the app isn't sandboxed) |

### Security

**The overlay is visual only. It does not lock your Mac.** Anyone can close it with Esc or a swipe. For real protection, turn on **System Settings → Lock Screen → "Require password after screen saver begins or display is turned off" → Immediately**, and lock with ⌃⌘Q.

---

## Using it

1. Play a song in **Spotify** or **Music**.
2. Lock the Mac: **⌃⌘Q** (or LockTune's shortcut **⌃⌘L**, which turns the display off. That locks the Mac when "Require password" is set to *Immediately*).
3. The widget appears: album art, a glass player card (play/pause, previous/next, seek bar) and a **toolbar on the left**:
   - 💬 **Lyrics**: art and controls move left, synced lyrics appear on the right. Clicking the album art does the same.
   - 🖼 **Artwork wallpaper**: fills the screen with the album art and a clock, like in the reference video. Off by default, so your normal lock screen stays visible.
   - 👁 **Hide**: hides the widget, leaving only the toolbar.
4. **Menu bar ♪ → Preview Widget** shows the widget on the desktop, so you can see how it looks without locking (Esc closes it).
5. **Status & Settings…** (also opens on first launch, or when you open LockTune again from Applications):
   - checks that a song is detected and that the Automation permission is granted;
   - lets you change the shortcut, idle-lock, all displays, wallpaper style, clock font and lyrics.

In the widget (preview, or after unlocking): **Space** play/pause · **← / →** previous/next · **L** lyrics · two-finger scroll to browse lyrics · **Esc** close.

### Showing the widget on the lock screen itself

macOS has **no public API** for drawing over the lock screen. Apps that do it (lock-screen widget apps, and most likely the app in the reference video) move their window into a private *SkyLight* window-server space that sits above the lock screen.

At its owner's request, LockTune does the same in `Overlay/SkyLightLockScreenLayer.swift`. It plugs into the `LockScreenLayer` protocol in `Overlay/LockWidgetController.swift`.

- The widget only *draws* above the lock screen. Its panels are non-activating and never become key while locked, so the keyboard stays with the system password field and Touch ID works as usual.
- It's removed the moment you unlock.
- The functions are looked up at runtime. If a macOS update removes them, the Status window says so and the widget appears right after unlocking instead.
- *Lock Screen Now* / ⌃⌘L call the same system function as Apple menu → Lock Screen.

### Diagnostics and self-test

- **Status → Diagnostics** shows, for Spotify and Music: whether it's running, the playback state, the track ID, and the exact AppleScript error if something fails. **Copy Diagnostics** puts all of it on the clipboard.
- CI runs `LockTune.app/Contents/MacOS/LockTune --self-test` on a real Mac before publishing a release. It checks:
  - every AppleScript compiles against the Music and Spotify dictionaries;
  - the LRC parser;
  - the lock-screen functions exist;
  - a panel can be attached to the lock-screen layer.

## Architecture

```
LockTune/
├─ App/
│  ├─ LockTuneApp.swift          @main, MenuBarExtra (Show / Settings / Quit), Settings scene
│  ├─ AppCoordinator.swift       wires services and triggers together
│  └─ AppSettings.swift          UserDefaults-backed preferences
├─ NowPlaying/
│  ├─ NowPlayingProvider.swift   protocol + TrackInfo / PlayerSnapshot / ArtworkSource
│  ├─ AppleScriptPlayerProvider.swift   shared AppleScript base, AppleMusicProvider, SpotifyProvider
│  ├─ AppleScriptRunner.swift    compiled-script cache, error codes (-1743 = Automation denied)
│  ├─ NowPlayingController.swift polling (4 Hz overlay / 0.5 Hz idle), interpolation, commands
│  ├─ ArtworkFetcher.swift       URL download + iTunes Search fallback
│  └─ ArtworkPalette.swift       dominant colours, luminance, pre-blurred background image
├─ Lyrics/
│  ├─ LyricsService.swift        LRCLIB /api/get → /api/search fallback, disk cache (actor)
│  ├─ LRCParser.swift            .lrc parsing (multi-timestamp, [offset:]), active-line search
│  ├─ LyricsController.swift     loads lyrics whenever the track changes
│  └─ LyricsModels.swift
├─ Overlay/
│  ├─ OverlayWindowController.swift   OverlayWindow (borderless, .screenSaver level) on every NSScreen, key handling
│  └─ OverlayState.swift              mode (player/lyrics), swipe-to-dismiss, lyrics scrolling
├─ Triggers/  HotkeyManager (Carbon), IdleMonitor (CGEventSource idle time), LockMonitor
├─ Views/     LockScreenView, ClockView, ArtworkView, ArtworkBackgroundView,
│             PlayerControlsView, ProgressBarView, LyricsView
├─ Settings/  SettingsView, HotkeyRecorderView, LaunchAtLogin (SMAppService)
└─ Support/   Info.plist (LSUIElement, NSAppleEventsUsageDescription), entitlements
```

To add a new source (for example a browser or another player), implement `NowPlayingProvider` and add it to the `providers` array in `NowPlayingController.init`.

### How the main effects work

- **Smooth position:** the player is polled every 250 ms while the overlay is up. Between polls, the position is `basePosition + (now − baseDate)`. It only resyncs on a state change or when it drifts by more than 0.35 s, so lyrics never jump backwards because of AppleScript latency.
- **Background:**
  - The artwork is reduced to 128 px, Gaussian-blurred and saturated once per track with Core Image.
  - Three copies of that small image rotate and drift at different speeds inside a `drawingGroup()`, which costs little GPU.
  - On top sits a gradient made from the extracted dominant colours, plus a scrim and a vignette.
- **Text colour:** it's based on the average luminance of the blurred artwork. Bright covers get dark text; dark covers get white text tinted slightly with the dominant colour, like the bluish-white clock in the reference video.
- **Album art:** springs to 84 % when paused and 100 % when playing (Apple Music behaviour). The shadow deepens while playing.
- **Player ↔ lyrics:**
  - `matchedGeometryEffect` moves the artwork and the glass card between layouts with a spring animation.
  - The lyrics are laid out by hand, so each line springs to its new place with a small delay that grows with its distance below the current line. That gives the Apple Music "wave".
  - Lines blur and fade with distance from the current line; blur is turned off while you scroll manually.

---

## Honest list of limitations

1. **Lock screen:** see *Showing the widget on the lock screen itself* above. With no public API, this build shows the widget right after you unlock.
2. **Only Apple Music and Spotify are supported**, through AppleScript. Browsers, YouTube, podcasts apps and others aren't, because there's no public system-wide "now playing" API. The private MediaRemote framework is **not used**: it's locked down since macOS 15.4. The `NowPlayingProvider` protocol leaves room for more sources.
3. **Apple Music artwork:** for songs streamed from Apple Music that aren't in your library, AppleScript often returns no artwork. In that case the cover is looked up via the public iTunes Search API, which can occasionally pick the wrong edition of an album.
4. **Lyrics:**
   - Coverage depends on LRCLIB.
   - Sync is line by line, not word by word like Apple Music's karaoke mode.
   - Timing depends on the LRC file. Lines are highlighted 0.25 s early, as Apple Music does.
   - Found and not-found answers are cached in `~/Library/Caches/com.example.LockTune/Lyrics`. Not-found answers are retried after 3 days.
5. **Swipe up** is a *two-finger* scroll gesture on the trackpad. Three- and four-finger swipes belong to Mission Control and never reach apps.
6. **Global shortcut:**
   - It must include ⌘ or ⌃. Since macOS 15, shortcuts using only ⌥/⇧ are rejected.
   - It doesn't fire while macOS "secure input" is on, for example while a password field is focused.
7. **Not sandboxed**, so it can't go on the Mac App Store as-is. Scripting other apps from the sandbox needs temporary-exception entitlements.
8. Releases are **ad-hoc signed, not notarized**, so Gatekeeper asks you to confirm the first launch (see Install above).
