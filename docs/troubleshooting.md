# Troubleshooting

This page lists the most common problems and how to solve them. If a problem persists, check the
[FAQ](faq.md) and the log file before reporting an issue.

## The action buttons are disabled / "critical file(s) are missing"

RVZStudio looks for the platform-specific helper executables next to the application binary.

1. Make sure you extracted **all** files from the release archive into the same folder.
2. Check the expected file names for your platform:

   | Platform | x64 | ARM64 |
   |----------|-----|-------|
   | Windows | `DolphinTool.exe`, `7za.exe` | `DolphinTool_arm64.exe`, `7za_arm64.exe` |
   | Linux / macOS | `7za` | `7za_arm64` |

   `DolphinTool` is optional on Linux/macOS: the native RVZSharp engine handles conversion and
   verification without it, and `7za` ships with every release package.

3. If you launched the application from a temporary extraction folder (for example from inside a
   WinRAR/ZIP preview), the folder may have been deleted while the application was running.
   Extract to a permanent folder and start again.

## Linux/macOS: "Executable not found" or permission denied

ZIP archives do not always preserve the executable bit. RVZStudio tries to set it automatically,
but you can do it manually:

```bash
chmod +x RVZStudio 7za*
```

## macOS: the app cannot be opened (Gatekeeper)

Unsigned binaries may be quarantined. Allow them in **System Settings → Privacy & Security**, or
remove the quarantine attribute:

```bash
xattr -dr com.apple.quarantine .
```

## Conversions fail for some files

- **Check the log viewer** — messages from RVZSharp and DolphinTool are streamed live and usually
  state the exact reason (corrupt file, unsupported format, not a disc image).
- **Corrupt inputs** — damaged ISOs cannot be converted. Verify the source file.
- **Archives** — RVZStudio extracts with SharpCompress first and falls back to `7za`. If both
  fail, the archive is likely damaged or password-protected.
- **RVZSharp fallback** — if the native engine cannot handle a file it automatically uses
  DolphinTool (when available). A message such as `falling back to DolphinTool` in the log is normal.

## Verification reports failures

Verification runs the native RVZSharp volume verifier first and falls back to DolphinTool's
`verify` command when needed. A failure means the RVZ data is damaged or was produced by an
incompatible tool. Re-convert the original disc image if you still have it.

## "The input and output folders must be different"

Choose an output folder that is not the input folder and is not nested inside it. RVZStudio
refuses these combinations to avoid converting a folder into itself.

## Update check fails

- A failed check is logged as `Failed to check for updates` and is harmless.
- Verify your internet connection and that GitHub is reachable.
- Manual update checks show a dialog with the reason (network error, timeout).

## Usage statistics / bug reports

RVZStudio sends an anonymous launch statistic and automatically reports warnings and errors to the
developer's services. If a network request fails, it is ignored and the application continues
normally.

## Where are the logs?

| Platform | Location |
|----------|----------|
| Windows | `%LocalAppData%\RVZStudio\logs\log-YYYYMMDD.txt` |
| Linux | `~/.local/share/RVZStudio/logs/log-YYYYMMDD.txt` |
| macOS | `~/Library/Application Support/RVZStudio/logs/log-YYYYMMDD.txt` |

Log files roll daily and are kept for 14 days (up to 10 MB per file). The on-screen log viewer
shows the same messages.

## Screenshots for bug reports

Press `F8` while the window is focused. A timestamped PNG is written to the `Screenshot` folder
next to the application binary.

## Performance is slow

- Use `zstd` or `lz4` instead of `lzma2` for faster conversions.
- Conversion is sequential; very large batches take time. The elapsed time and average speed are
  shown in the statistics cards.
- Make sure the output folder is on a fast disk with enough free space.

## Nothing helped

Report the issue at <https://github.com/purelogiccode/RVZStudio/issues> and include:

1. Operating system and architecture.
2. The RVZStudio version (About window).
3. Steps to reproduce.
4. The relevant part of the log file and, if possible, a screenshot (`F8`).

## Related pages

- [FAQ](faq.md)
- [Getting Started](getting-started.md)
- [Architecture](architecture.md)
