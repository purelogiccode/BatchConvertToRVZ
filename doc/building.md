# Building

This page explains how to build, test and package RVZStudio from source.

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git
- Optional: Visual Studio 2022+, JetBrains Rider or Visual Studio Code with the C# extension

The repository pins the SDK through `global.json` (`10.0.0`, rolling forward to the latest major).

## Clone and build

```bash
git clone https://github.com/purelogiccode/BatchConvertToRVZ.git
cd BatchConvertToRVZ
dotnet build RVZStudio.sln -c Release
```

The solution contains two projects:

| Project | Description |
|---------|-------------|
| `RVZStudio` | The Avalonia desktop application. |
| `RVZStudio.Tests` | xUnit test suite for models and services. |

## Run

```bash
dotnet run --project RVZStudio/RVZStudio.csproj
```

The optional fallback executables (`DolphinTool*`, `7za*`) are copied automatically from the
project folder to the output directory by the build; the application runs without them using the
built-in RVZSharp engine.

## Test

```bash
dotnet test RVZStudio.sln
```

Or run the test project directly with a detailed logger:

```bash
dotnet test RVZStudio.Tests/RVZStudio.Tests.csproj --logger "console;verbosity=normal"
```

The test suite is platform-aware and runs on Windows, Linux and macOS.

## Publishing

### Using the publish script

`publish.ps1` produces self-contained, single-file builds for every supported platform and creates
one ZIP per runtime identifier. The helper executables are kept outside the single-file bundle so
they can be launched as child processes.

```powershell
./publish.ps1
```

Results:

```
publish/
├── win-x64/        # RVZStudio.exe + helper tools
├── win-arm64/
├── linux-x64/
├── linux-arm64/
├── osx-x64/
├── osx-arm64/
├── release_<version>_win-x64.zip
├── release_<version>_win-arm64.zip
├── release_<version>_linux-x64.zip
├── release_<version>_linux-arm64.zip
├── release_<version>_osx-x64.zip
└── release_<version>_osx-arm64.zip
```

Useful switches:

| Switch | Effect |
|--------|--------|
| `-Rids win-x64,linux-x64` | Publish only the listed runtime identifiers. |
| `-Configuration Debug` | Publish a debug build. |
| `-FrameworkDependent` | Publish framework-dependent instead of self-contained. |
| `-NoZip` | Skip ZIP creation. |

### Manual publish

```bash
dotnet publish RVZStudio/RVZStudio.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true
```

Supported runtime identifiers: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`,
`osx-arm64`.

## Continuous integration

Two GitHub Actions workflows live in `.github/workflows`:

### `ci.yml` — build and test

Runs on every push to `master`/`main`, on pull requests, and manually.

1. **Build & Test** matrix on `windows-latest`, `ubuntu-latest` and `macos-latest`:
   restore, `dotnet build -c Release`, `dotnet test`, and upload of the TRX test results.
2. **Publish smoke test** on Ubuntu for `linux-x64` and `linux-arm64` to catch packaging
   regressions.

### `release.yml` — publish and release

Triggered by pushing a tag that starts with `v` (for example `v2.5.0`) or manually from the
Actions tab.

1. A six-entry matrix publishes every runtime identifier on a matching runner and uploads the
   `release_<version>_<rid>.zip` files as artifacts.
2. A final job downloads all artifacts and creates a GitHub Release with auto-generated release
   notes and the ZIP files attached. The release job only runs for tag builds.

### Cutting a release

1. Update `AssemblyVersion`/`FileVersion` in `RVZStudio/RVZStudio.csproj` (and the test project if
   desired).
2. Commit the version bump.
3. Tag and push:

   ```bash
   git tag v2.5.0
   git push origin v2.5.0
   ```

4. The Release workflow builds and attaches the six ZIP archives.

## Versioning

The application version is read from the assembly and compared against GitHub release tags
(`v1.2.3` style) by `UpdateService`. The publish script reads `FileVersion` from the project file
to name the archives.

## Related pages

- [Architecture](architecture.md) — how the code is organized.
- [Contributing](contributing.md) — development workflow and conventions.
