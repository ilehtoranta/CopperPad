# Contributing to CopperPad

## Development requirements

- .NET 10 SDK
- Git
- macOS and the matching Xcode/.NET iOS workload only when changing native Apple provider code

## Validate a change

Run the same commands used by normal CI:

```sh
dotnet restore CopperPad.slnx
dotnet build CopperPad.slnx --configuration Release --no-restore -warnaserror
dotnet test CopperPad.slnx --configuration Release --no-build --no-restore
```

Changes to controller mappings, lifecycle behavior, persistence, or public APIs should include focused regression tests. Changes to Apple-native code must also pass the `iOS` workflow and should be smoke-tested on physical hardware before release.

## Pull requests

Keep changes focused, describe externally visible behavior, and note any compatibility impact. Do not commit build output, generated packages, controller profiles containing personal data, or signing material.

