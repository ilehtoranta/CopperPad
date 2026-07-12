# CopperPad Release Checklist

## Automated checks

- Confirm the `CI` workflow passes on Windows and Linux.
- Confirm the `iOS` workflow builds `net10.0-ios`, packs the iOS-enabled package, and uploads its artifact.
- Build and test locally when practical:

```sh
dotnet restore CopperPad.slnx
dotnet build CopperPad.slnx --configuration Release --no-restore -warnaserror
dotnet test CopperPad.slnx --configuration Release --no-build --no-restore
```

## Package checks

Pack the platform-neutral libraries:

```sh
dotnet pack src/CopperPad/CopperPad.csproj --configuration Release --output artifacts/packages
dotnet pack src/CopperPad.HidSharp/CopperPad.HidSharp.csproj --configuration Release --output artifacts/packages
dotnet pack src/CopperPad.GameController/CopperPad.GameController.csproj --configuration Release --output artifacts/packages
```

- Confirm package IDs and versions are correct and all packages reference `https://github.com/ilehtoranta/CopperPad`.
- Confirm every `.nupkg` contains `README.md` and `LICENSE`.
- Confirm `CopperPad.HidSharp` also contains `THIRD-PARTY-NOTICES.md`, the SDL_GameControllerDB license, and `gamecontrollerdb.txt`.
- Obtain the iOS-enabled `CopperPad.GameController` package from the successful `iOS` workflow rather than publishing a Windows-produced package.

## GUI checks

Publish and smoke-test the end-user GUI:

```powershell
./scripts/SmokeTest-Gui.ps1 -Configuration Release -Runtime win-x64
```

- Start with no controller attached; the application must remain open and show a clear empty state.
- Attach a known controller; it must appear without enabling **Show all HID**.
- Exercise controller summary, test, profile creation/editing, save, reload, import, and export.
- Disconnect and reconnect while the GUI is open; the application must show status instead of exiting.
- Confirm the GUI archive contains the executable, README, MIT license, third-party notices, and SDL data/license files.

## Apple hardware gate

On physical iOS hardware, verify:

- Live extended, standard, and micro-gamepad updates.
- Two same-vendor controllers receive distinct provider-lifetime IDs.
- Independent disconnect and reconnect behavior.
- Disconnect neutralizes buttons, axes, and triggers exactly once.
- Provider stop and disposal do not produce duplicate callbacks.

## Release

- Confirm downstream applications build against the release candidate.
- Document breaking API changes, immutable runtime collections, `ReadOnlyMemory<byte>` report data, and profile JSON schema v2 compatibility.
- Until the Apple hardware gate is complete, use a prerelease version such as `2.0.0-preview.1` and explicitly document the missing hardware validation.
- Create and push an annotated version tag such as `v2.0.0-preview.1` from the validated commit.
- The tag-triggered release workflow publishes all packages from that exact commit and creates the matching GitHub release. Hyphenated versions are marked as prereleases automatically.
