# Frequently Asked Questions

### What is RVZStudio?

A cross-platform desktop application that batch converts GameCube and Wii disc images to the RVZ
format, verifies RVZ integrity, and extracts RVZ files back to ISO, WBFS, GCZ, WIA, CISO or TGC.

### Which platforms are supported?

Windows, Linux and macOS on both x64 and ARM64. See [Getting Started](getting-started.md) for
installation instructions.

### Is RVZStudio free?

Yes. It is open source under the GNU General Public License v3.0 (see [LICENSE.txt](../LICENSE.txt)).

### Do I need Dolphin or DolphinTool installed separately?

No. RVZStudio's primary engine (RVZSharp) is built in. Official release packages also bundle
`DolphinTool` as an optional fallback (and `7za` as an archive fallback); RVZStudio looks for them
in its own folder and works without them.

### Which input formats can I convert?

`.iso`, `.gcm`, `.wbfs`, `.gcz`, `.wia`, `.ciso`, `.wbi`, `.tgc`, `.nfs`, `.nkit.iso` and the
archives `.zip`, `.7z`, `.rar` containing any of those.

### Can I convert RVZ back to ISO?

Yes — use the **Extract from RVZ** tab. Supported outputs are ISO, WBFS, GCZ, WIA, CISO and TGC.

### Can I browse the files inside a disc image?

Yes — the **Explorer** tab opens any supported image (ISO, RVZ, WIA, GCZ, CISO, WBFS, TGC, NFS)
and shows its file system. You can expand folders, copy individual files or whole folders out,
and compute per-file SHA-256 hashes without converting the image.

### What compression should I use?

`zstd` at level 5 with a 128 KB block size is the recommended default. See the
[Settings Reference](settings-reference.md) for presets.

### Is conversion lossless?

Yes. RVZ is a lossless container: no game data is discarded. RVZSharp decodes RVZ back to the
original ISO byte-for-byte.

### Does it work without an internet connection?

Yes. Internet access is only used for the optional update check and anonymous error/usage
reporting.

### What data does RVZStudio send?

- An anonymous launch statistic (application id and version).
- Automatic bug reports for warnings and errors, including the error message, exception details,
  operating system and architecture. No game files or personal files are transmitted.

### Where are the settings stored?

RVZStudio does not persist UI settings between sessions; each batch uses the defaults unless you
change the controls for that run. Logs are stored in the platform-specific locations listed in
[Troubleshooting](troubleshooting.md).

### Can multiple conversions run at once?

No. Only one operation (conversion, verification or extraction) can run at a time; the other
actions are disabled until it finishes. Click **Cancel** to stop the current operation.

### Where can I report bugs or request features?

Open an issue at <https://github.com/purelogiccode/RVZStudio/issues>. Include your OS,
RVZStudio version and the relevant log output.

### How can I support the project?

Star the repository, report bugs, contribute code or documentation, or donate through
<https://www.purelogiccode.com/donate>.

## Related pages

- [Troubleshooting](troubleshooting.md)
- [Contributing](contributing.md)
