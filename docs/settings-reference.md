# Settings Reference

RVZStudio exposes a small set of settings that control output size, conversion speed and how
files are organized. All settings are per-run; the application does not persist them between
sessions.

## Conversion settings

These options live under **Compression Settings** on the *Convert to RVZ* tab.

### Compression method

| Method | Level range | Engine | Typical use |
|--------|-------------|--------|-------------|
| `zstd` (default) | 1 – 22 | RVZSharp | Best overall balance of speed and size |
| `zlib` | 1 – 9 | DolphinTool only | Maximum compatibility |
| `lzma` | 1 – 9 | RVZSharp | Excellent compression, slow |
| `lzma2` | 1 – 9 | RVZSharp | Excellent compression, slow |
| `bzip2` | 1 – 9 | RVZSharp | Alternative to zlib/lzma |
| `lz4` | 1 – 12 | DolphinTool only | Fastest conversion, larger files |

When a method that RVZSharp cannot write is selected (`zlib`, `lz4`), the conversion always uses
DolphinTool. For the other methods RVZStudio first tries the native engine and transparently falls
back to DolphinTool if the library reports a problem.

### Compression level

The slider range adapts to the selected method (see the table above). Higher levels compress
better but take longer. If you switch to a method with a smaller range, the level is clamped
automatically.

### Block size

The block size is the granularity at which the RVZ container stores compressed data.

| Block size | Notes |
|------------|-------|
| 32 KB | Best random access, slightly worse compression |
| 64 KB | |
| **128 KB** (default) | Recommended balance |
| 256 KB | |
| 512 KB | |
| 1 MB | Better compression |
| 2 MB | Best compression, coarser random access |

### Recommended presets

| Goal | Method | Level | Block size |
|------|--------|-------|------------|
| Balanced (default) | `zstd` | 5 | 128 KB |
| Smallest files | `lzma2` | 9 | 2 MB |
| Fastest conversion | `lz4` | 1 | 32 KB |
| Maximum compatibility | `zlib` | 9 | 128 KB |

## General settings — Convert tab

| Option | Default | Description |
|--------|---------|-------------|
| Delete original files after conversion | Off | Permanently deletes each source file (including archives) after it converts successfully. Use with caution. |
| Scrub non-game Wii partitions (update/channel) | Off | Zeroes the data of non-game Wii partitions before encoding, making RVZ files smaller. Ignored for GameCube discs. |

## General settings — Verify tab

| Option | Default | Description |
|--------|---------|-------------|
| Move failed RVZ files to '_Failed' subfolder | Off | Moves files that fail verification into `_Failed`. |
| Move successful RVZ files to '_Success' subfolder | Off | Moves verified files into `_Success`. |
| Include subfolders | Off | Searches for RVZ files recursively and refreshes the list when toggled. |

## General settings — Extract tab

| Option | Default | Description |
|--------|---------|-------------|
| Delete original RVZ files after extraction | Off | Deletes the source RVZ after a successful extraction. |
| Output Format | `ISO` | Target format: `ISO`, `WBFS`, `GCZ`, `WIA`, `CISO` or `TGC`. |

All output formats are written natively by RVZSharp. DolphinTool is used as a fallback for the
formats it supports (`ISO`, `WBFS`, `GCZ`, `WIA`); `CISO` and `TGC` are RVZSharp-only.

## Behavior that is always on

Some safeguards are not configurable:

- **Same-folder protection** — input and output folders must differ and must not be nested.
- **Existing output handling** — conversions skip files whose RVZ output already exists.
- **Disc header pre-validation** — before the native encoder runs, RVZStudio checks the
  GameCube/Wii disc magic on ISO/GCM files and the real container magic on WBFS/GCZ/WIA/CISO/
  TGC/NFS files. Unrecognized data is routed to DolphinTool instead of producing a broken RVZ.
- **Automatic fallback** — any RVZSharp failure falls back to DolphinTool for that file (where
  DolphinTool supports the operation) and the batch continues.

## Related pages

- [User Guide](user-guide.md) — where to find each option.
- [Architecture](architecture.md) — how the engines and fallback work internally.
