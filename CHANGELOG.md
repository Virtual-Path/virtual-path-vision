# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/xianshi3/machine-vision-app/compare/v3.1.0...HEAD
[3.1.0]: https://github.com/xianshi3/machine-vision-app/compare/v2.3.0...v3.1.0
[2.3.0]: https://github.com/xianshi3/machine-vision-app/releases/tag/v2.3.0
