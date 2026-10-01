# Contributing to Machine Vision App

Thanks for your interest in improving **Machine Vision App**! This document describes how to
report issues, propose features, and submit pull requests.

> 感谢你参与 **Machine Vision App** 的开发！本文说明如何反馈问题、提出功能建议与提交代码。

---

## Code of Conduct

By participating you agree to abide by our [Code of Conduct](CODE_OF_CONDUCT.md).
参与本项目即表示你同意遵守[行为准则](CODE_OF_CONDUCT.md)。

---

## Ways to Contribute

- **Bug reports** – open an [issue](https://github.com/xianshi3/machine-vision-app/issues/new/choose)
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
git clone https://github.com/xianshi3/machine-vision-app.git
cd machine-vision-app
dotnet restore
dotnet build
dotnet run --project MachineVisionApp/MachineVisionApp.csproj
```

Or open `MachineVisionApp.sln` in Visual Studio 2022 and press **F5**.

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

---

## Pull Request Checklist

- [ ] The solution builds with **0 errors** (`dotnet build`).
- [ ] No new warnings were introduced.
- [ ] UI changes work in **both** light and dark themes.
- [ ] New UI strings are localized (zh-CN + en-US).
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
