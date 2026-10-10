# Getting Started

This page covers everything you need to install RVZStudio and convert your first disc image.

## System requirements

| Component | Requirement |
|-----------|-------------|
| Operating system | Windows 10 or later, a modern 64-bit Linux distribution, or macOS 11 or later |
| Architecture | x64 or ARM64 |
| Runtime | [.NET 10.0 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — required for the release builds (framework-dependent single-file) |
| Helper tools | Optional: `7za` (archive fallback, all platforms) and `DolphinTool` (fallback engine, Windows releases) |

> RVZStudio's primary engine is the built-in **RVZSharp** library (pure managed code). **DolphinTool**
> from the [Dolphin Emulator project](https://dolphin-emu.org/) is used as a fallback
> conversion/verification engine when available, and **7za** (7-Zip) is the fallback archive
> extractor. `7za` ships with every release package; `DolphinTool` ships with the Windows packages
> and can be added manually on Linux/macOS.

### Helper executable names

RVZStudio locates the helper tools next to the application binary. The expected file names are:

| Platform | x64 | ARM64 |
|----------|-----|-------|
| Windows | `DolphinTool.exe`, `7za.exe` | `DolphinTool_arm64.exe`, `7za_arm64.exe` |
| Linux / macOS | `7za` (optional `DolphinTool`) | `7za_arm64` (optional `DolphinTool_arm64`) |

## Installation

### Windows

1. Download `release_<version>_win-x64.zip` or `release_<version>_win-arm64.zip` from the
   [Releases page](https://github.com/purelogiccode/RVZStudio/releases).
2. Extract the archive to a permanent folder (avoid running directly from an archiver's
   temporary folder).
3. Run `RVZStudio.exe`.

### Linux

1. Download `release_<version>_linux-x64.zip` or `release_<version>_linux-arm64.zip`.
2. Extract the archive, for example:

   ```bash
   mkdir -p ~/apps/RVZStudio
   unzip release_2.5.0_linux-x64.zip -d ~/apps/RVZStudio
   cd ~/apps/RVZStudio
   chmod +x RVZStudio 7za*
   ./RVZStudio
   ```

3. Optionally create a desktop entry or launcher for `RVZStudio`.

### macOS

1. Download `release_<version>_osx-x64.zip` or `release_<version>_osx-arm64.zip`.
2. Extract the archive and run the binary:

   ```bash
   mkdir -p ~/Applications/RVZStudio
   unzip release_2.5.0_osx-arm64.zip -d ~/Applications/RVZStudio
   cd ~/Applications/RVZStudio
   chmod +x RVZStudio 7za*
   ./RVZStudio
   ```

3. If macOS Gatekeeper blocks the unsigned binaries, allow them in
   **System Settings → Privacy & Security** or remove the quarantine attribute:

   ```bash
   xattr -dr com.apple.quarantine .
   ```

> **Tip:** RVZStudio automatically adds the execute permission to the bundled helper tools when
> it can. If your extraction tool stripped permissions, the manual `chmod` above is all you need.

## First launch

When RVZStudio starts it performs three checks:

1. **Dependencies** — it checks whether the platform-specific `DolphinTool` executable exists in
   the application folder and reports the result. DolphinTool is optional: when it is missing the
   native RVZSharp engine still handles conversion, extraction and verification, and the log notes
   that the fallback is unavailable.
2. **Update check** — the application queries GitHub for the latest release and logs whether a
   newer version is available.
3. **Usage statistics** — an anonymous launch statistic is sent to the developer's stats service.
   Network failures are ignored silently.

Logs are written to:

| Platform | Location |
|----------|----------|
| Windows | `%LocalAppData%\RVZStudio\logs\log-YYYYMMDD.txt` |
| Linux | `~/.local/share/RVZStudio/logs/log-YYYYMMDD.txt` |
| macOS | `~/Library/Application Support/RVZStudio/logs/log-YYYYMMDD.txt` |

## Quick start: convert a folder of disc images

1. Open the **Convert to RVZ** tab.
2. Click **Browse…** next to *Input Folder* and select the folder containing your game files, or
   drag the folder onto the file list.
3. Click **Browse…** next to *Output Folder* and select an empty folder for the RVZ files.
   The output folder must be different from the input folder and cannot be nested inside it.
4. (Optional) Expand **Compression Settings** and choose the compression method, level and block
   size. The defaults — `zstd`, level `5`, block size `128 KB` — are a good balance of speed and
   size.
5. Ensure the files you want are checked in the list, then click **Start Conversion**.

Progress is shown in the status bar, the two progress bars and the statistics cards. When the
batch finishes, a summary dialog reports how many files succeeded and failed.

## Next steps

- Learn every screen in the [User Guide](user-guide.md).
- Tune output size and speed with the [Settings Reference](settings-reference.md).
- If something goes wrong, see [Troubleshooting](troubleshooting.md).
