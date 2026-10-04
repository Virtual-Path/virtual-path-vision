# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
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
