# User Guide

This page describes every part of the RVZStudio interface.

![Convert tab](../screenshot.png)

## Window layout

The main window is divided into four regions:

| Region | Purpose |
|--------|---------|
| **Header** | Application title and the global **Updates**, **About** and **Exit** buttons. |
| **Left panel** | A tab control with the three workflows: *Convert to RVZ*, *Verify Integrity of RVZ* and *Extract from RVZ*. |
| **Right panel** | The live log viewer. The divider between the panels can be dragged to resize them. |
| **Bottom area** | Statistics cards, progress bars and the status bar. |

The header buttons are always available:

- **Updates** — manually checks GitHub for a newer release.
- **About** — opens the About window with the version, links and acknowledgements.
- **Exit** — closes the application. If an operation is running, it is cancelled first.

## Convert to RVZ

The first tab converts disc images and archives to RVZ.

### Input and output folders

- **Input Folder** — the folder containing the files to convert. Click **Browse…** to pick a
  folder, or drag a folder onto the file list.
- **Output Folder** — where the converted `.rvz` files are written. It must be a different folder
  than the input folder and must not be nested inside it. The folder is created if it does not
  exist.

### File list

After a folder is selected, all supported files in it are listed with a checkbox, the file name
and the file size. Files are selected by default.

- **Select All** / **Deselect All** — toggle every checkbox at once.
- **Drag and drop** — drop files directly onto the list to add them. Duplicates are ignored.
  Dropping a folder replaces the input folder and rescans it.

Supported input formats: `.iso`, `.gcm`, `.wbfs`, `.gcz`, `.wia`, `.ciso`, `.wbi`, `.tgc`, `.nfs`,
`.nkit.iso`, `.zip`, `.7z`, `.rar`. Archives are extracted automatically and the disc image inside
is converted.

### Starting a conversion

Click **Start Conversion**. During the run:

- The **Cancel** button appears next to the progress area.
- The per-file progress bar tracks the active file's progress in real time.
- The overall progress bar fills as files complete.
- The log viewer streams messages from the application, RVZSharp and DolphinTool live.

When the batch finishes, a summary dialog reports totals. Files whose output already exists are
skipped.

## Verify Integrity of RVZ

The second tab checks existing RVZ files for corruption.

1. Select the **Verify Folder** that contains RVZ files.
2. Configure the options under **General Settings**:
   - **Move failed RVZ files to '_Failed' subfolder** — moves files that fail verification.
   - **Move successful RVZ files to '_Success' subfolder** — moves verified files.
   - **Include subfolders** — scans subfolders recursively. Toggling this rescans the folder.
3. Review the file list and click **Start Verification**.

Verification results are streamed to the log viewer. When files are moved, the list refreshes
after the batch so it reflects the new locations.

## Extract from RVZ

The third tab decodes RVZ files back to other formats.

1. Select the **Input Folder** containing RVZ files.
2. Select the **Output Folder** for the extracted images (different from the input folder).
3. Configure the options under **General Settings**:
   - **Delete original RVZ files after extraction** — removes the source RVZ after a successful
     extraction.
   - **Output Format** — one of `ISO`, `WBFS`, `GCZ`, `WIA`, `CISO` or `TGC`.
4. Click **Start Extraction**.

An overlay is shown if extraction is cancelled while an archive is still being unpacked.

## Explorer

The fourth tab browses the file system inside a disc image without extracting it.

1. Click **Browse…** to pick a disc image (`.iso`, `.rvz`, `.wia`, `.gcz`, `.ciso`, `.wbi`,
   `.wbfs`, `.tgc` or `.nfs`).
2. Click **Open** to parse the image. The volume summary shows the container type, decoded size,
   block size, game ID/title/region, partition and FST location.
3. Expand folders in the tree and select an entry. The details panel shows its image path, type,
   size and data offset.
4. **Copy Out…** extracts the selected file (a save dialog) or the selected folder (a folder
   dialog, copied recursively) to disk.
5. **SHA-256** computes the hash of the selected file directly from the image.
6. For Wii discs with multiple partitions, a **Partition** selector appears to switch between the
   game, update and channel partitions.
7. **Refresh** reopens the current image; **Close** releases it.

The explorer reads the image in-process through RVZSharp (the same FST parser Dolphin uses), so
copy-out writes the decrypted file bytes without converting the whole disc.

## Progress, statistics and logs

| Element | Meaning |
|---------|---------|
| **TOTAL FILES** | Number of files in the current batch. |
| **SUCCESS** | Files completed successfully. |
| **FAILED** | Files that failed. |
| **ELAPSED** | Wall-clock time since the batch started. |
| **SPEED** | Average throughput in MB/s based on processed bytes. |
| **File progress bar** | Progress of the file currently being processed. |
| **Overall progress bar** | Completed files / total files. |

The log viewer shows timestamped messages from the application, RVZSharp and DolphinTool. It
scrolls automatically only when you are already at the bottom, so you can read earlier messages
while a batch runs. The log is cleared at the start of each batch and trimmed automatically if it
exceeds 5,000 lines.

## Cancelling an operation

Click **Cancel** to request cancellation. The current child process is terminated and the batch
stops as soon as the active file is aborted. A "Please wait" overlay is displayed while the
operation is winding down.

## Keyboard shortcuts

| Key | Action |
|-----|--------|
| `F8` | Save a PNG screenshot of the window to the `Screenshot` folder next to the application. |

## Drag and drop

Drag and drop works on all three file lists:

- Dropping a **folder** sets it as the input folder for that tab and rescans it.
- Dropping **files** adds the supported ones to the list and sets the folder box to the common
  parent directory (or `(Multiple locations)` when the files come from different folders).
- Unsupported files are filtered out; if nothing is supported an information dialog is shown.

## Related pages

- [Settings Reference](settings-reference.md) — every option explained.
- [Troubleshooting](troubleshooting.md) — when something does not work.
