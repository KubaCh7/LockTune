# LockTune

LockTune is a macOS menu bar app (Swift + SwiftUI, macOS 14+) that shows an iPhone-style "lock screen" with a now-playing player over every display. It has a large clock and date, the album art, a blurred and slowly moving artwork background, glass playback controls, and Apple Music-style synced lyrics.

It does not include a notch, Dynamic Island or menu-bar pill widget. The app is only the full-screen overlay plus a small menu bar icon.

---

## Setup

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

| Action | How |
|---|---|
| Show / hide | **⌃⌘L** (change it in Settings), or menu bar → *Show Lock Screen Now* |
| Play / pause | **Space**, or the ▶︎/❚❚ button |
| Previous / next | **← / →**, or the buttons. Previous restarts the song if it's more than 3 s in, like on iPhone. |
| Seek | Drag the progress bar, or click a lyric line |
| Lyrics | Click the album art, the 💬 button, or press **L**. Click again to go back. |
| Scroll lyrics | Two-finger scroll. It snaps back to the current line after 3 s. |
| Close | **Esc**, **↑**, any other key (can be turned off), a two-finger swipe up on the trackpad, or dragging the screen up with the mouse |

### Settings

- **General:** launch at login, the global shortcut (recorder + on/off), which music source to use (Automatic / Apple Music / Spotify), and Automation status.
- **Triggers:**
  - Show after N minutes idle while music is playing.
  - Show when the screen locks, and/or when the display sleeps.
  - What to do when nothing is playing: show the clock only, or don't show the overlay.
  - Whether any other key closes the overlay.
  - Cover all displays, or only the one under the pointer.
- **Appearance:**
  - Clock font (New York serif / SF Pro Rounded / SF Pro) and weight, with a live preview.
  - Background: *Blurred & animated* (Apple Music style) or *Artwork wallpaper* (as in the reference video).
  - Lyrics layout: *Side by side* (as in the video) or *Compact header* (small art next to the title at the top left, lyrics fill the screen).
  - Lyrics on/off and clearing the lyrics cache.

---

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

1. **It can't appear on the real lock screen.** macOS doesn't let third-party apps draw on the login/lock window. LockTune uses a borderless window at `NSWindow.Level.screenSaver` on every screen, which covers the menu bar and Dock. While it's up it also hides the Dock and menu bar and blocks ⌘⇥.
   - **"Show when the screen locks" / "when the display sleeps"** put the overlay up *behind* the system lock screen. You see it right after you unlock or wake the display, not on the lock screen itself.
   - Force Quit (⌥⌘⎋) still works on purpose, so you can't get trapped.
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
8. **Not compiled yet.** The project was written in a Linux environment without Xcode. The Swift files pass a syntax check and the `.xcodeproj` parses correctly, but nothing has been compiled or run on a Mac. Expect possibly a few small compiler fixes on the first build, and please report them.
