# Verification checklist

These are acceptance checks, not completed results. Record Passed, Failed, or Untested plus Windows build, DPI, device/driver, and relevant evidence.

## Window and island

- Main window starts visible; only one app instance/session controls playback.
- Minimize removes the running taskbar button and shows the island on the correct monitor.
- A manually pinned taskbar shortcut is not modified.
- Initial idle hide, edge-hover reveal, and exit grace match the design defaults.
- A hidden island is still discoverable by hover at its small edge trigger.
- Areas outside the trigger remain clickable; no full-width/fullscreen invisible overlay.
- Hover does not steal keyboard focus; intentional interaction remains accessible.
- Seek drag, menus, and focused controls prevent premature hiding.
- Rapid pointer re-entry and repeated minimize/restore do not strand or duplicate windows.
- Open app and Tray Open restore the same main window.
- Close and Tray Exit stop audio, remove all windows/tray icons, and release the DAC.
- Unplugging a monitor and changing DPI/resolution leave the app recoverable.
- 100%, 150%, and 200% DPI do not create position drift or clipped controls.
- Full-screen suppression behaves as documented; island can be disabled if needed.

## Visual inspection

- Main, compact, expanded, empty, loading, disabled, error, and settings states are coherent.
- Bright wallpaper behind glass does not destroy text contrast.
- Blur affects the backdrop, not cover/text/icons.
- System transparency-off, high contrast, and reduced motion are respected.
- Long filenames and CJK tags do not overlap transport controls.
- Keyboard navigation, accessible names, and focus indicators work.
- Album art is the main color source; no neon, gradient wallpaper, fake badges, or dashboard clutter.

## Playback and audio claims

- Test valid FLAC files at 16/44.1, 24/48, and another supported rate, recording actual hardware support.
- Verify source metadata separately from accepted output format.
- Test pause/resume, seek, next/previous, queue end, corrupt/missing file, and Unicode paths.
- Playback position/session persists through all window transitions.
- Exclusive mode handles busy/unsupported devices explicitly.
- Switching between 44.1 and 48 kHz actually reconfigures output where supported.
- Transparent mode has unity software gain and bypasses all sample-changing DSP.
- Software PCM equality checks compare normalized integer sample values through any lossless packing.
- Same-format gapless checks verify boundary sample count/order, not just listening.
- Cross-format limitations are stated rather than concealed.
- DAC disconnect/reconnect is recoverable.
- ASIO is tested with an actual driver or clearly left unavailable/untested.
- “Bit-perfect verified” is never inferred from exclusive mode, FLAC metadata, or a successful build.

## Packaging

- The portable folder launches without a developer environment.
- Required FLAC/native output libraries are included and architecture-compatible.
- Missing native dependencies produce a useful error.
- Settings/index/cache are written under LocalAppData, not the install directory.
- Music files remain unchanged.
- Dependency notices, run instructions, and known limitations are present.
