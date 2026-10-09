# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Production-line card** (`Industrial` panel): pass/fail counters, yield figure and bar, plus
  controls for the orchestration layer — enable switch, stable-frame count, inspection ROI,
  MES gateway URL and JWT. The card is **off by default**: it counts and reports to MES, both of
  which have external side effects.
- **Calibratable inspection ROI.** X/Y/W/H moved from a hardcoded pixel rect into the card.
  The default `(360,180,560,280)` matches a 1280×720 frame; invalid input falls back to the
  default rather than erroring, because a typo puts real workpieces outside the zone and missed
  detections are silent.
- **MES client aligned with the real gateway.** The previous implementation was written against a
  guess. It is now matched to `virtual-path-mes`: `Authorization: Bearer <JWT>` is mandatory
  (the gateway's `JwtAuthGlobalFilter` whitelist excludes `/api/quality/**`), the body uses the
  DTO's camelCase field names, `checkType` is required and pattern-checked, strings are clipped to
  the DTO's `@Size` limits, and pass/reject address the record by the id returned from
  `POST /record` rather than by posting the payload again.
- **Stream path field** on the camera panel. `BuildNetworkUrl()` hardcoded `/video` while
  VirtualPath-Core serves `/cam1`, so every connection 404'd, OpenCV reopened, and the engine
  logged `client disconnected` in an endless loop. The path is now an input (default `/cam1`) and a
  missing leading slash is added — `cam1` and `/cam1` mean the same thing.
- **Regression tests for shutdown** (`VirtualPathVision.Tests`): a deterministic check that
  `StopCapture` does not re-acquire the component lock after its wait, plus a Dispose-idempotency
  check. 83 assertions total.

### Fixed
- **Closing the app with a camera open could hang forever.** `VideoCaptureComponent.StopCapture()`
  re-acquired `_lock` after waiting for the capture loop, while the loop's `finally` held `_lock`
  across a `Dispatcher.Invoke` — each waiting on the other, and the dispatcher can only be pumped
  by the very UI thread that was blocked. Whichever side won the lock decided whether closing
  worked, which is why it was intermittent. `StopCapture` now takes everything it needs before the
  wait and never touches the lock afterwards; native handles are no longer released under a live
  capture thread (that was undefined behaviour, and the worst case on MSMF); `IsStopping` is set
  before the wait so frame callbacks stop reaching for the UI thread; and `ProcessFrame`'s
  blocking `Dispatcher.Invoke` calls became non-blocking dispatches. Measured end to end
  (real `MainWindow`, MJPEG source): 0 hangs in 8 runs, previously 1–3 in 5.
- **`GlassTextBox` applied to a `PasswordBox` crashed the app on startup.** WPF styles do not
  cross `TargetType`, so `MainWindow.InitializeComponent` threw and the application never opened.
  XAML compilation does not catch this. Both themes now carry a `GlassPasswordBox`.
- **One-shot events could be dropped by the per-frame dispatcher de-duplication.** "Capture
  stopped" is a state transition, not a repeating update: losing it leaves the panel stale (image
  not cleared, buttons still disabled). UI dispatch is now split — `InvokeUi` de-duplicates for
  per-frame refreshes, `PostUi` does not, and the capture-stopped/capture-error handlers use the
  latter.
- **Capture-stopped and capture-error handlers no longer block the capture thread during
  shutdown**, and no longer touch UI elements while the window is being torn down. Error text is
  still logged, just not shown.
- The engine's `MjpegServer` now logs the rejected path on a 404
  (`rejected /video (only /cam1 is served)`) instead of an uninformative `client disconnected`
  for every connection, and no longer prints that message for connections that never became
  clients. Its `finally` block also no longer clears `_stream`/`HasClient` unconditionally, which
  could wipe a newer client's state.
- **The dual-view toggle is now translated.** Its label was hardcoded Chinese (`双视图`), so it stayed
  in Chinese on English builds. The tooltip was hardcoded bilingual (`第二视图 / Dual View`) and is now
  localized too (`DualView`, `DualViewHint`).
- The camera connection badge kept its English XAML default (`Disconnected`) after a language switch,
  because only runtime state changes ever wrote to it. `CameraPanel` now tracks its `ConnectionState`
  and re-renders the badge in `RefreshTexts`.
- The sidebar no longer jumps on startup: its width was applied in `Loaded`, so the first frame was
  rendered at the XAML width and then resized.
- **Dropdowns now show the selected value while closed.** The custom `GlassComboBox` template was
  missing the `SelectionBoxItem` binding that WPF's default template uses, so every combo box
  (signal source, processing mode, colour, serial port, baud rate, scan source, language …)
  rendered blank until you clicked it open.
- The Chinese `AppTitle` resource still returned the old product name “机器视觉应用”; it now
  returns **Virtual Path Vision** (matching the English resource).
- YOLO model loading no longer risks a null dereference when OpenCV fails to produce a network.
- Build now compiles with **0 warnings** (was 12): fixed 2 nullable warnings in `YoloDetectionComponent`
  and documented the OPC UA obsolete-API usage with a scoped `#pragma warning disable CS0618`.

### Changed
- **Build output no longer ships foreign-platform native libraries.** Without a `RuntimeIdentifier`
  the SDK copies every platform's native assets out of each dependency, so Linux/macOS/win-arm
  `.so`/`.dylib`/`.a` files landed in the output of a Windows-only app — 18 MB per configuration.
  The project now targets `win-x64`, cutting each configuration from 154 MB to 132 MB. The output
  path is unchanged (`bin\<Cfg>\net8.0-windows\`, not `…\win-x64\`) and the win-x64 natives are
  still shipped, now placed directly in the app root instead of under `runtimes\win-x64\native\`.
- Removed the unreferenced `SidebarIcon` and `NavButton` styles from both themes (154 lines total).
  Both were marked *“legacy, kept for compatibility”* but nothing referenced them via `StaticResource`,
  `DynamicResource` or code.
- **Sidebar redesigned.** The six nav items are split into *Workspace* (Camera, Image Processing,
  AI Detection) and *System & Integrations* (Cloud, Industrial, Log) groups with captions and a
  divider; settings and the collapse toggle became full-width rows matching the nav items instead of
  two floating icons. Nav labels use short, localized names (`NavCamera`, `NavImage`, …) rather than
  the panel titles, which overflowed the sidebar, and every row now carries a localized tooltip — the
  only label visible when the sidebar is collapsed. Selected/hover states animate (the pill indicator
  scales in, the hover wash fades), the logo aligns with the icon column, and the sidebar widened
  from 200 px to 216 px to fit them.
- Sidebar logo re-branded from “MV / Vision” to **VP / Virtual Path Vision**.
- Removed the unused `ComboBoxStyle` resource from `App.xaml` (only `GlassComboBox` is used).
- Regenerated the dark/light main-UI screenshots with a DPI-aware capturer (previous captures were
  clipped on high-DPI displays).
- **Regenerated `TestImages/test_scene.png`.** The scene now carries the new title
  “Virtual Path Vision Test Scene”, a QR code pointing at the new repository
  (`https://github.com/virtual-path/virtual-path-vision` — the old one resolved to the pre-fork
  project), and a Code 128 barcode encoding `OPENCV5` with proper quiet zones (the previous bars
  ran into the image edge and could not be decoded). The stray half-barcode fragment on the right
  edge was removed, and `TestImages/template_qr.png` was re-cropped to match.
- Refreshed the mode screenshots `docs/images/02`–`11` against the regenerated test scene; the
  capturer now renders through `PrintWindow`, so results no longer depend on window focus.

## [3.1.1] - 2026-10-01

### Fixed
- **Startup no longer crashes when the bundled face-detection model is missing.**
  If `face_detection_yunet_2023mar.onnx` is absent or fails to load, the app now starts
  normally with face detection disabled, instead of throwing during `MainWindow` construction.
- Unhandled exceptions now show a readable error dialog instead of failing silently.

## [3.1.0] - 2026-10-01

### Added
- **AI active perception** – YOLO (YuNet/YOLO) detection, Kalman multi-object tracking,
  digital-twin overlay and per-class detection breakdown (`AI/`).
- **AWS cloud integration** – S3 upload, IoT Core publish, Lambda invoke (`Cloud/`).
- **Industrial connectivity** – Modbus TCP, OPC UA, serial and TCP barcode scanners,
  work-report export (`Industrial/`).
- **Light / dark themes** with runtime switching and a *Follow system* option
  (`ThemeService`, `Themes/LightTheme.xaml`, `Themes/DarkTheme.xaml`).
- **Collapsible sidebar** with persisted state, plus a second (dual) camera preview toggle.
- **Responsive layout** – multi-column panels stack automatically on narrow windows.
- Open-source project scaffolding: `LICENSE`, `CONTRIBUTING`, `CODE_OF_CONDUCT`,
  `SECURITY`, `CHANGELOG`, `.editorconfig`, GitHub issue/PR templates and CI.

### Changed
- **Full UI redesign** – Apple-style card layout, unified spacing, accent-coloured
  primary actions, refined sliders and toggles across every page.
- Home preview area now fills the available space and scales the camera image to fit
  (no more oversized preview on high-resolution cameras).
- Window uses `PerMonitorV2` DPI awareness for crisp rendering on 2K/4K displays.
- Upgraded to **OpenCvSharp5 (OpenCV 5)** and **AWS SDK v4**.

### Fixed
- Settings window no longer crashes when opened (wrong control style for the theme radios).
- Switching language no longer wipes the saved theme preference.
- The saved theme is correctly restored on startup instead of falling back to dark.
- Removed stray 1-px highlight lines inside buttons and inputs.

## [2.3.0] - 2025-02-02

### Added
- 11 processing modes: Canny, Sobel, Laplacian, Binary, Contour, QR/Barcode,
  Color Detection, Template Match, Shape Detection, Feature Match, Enhancement.
- Real-time i18n (Chinese / English) and GitHub-dark UI.
- Video recording, screenshots and network camera (IP Webcam) support.

[Unreleased]: https://github.com/virtual-path/virtual-path-vision/compare/v3.1.1...HEAD
[3.1.1]: https://github.com/virtual-path/virtual-path-vision/compare/v3.1.0...v3.1.1
[3.1.0]: https://github.com/virtual-path/virtual-path-vision/compare/v2.3.0...v3.1.0
[2.3.0]: https://github.com/virtual-path/virtual-path-vision/releases/tag/v2.3.0
