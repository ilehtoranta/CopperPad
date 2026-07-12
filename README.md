# CopperPad

[![CI](https://github.com/ilehtoranta/CopperPad/actions/workflows/ci.yml/badge.svg)](https://github.com/ilehtoranta/CopperPad/actions/workflows/ci.yml)
[![iOS](https://github.com/ilehtoranta/CopperPad/actions/workflows/ios.yml/badge.svg)](https://github.com/ilehtoranta/CopperPad/actions/workflows/ios.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

CopperPad is a cross-platform .NET game-controller input library with normalized controller state, profile persistence, SDL mappings, desktop HID support, and Apple GameController support. The repository also includes an Avalonia controller tester, mapper, and calibration application.

## Projects

- `src/CopperPad` — platform-neutral controller abstractions and profile JSON support.
- `src/CopperPad.HidSharp` — desktop HidSharp provider, SDL_GameControllerDB data, diagnostics, and fallback mappers.
- `src/CopperPad.GameController` — Apple GameController provider and platform-neutral Apple mapping.
- `src/CopperPad.Gui` — Avalonia controller test, mapping, and calibration application.
- `tests/CopperPad.Tests` — core, mapping, provider, and lifecycle tests.
- `tests/CopperPad.Gui.Tests` — GUI profile and editor logic tests.

## Build and test

The repository requires the .NET 10 SDK.

```sh
dotnet restore CopperPad.slnx
dotnet build CopperPad.slnx --configuration Release --no-restore
dotnet test CopperPad.slnx --configuration Release --no-build --no-restore
```

The native iOS target requires macOS, Xcode, and the matching .NET iOS workload. CI builds it separately with `EnableIOSProviderBuild=true`.

## Packages

The repository produces three public packages:

- `CopperPad`
- `CopperPad.HidSharp`
- `CopperPad.GameController`

All packages are currently versioned `2.0.0-preview.1`. The preview has passed automated Windows, Linux, and iOS compilation checks, but Apple controller behavior has not yet been verified on physical iOS hardware. See [RELEASE.md](RELEASE.md) for release validation and [CONTRIBUTING.md](CONTRIBUTING.md) for contribution guidance.

## License

CopperPad is licensed under the [MIT License](LICENSE). The bundled SDL_GameControllerDB data retains its own license; see `src/CopperPad/THIRD-PARTY-NOTICES.md`.
