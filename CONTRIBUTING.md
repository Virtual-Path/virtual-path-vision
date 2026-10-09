# Contributing to Virtual Path Vision

Thanks for your interest in improving **Virtual Path Vision**! This document describes how to
report issues, propose features, and submit pull requests.

> 感谢你参与 **Virtual Path Vision** 的开发！本文说明如何反馈问题、提出功能建议与提交代码。

---

## Code of Conduct

By participating you agree to abide by our [Code of Conduct](CODE_OF_CONDUCT.md).
参与本项目即表示你同意遵守[行为准则](CODE_OF_CONDUCT.md)。

---

## Ways to Contribute

- **Bug reports** – open an [issue](https://github.com/virtual-path/virtual-path-vision/issues/new/choose)
  using the *Bug report* template.
- **Feature requests** – open an issue using the *Feature request* template.
- **Pull requests** – fix a bug, add a processing mode, improve docs or translations.
- **Translations** – add a new `.resx` file (see [Internationalization](README.md#internationalization)).

---

## Development Setup

| Requirement | Version |
|---|---|
| OS | Windows 10 / 11 (x64) |
| .NET SDK | 8.0 |
| IDE | Visual Studio 2022 / Rider / VS Code |

```bash
git clone https://github.com/virtual-path/virtual-path-vision.git
cd virtual-path-vision
dotnet restore
dotnet build
dotnet run --project VirtualPathVision/VirtualPathVision.csproj
```

Or open `VirtualPathVision.sln` in Visual Studio 2022 and press **F5**.

### Running the tests

There is a headless regression suite (no UI process required):

```bash
dotnet run --project VirtualPathVision.Tests/VirtualPathVision.Tests.csproj
```

It prints one line per assertion and exits non-zero on failure. Coverage is described in
[Known Limitations](README.md#known-limitations) and in the source files themselves.

> **Building is not enough.** XAML compiles happily while being wrong at runtime — a
> `Style` whose `TargetType` does not match the element is not a compile error, and the
> failure only appears when the window loads. **Launch the app after touching XAML.**

---

---

## Project Conventions

- **Language** – C# with `<Nullable>enable</Nullable>`; keep the existing file-scoped namespace style.
- **UI** – WPF/XAML. Colours must reference theme brushes (`{DynamicResource ...}`), **never hard-coded hex**,
  so both the light and dark themes keep working.
- **ViewModels / text** – all user-visible strings must go through `TranslationService` and the `.resx` files
  (add both `Strings.resx` and `Strings.en.resx` entries).
- **Formatting** – follow [`.editorconfig`](.editorconfig) (4-space indent, CRLF).
- **Native code** – the `AI/` and `Components/` OpenCV `Scalar`/HSV values are functional (detection),
  not UI colours — do not remap them.
- **XAML styles do not cross `TargetType`.** `GlassTextBox` (`TargetType="TextBox"`) applied to a
  `<PasswordBox>` compiles without error and throws at load time, taking the whole window with it.
  When a control needs a restyled look, add a style for its own type to **both** themes.
  Note also that a `ControlTemplate` trigger must name a property of that control type —
  `<Trigger Property="Text" ...>` does not transfer to `PasswordBox`, which uses `Password`.
- **Threading on the capture path.** Capture runs on a background thread, so **UI updates there must
  be non-blocking**. Use `InvokeUi` for per-frame refreshes (it de-duplicates, keeping the newest
  frame) and `PostUi` for one-shot events (dropping one is unrecoverable — a dropped
  "capture stopped" leaves the panel stale). A blocking `Dispatcher.Invoke` makes the capture
  thread wait for the UI thread; while closing, the UI thread waits for the capture loop. That
  mutual wait hung the app permanently until `VideoCaptureComponent.StopCapture` was changed to
  take everything it needs *before* waiting and to never touch `_lock` afterwards.
- **Do not release native handles while the loop may still be using them.** `capture.Release()`
  and `Mat.Dispose()` must happen after the capture loop has exited, not after a fixed timeout.

---

---

## Pull Request Checklist

- [ ] The solution builds with **0 errors** (`dotnet build`).
- [ ] No new warnings were introduced.
- [ ] Tests pass: `dotnet run --project VirtualPathVision.Tests/VirtualPathVision.Tests.csproj`.
- [ ] **The app launches** (`dotnet run --project VirtualPathVision/VirtualPathVision.csproj`) —
  XAML mistakes are not compile errors.
- [ ] UI changes work in **both** light and dark themes.
- [ ] New UI strings are localized (zh-CN + en-US).
- [ ] Any new assertion was checked by **injecting the bug it is meant to catch** and confirming
  it goes red — an assertion that passes either way proves nothing.
- [ ] The PR description explains *what* changed and *why*; link related issues.
- [ ] Screenshots are attached for visible UI changes.

---

## Commit Messages

Keep them short and imperative. A light Conventional-Commits style is welcome:

```
feat: add CLAHE adaptive enhancement mode
fix: prevent preview from overflowing on high-resolution cameras
docs: refresh README project structure
ui: unify card layout across panels
```

---

## License

By contributing you agree that your contributions are licensed under the
[GNU General Public License v3.0](LICENSE).
