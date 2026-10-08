# CopperPad audit fix validation

Recorded on 2026-10-08 for the working-tree changes based on
`751139d0ea5c8e05024ecd7e2bfa97e2ba478478`.

All eight findings have code fixes and passing regression coverage. Release
validation is still incomplete: hosted Windows/Linux CI, native iOS compilation,
and physical-controller checks must pass on the final release commit.

## Fixes and regression coverage

| Finding | Result | Coverage |
| --- | --- | --- |
| Profile document preservation | Failed or cancelled loads block ordinary saves. Retrying a successful load restores saving. Import recovery copies the original to a unique `.bak` before replacing it; backup failures retain the original and drafts. Previously loaded profiles survive a failed retry. | `ProfileRecoveryTests`, GUI save/import/repair regressions |
| SDL decoding | Persistent parsers receive the matching HID report definition. Unknown IDs and incomplete reports diagnose the problem and retain the previous state. Raw fallback is explicit. Separate parsers per input report avoid HidSharp 2.6.4 overwriting another report's values. | `SdlHidInputDecoderTests`, SDL audit reproductions; numbered/unnumbered reports, signed axes, buttons, null hats, stable indexes and multiple reports |
| Lifecycle notifications | Provider generation and session checks suppress notifications invalidated by Stop/Dispose callbacks. The host clears retained controllers and releases their state. Host refreshes and element/profile notification sequences stop after reentrant lifecycle changes. | Lifecycle audit reproduction with Stop and Dispose; provider discovery/snapshot callbacks, host discovery disconnect callbacks and profile callbacks |
| Diagnostics reader cancellation | Reader identity fences raw data, mapped snapshots, and read errors. Old handles close before replacements open; stream disposal occurs once. | `DiagnosticsReaderLifetimeTests`; delayed data/errors after profile changes, device switches, Stop and Dispose; changes inside a raw callback; current errors remain visible |
| Switch Pro Y axes | Packed Y uses the shared byte's high nibble and all eight bits of the following byte. | `MapperSafetyTests`; both sticks at center and both extremes, with different X coordinates |
| Device identification | Known fallback layouts require supported vendor/product pairs, sufficient report length, and controller usage when a descriptor exists. Manufacturer/name matches alone cannot select these layouts. Explicit profiles retain precedence, including discovery with usage filtering enabled. | Manufacturer/peripheral, supported product/layout, and explicit override regressions |
| Diagnostic capabilities | Unmapped discovery metadata and connected/disconnected snapshots advertise only `RawInput`, with no fabricated gamepad elements. | Mapper and provider discovery/read capability regressions |
| Truncated axis sources | Signed 16-bit bindings require two available bytes. Incomplete bindings stay neutral and identify their target/offset in a diagnostic. Active-low sources cannot become pressed through truncation. | Signed source tests for axes, triggers and buttons; effective report lengths, large offsets, and complete signed ranges |

## Local results

Host: Debian GNU/Linux 13, x64, .NET SDK 10.0.401.

| Check | Result |
| --- | --- |
| Release solution build with `-warnaserror` | Passed; zero warnings and zero errors |
| Complete core suite | 111 passed; zero failed or skipped |
| Complete GUI suite | 137 passed; zero failed or skipped |
| Original audit reproductions | Eight core reproductions and one GUI reproduction passed; the external GUI harness's Headless package was aligned to the upgraded GUI dependencies |
| Headless startup/teardown regression | 100 sequential session lifetimes passed as part of the GUI suite |
| Patch whitespace check | `git diff --check` passed |

Commands used from the repository root:

```sh
dotnet build CopperPad.slnx --configuration Release -warnaserror
dotnet test CopperPad.slnx --configuration Release --no-build --no-restore \
  --logger 'trx;LogFilePrefix=audit-fixes-final' \
  --results-directory /tmp/copperpad-validation
```

The session used a temporary .NET installation and NuGet cache. GUI tests used
Fontconfig restricted to the installed DejaVu TrueType fonts; this environment's
default font selection otherwise chooses a web font unsupported by Skia.
Build output and TRX results were retained in `/tmp/copperpad-release-build.log`,
`/tmp/copperpad-full-validation.log`, and `/tmp/copperpad-validation/`.

## GUI teardown investigation

Avalonia 12.0.3's `HeadlessUnitTestSession.StartNew` starts `Task.Run` before
assigning its result to the captured `task` variable. The delegate can construct
a session with a null dispatch task, which subsequently fails in `Dispose`.
This accounts for the intermittent teardown failure across different theory rows.

The GUI and Headless packages now use Avalonia 12.1.3. Its implementation constructs
the task first and starts it after assignment. Screenshot tests use the new PNG
encoder overload so the full solution remains warning-free. Source references:
[12.0.3](https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Headless/Avalonia.Headless/HeadlessUnitTestSession.cs)
and [12.1.3](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Headless/Avalonia.Headless/HeadlessUnitTestSession.cs).

Save-failure tests now create their blocking directory after startup. A directory
present during loading correctly triggers the new load protection, which must be
tested separately from an ordinary write failure.

## Platform and hardware checks still required

| Check | Status and reason |
| --- | --- |
| Hosted Linux CI | Not run for these working-tree changes; local Linux build/full suites passed. The existing [CI workflow](../.github/workflows/ci.yml) covers Ubuntu 24.04. No authenticated workflow dispatch or published commit was available in this session. |
| Hosted Windows CI | Not run; no Windows execution host or authenticated workflow dispatch was available. The same CI workflow covers Windows 2025. |
| Native iOS compilation | Attempted with `dotnet build src/CopperPad.GameController/CopperPad.GameController.csproj -c Release -f net10.0-ios -p:EnableIOSProviderBuild=true -warnaserror`. Restore stopped at NETSDK1147 (missing workload; this Linux SDK reported `wasm-tools`). This host has no macOS/Xcode or iOS toolchain. The local solution build compiles the platform-neutral Apple code and stub, not the native iOS provider. Run the existing [iOS workflow](../.github/workflows/ios.yml) on macOS before release. |
| Physical desktop controllers | Not run; no `/dev/hidraw*` or `/dev/input/event*` devices were exposed. Synthetic reports do not verify actual device layouts, driver behavior or transport differences. |
| Physical iOS controllers | Not run; no iOS device or controllers were available. The [Apple hardware gate](../RELEASE.md#apple-hardware-gate) remains required. |

Before release, record CI run links for the final commit and smoke-test a DualShock
4, Switch Pro, Xbox controller, and a generic/SDL-mapped HID controller. Exercise
USB and Bluetooth where supported: both sticks at center/extremes, hats, buttons,
triggers, simultaneous controls across report IDs, disconnect/reconnect, profile
save/reload/recovery, device switching and profile changes during live reads, and
Stop/Dispose callbacks. Verify that unrelated same-manufacturer keyboards and
adapters are excluded unless explicitly profiled. Record each device's VID/PID,
transport, OS, firmware, mapping source, report descriptor and observed result.

Keep these checks marked pending until the corresponding platform or hardware
result is recorded; the local passes do not establish release readiness.
