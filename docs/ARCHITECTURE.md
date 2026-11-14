# Ripple Architecture Overview

## 1. Threading & Rendering Model
- **UI / Compositor Thread:** `NotchWindow` subscribes to WPF `CompositionTarget.Rendering`, executing at the active monitor's native refresh rate (`60/120/144/240 Hz`, or throttled to `30 Hz` on battery saver).
- **Zero Layout Thrash:** `NotchShellControl` draws a single frozen `StreamGeometry` in `OnRender(DrawingContext)`, avoiding per-frame visual tree creation.
- **Adaptive Service Polling:** Background telemetry (`SystemMonitorService`, `BluetoothService`, `MediaService`) scales polling automatically (`1s` when expanded vs. `5s–15s` at rest) and suspends during `SessionSwitchReason.SessionLock`.

## 2. State Machine (`Ripple.Core.StateMachine.NotchStateMachine`)
States: `Rest`, `Peek`, `Expanded`, `Pinned`, `Activity`, `Dodging`, `HiddenFullscreen`.
- Transitions are governed by an injected `ITimeProvider` for deterministic unit testing.
- Pointer entry starts an `80 ms` intent timer in `Peek`; leaving starts a `250 ms` grace timer before collapsing.
- `HiddenFullscreen` and `Dodging` temporarily override idle states without clearing user preferences or active live activities.

## 3. Analytical Spring Physics (`Ripple.Core.Motion.SpringSolver`)
Instead of fixed-duration cubic-bezier keyframes, every dimension (`Width`, `Height`, `CornerRadius`, `OffsetY`, `PeekGlow`, `ContentReveal`, `BlurRadius`, `PressScale`) is solved by closed-form damped harmonic integration sub-stepped at `dt <= 1/240s`. Mid-flight target reversals preserve exact instantaneous velocity and position.

## 4. Safe Corridor Hit-Testing (`SafeCorridorHitTester`)
`WM_NCHITTEST` translates physical screen cursor coordinates via `CompositionTarget.TransformFromDevice` (supporting mixed-DPI multi-monitor setups `100%–250%`). When open, interactive hit-testing covers the union of:
1. Rest pill rectangle
2. Instantaneous animated shell rectangle
3. Target expanded bounds + halo padding
4. Convex trapezoidal corridor bridging the entry coordinate on the top pill to the expanded corners

## 5. Security & Privacy Boundaries
- **No Telemetry:** Zero outbound network telemetry; LRCLIB lyrics and Open-Meteo weather are invoked solely on explicit feature activity.
- **Loopback API Authentication:** `LocalApiServer` binds strictly to `http://127.0.0.1:<port>/` and validates `X-Ripple-Token` or `Authorization: Bearer <token>` on all state-mutating routes.
- **Clipboard Encryption at Rest:** Clipboard entries are sealed with `AesGcm` (256-bit key stored DPAPI-protected for `DataProtectionScope.CurrentUser`), while password managers (`1Password`, `Bitwarden`, `KeePass`, `LastPass`, `Dashlane`) are excluded automatically.
