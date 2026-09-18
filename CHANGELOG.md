# Changelog

## 0.2.0

- New defaults: rainbow glow, no hold after release, no focus mask. Existing preferences are preserved.
- Optional settings window with an animated preview, adjustable width, glow, color cycle, fade duration and mask depth.
- Optional current-user Windows login startup, disabled by default; paths containing spaces are quoted.
- Right-drag, Alt+right-drag, back/forward side-button triggers and process exclusions.
- Esc clears active highlights without replaying the cancelled gesture.
- Independent focus-hole fades and a choice of all displays or the display containing the most recent frame.
- Cached premultiplied drawing surfaces, bounded frame history, display-change and resume recovery.
- Configuration migration, input-state, rendering, startup-entry and settings-window regression checks.

## 0.1.0

First public release.

- Right-drag rectangle annotation with retained ordinary right clicks.
- Seven animated styles and five solid-color presets.
- Optional focus dimming, configurable hold time, and 0.5-second disappearance.
- Persistent settings, tray controls, and a global pause shortcut.
- Dedicated mouse-hook thread and listener repair.
- Windows build script, automated checks, and portable release package.
