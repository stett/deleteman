# What the window shows and does

Reference for the UI's behaviour. `tests\DeleteMan.ViewModels.Tests` enforce most of it; change the tests
with the behaviour.


- **Drive rows** show capacity on the right and the used fraction as the filled part of the bar. Hover for
  used/free. Click to scan the drive and open its *used* row.
- **Used row** (`C:\`) is the used space at full width, split into the root folders, `(files)` for files
  directly in the root, and a hatched `unscanned` remainder that shrinks as the scan proceeds (after the
  scan it becomes `other/system`: metadata, protected folders, shadow copies).
- **Folder rows** show the folder's total on the right and its subfolders as segments, largest first.
  Segment names are clipped to the segment; hover to see the full path, size and file count. Subfolders under
  0.25 % of the bar, and any past the 64 largest (all the engine lists for a row), fold into one leftover
  segment at the end of the bar: `(small N)` when every subfolder was listed, `(other N)` when some were not.
  Click it for a page of those folders, largest first, at their own scale; a page's own leftover (its tiny
  folders, and whatever lies beyond the page) opens the next page. Access-denied folders appear as striped red
  slivers.
  Click `(files)` to list the files directly in the folder, largest first; click a file to open it as its own
  row, which carries the Recycle Bin and delete buttons for that one file. When the loose files amount to one
  file (the only one, or the only one big enough to show), the segment carries that file's name instead of
  `(files)` and opens its row directly.
- **Controls** on each row, as two joined pairs: ♻ move to the Recycle Bin with 🗑 delete permanently (folder
  and file rows only), and pin (a double chevron down, "expand all") with close (a chevron up, "contract this").
  Pin and close are on every row, drive rows included, and are greyed out where they cannot act: the pin on a row
  with no folders below it, close on a collapsed drive. The pin keeps the largest item open at every level below
  the row and follows the scan as sizes change: a folder, or the loose files and then the largest file among them,
  down to that file's row. A drive has one pin at a time, so pinning another row moves it there;
  pinning takes over whatever is open below the row, and opening a folder by hand under the pin afterwards
  releases it (the button turns off); pin again to take the rows back over. The drive row's pin is its own: on a collapsed drive it starts the scan and
  opens the root under it, and it keeps the largest folders open below the drive just as a pin on the root row
  would, so the two are interchangeable but toggle independently. Close on a drive row collapses the drive. Every
  deletion asks for confirmation and names the path, size and counts. Drive roots cannot be deleted.
- **During a deletion** the row becomes a progress bar (`files · GB freed · elapsed`), everything else is
  locked, and the only live control is **Cancel**, which warns that the folder will be left partially
  deleted. Errors (for example a file held open by another program) are collected and listed when the job
  finishes; the folder is then rescanned so what is left is visible.
- **Resume.** Jobs are written to `jobs.json` in `%LOCALAPPDATA%\com.deleteman.app\` once a second.
  Unfinished jobs restart automatically when the app starts and are shown in the banner with a `resumed`
  tag. Closing the window while a deletion runs asks first; the job resumes next launch.

The window uses the Fluent UI and follows the system light/dark theme and accent colour.

The app runs as a normal user. Folders it cannot read are shown as access denied rather than silently
skipped. Junctions and symbolic links are never followed, neither while scanning nor while deleting: a
link inside a deleted folder is removed as an entry and its target is left untouched.

