# RVZStudio Documentation

RVZStudio is a cross-platform desktop application for batch converting GameCube and Wii disc
images to the **RVZ** format, verifying the integrity of existing RVZ files, and extracting RVZ
images back to ISO, WBFS, GCZ, WIA, CISO or TGC. It runs on **Windows, Linux and macOS** on both
**x64 and ARM64** architectures and is built with [Avalonia UI](https://avaloniaui.net/) on .NET 10.

![RVZStudio main window](../screenshot.png)

## Documentation map

| Page | Description |
|------|-------------|
| [Getting Started](getting-started.md) | Requirements, installation and a five-minute quick start. |
| [User Guide](user-guide.md) | Complete walkthrough of every screen and control. |
| [Settings Reference](settings-reference.md) | Compression methods, levels, block sizes and all options. |
| [Troubleshooting](troubleshooting.md) | Solutions for common problems and how to collect diagnostics. |
| [FAQ](faq.md) | Short answers to frequently asked questions. |
| [What's New](WhatsNew.md) | Release highlights and changes for the current version. |
| [Architecture](architecture.md) | Internal design, services, data flow and platform integration. |
| [Building](building.md) | Building, testing, publishing and the CI/CD pipelines. |
| [Contributing](contributing.md) | How to report issues and submit changes. |

## What RVZStudio does

- **Batch conversion** — convert many ISO, GCM, WBFS, GCZ, WIA, CISO/WBI, TGC, NFS, NKIT.ISO,
  ZIP, 7Z and RAR inputs to RVZ in a single run, with an optional Scrub setting for Wii discs.
- **Native RVZ engine** — the built-in [RVZSharp](https://github.com/purelogiccode/RVZSharp)
  library encodes, decodes and verifies disc images natively, with automatic fallback to
  DolphinTool when needed.
- **Integrity verification** — check existing RVZ files with the native volume verifier
  (partition hash trees, TMD/H3 tables) and CRC-32/MD5/SHA-1 hashes, with DolphinTool fallback,
  and optionally move results into `_Success` / `_Failed` folders.
- **Extraction** — decode RVZ files back to ISO, WBFS, GCZ, WIA, CISO or TGC.
- **Disc explorer** — browse the file system of any supported disc image, copy files/folders out
  and compute per-file SHA-256 hashes without extracting the whole image.
- **Live progress** — overall progress bar, per-file progress, statistics and a streaming log
  viewer.

## External links

- **Releases & source code**: <https://github.com/purelogiccode/RVZStudio>
- **Website**: <https://www.purelogiccode.com>
- **Dolphin Emulator** (DolphinTool): <https://dolphin-emu.org/>
- **SharpCompress**: <https://github.com/adamhathcock/sharpcompress>

## License

RVZStudio is distributed under the GNU General Public License v3.0. See [LICENSE.txt](../LICENSE.txt).
