# What's New

## Version 2.5.0

RVZStudio 2.5.0 is the first cross-platform release: the WPF/Windows-only application has been
rebuilt on **Avalonia UI** and now runs on **Windows, Linux and macOS** on both **x64 and ARM64**.

### Highlights

- **Cross-platform UI** — the same application now ships for Windows, Linux and macOS, and
  automatically locates the platform-specific helper executables.
- **Native RVZSharp engine** — the built-in [RVZSharp](https://github.com/purelogiccode/RVZSharp)
  library is now the primary engine for encoding, decoding and verification (100% managed codecs),
  with DolphinTool as an automatic fallback. Input files are pre-validated against their real
  container magic bytes so invalid data is never wrapped into a broken RVZ.
- **Disc Explorer tab** — browse the file system of ISO, RVZ, WIA, GCZ, CISO, WBFS, TGC and NFS
  images without extracting them; copy files/folders out and compute per-file SHA-256 hashes. The
  log viewer is hidden while this tab is active so the tree gets the full width.
- **Archive extraction with 7za fallback** — ZIP, 7Z and RAR archives are extracted with
  SharpCompress first; when SharpCompress cannot read an archive, the bundled **7-Zip `7za`**
  console tool is used as a fallback. 7-Zip 26.04 binaries are bundled for **all six platforms**
  (Windows/Linux/macOS × x64/ARM64).
- **Structured logging** — all logging goes through **Serilog** with an on-screen log viewer,
  rolling daily log files and automatic bug reports for warnings and errors.
- **Update checks, usage statistics and bug reporting** — the application checks GitHub releases on
  startup, sends anonymous launch statistics and reports unexpected errors to the developer.
- **Screenshot capture** — press **F8** to save a PNG of the application window.
- **Donate button** — support development directly from the header.

### Improvements

- Smooth overall progress bar, per-file progress, statistics cards and write-speed display.
- Drag and drop files onto the Conversion, Verification or Extraction tab.
- Wii discs with multiple partitions expose a partition selector in the Disc Explorer.
- Native extraction of ISO, WIA, GCZ, WBFS, CISO and TGC without DolphinTool.
- Scrub option zeroes non-game Wii partitions before encoding for smaller RVZ files.
- Verification logs CRC-32, MD5 and SHA-1 of the decoded image for every verified file.

### Fixes

- Fixed a startup error in the tab-selection handler that logged a spurious bug report on every
  launch.
- Fixed message boxes failing when shown from a background thread after a batch operation.
- Fixed cancellation during verification sometimes being reported as a completed batch.
- Fixed temporary extraction folders and orphaned 7za processes being left behind when an archive
  could not be extracted or an operation was cancelled.
- Fixed `.nkit.gcz` files producing a `.nkit.rvz` output name instead of `.rvz`.
- Fixed drag-and-drop of files from several folders refusing to start the operation.
- Fixed "Copy Out" of a folder from a disc image not sanitizing the folder name.
- Fixed fatal error dialogs closing before they could be read.
- Removed stale `Avalonia.Diagnostics` 11.x package that conflicted with Avalonia 12.
- Native SkiaSharp/HarfBuzz debug symbols are no longer shipped in release packages.

### Previous versions

See the [Releases page](https://github.com/purelogiccode/RVZStudio/releases) for older versions.
