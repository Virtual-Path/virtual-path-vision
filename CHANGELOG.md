# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
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
- Sidebar logo re-branded from “MV / Vision” to **VP / Virtual Path Vision**.
- Removed the unused `ComboBoxStyle` resource from `App.xaml` (only `GlassComboBox` is used).
- Regenerated the dark/light main-UI screenshots with a DPI-aware capturer (previous captures were
  clipped on high-DPI displays).

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
