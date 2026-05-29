<!-- BEGIN DISCLAIMER (managed by FFXIWindower author; do not remove) -->
## ⚠️ Disclaimer — Use at Your Own Risk

This is unofficial, fan-made software for *Final Fantasy XI*. It is **not affiliated with, endorsed by, or supported by Square Enix Holdings Co., Ltd.** FINAL FANTASY is a registered trademark of Square Enix.

**Square Enix's official position is that third-party tools and modifications to the FFXI client are prohibited by the Terms of Service.** Installing or using this software may result in account suspension, account termination, character data loss, or other action taken by Square Enix at their sole discretion.

This software is provided **AS IS, without warranty of any kind**, express or implied — including but not limited to warranties of merchantability, fitness for a particular purpose, and non-infringement. In no event shall the author or contributors be liable for any claim, damages, account action, lost time, lost progress, file corruption, or any other liability arising from the use of, or inability to use, this software.

**By installing, building, or running this software you acknowledge that you understand and accept these risks.**

<!-- END DISCLAIMER -->
### Additional note — back up your `mcr*.dat` files before first use

This tool rewrites your FFXI macro files (`mcr*.dat`) in place. It writes to a `.new` file first, renames the original to `.bak`, then renames the `.new` into place — so the previous version of each saved page is always one rename away. **However, the `.bak` is overwritten on every subsequent save.** Before relying on this tool, copy your entire `<FFXI install>\USER\<character-id>\` folder to a backup location.
# FFXI Macro Manager

Standalone Windows desktop app for editing FINAL FANTASY XI's `mcr*.dat`
macro files **outside the game**. Looks like GSUI (dark navy + cyan
accents). Not an addon — a real `.exe` you double-click.

Built because typing six lines into the in-game macro editor with the
controller-style cursor is brutal compared to a real text box.

## What it does

- Asks for your FFXI install folder (the one containing `USER\`)
- Finds every character on that install
- Lets you pick character → macro book → page
- Shows the full 20-slot macro hotbar (CTRL row + ALT row, 10 slots each)
- Click a slot to edit it — title plus six lines, in real text boxes
- Pick a job from the dropdown to get a filtered list of every spell
  that job can learn, sorted by level
- Double-click any spell to insert it into the current macro line, with
  the right prefix (`/ma`, `/ja`, `/ws`) and a target placeholder
- Save writes back to the real `mcr<N>.dat` file with a `.bak` of the
  original next to it

It is byte-perfect: round-trip every macro page on a real character
(150 files) saves identical bytes back to disk when nothing is edited.
Auto-translate codes (`{AT:cat/kind/id}`) round-trip too.

## First-time setup

1. Build the `.exe` (one time):
   ```
   build.bat
   ```
   This drops `FFXIMacroManager.exe` and a `data\` folder under
   `src\bin\Release\`. Copy that folder anywhere — it is self-contained
   apart from .NET Framework 4.7.2+ (preinstalled on Windows 10/11).

2. Run `FFXIMacroManager.exe`.

3. Click **"Pick FFXI install folder..."** and point at your FFXI root
   — typically `<drive>:\...\SquareEnix\FINAL FANTASY XI`. The app
   scans `USER\` for character folders.

4. Pick a character, a book (1-20), a page (1-10). Click any slot.

5. Edit title + lines. Press **Save page** to write back. The original
   file becomes `mcr<N>.dat.bak`.

## Tabs / panels

```
+-------------------------------+------------------+------------------+
|  Macro hotbar (CTRL + ALT)    |  Editor          |  Action library  |
|  20 slots                     |  title + 6 lines |  spells / JAs /  |
|  click to select              |  type freely     |  target tokens   |
+-------------------------------+------------------+------------------+
```

- **Job filter** at the top filters the action library to spells that
  job can natively learn (with the level requirement shown). Set it to
  `(all jobs)` for an unfiltered alphabetical list.

- **Action library** also has a kind dropdown — switch to **Job
  abilities** for the JA list, or **Targets** for snippets like
  `<me>`, `<t>`, `<wait 3>` that append to the current line instead of
  replacing it.

- **Save page** writes the whole 20-macro page back. Slots you didn't
  touch preserve their raw bytes (auto-translate codes included).

## Binary format (what the editor speaks)

Each `mcr<N>.dat` is exactly 7,624 bytes:

```
0x0000  Magic       01 00 00 00     4 bytes
0x0004  Flags       usually 0       4 bytes
0x0008  MD5 hash    of payload     16 bytes  (recomputed on every save —
                                              FFXI rejects mismatched files)
0x0018  Macros      20 × 380       7600 bytes

Per-macro layout (380 bytes):
  +0    Reserved          4 bytes
  +4    6 lines × 61 each 366 bytes (null-padded ASCII)
  +370  Title             10 bytes (null-padded, max 8 chars usable)
```

Hotkey mapping: macros 0-9 → CTRL+1..CTRL+0, macros 10-19 → ALT+1..ALT+0.

The MD5-hash discovery (initially I thought it was an opaque obfuscated
block) came from cross-checking against the towbes / azenus open-source
"FFXI Macro Editor" VB.NET source (Functions.vb in their repos). Without
recomputing the hash on save, FFXI silently refuses to load edited macros.

Lines are plain ASCII text with optional auto-translate codes embedded
as `FD <cat> <kind> <id-hi> <id-lo> FD`. The editor displays them as
`{AT:cat/kind/id}` (or the English name if known) and re-encodes
correctly on save.

## Where data comes from

`tools/generate-data.sh "<path to Windower\res>"` parses
`spells.lua`, `job_abilities.lua`, `auto_translates.lua`, `jobs.lua`
into JSON files in `data/`. The .exe loads from a sibling `data\`
folder next to it. Re-run when you upgrade Windower's resources.

## Build requirements

- Windows 10 or 11
- Visual Studio 2022+ (any edition, "Desktop development with .NET"
  workload) OR the .NET Framework 4.7.2 Developer Pack
- That's it. No npm, no NuGet, no Python.

## Standalone runtime

The .exe is fully standalone — Windower is NOT required. The `data\`
folder shipped next to the .exe contains pre-generated copies of every
spell, job ability, weapon skill, auto-translate entry, and job tag the
tool needs. Everything came from Windower's `res/*.lua` at build time
but the .exe never opens those files at runtime; it only reads the
`mcr*.dat` / `mcr.ttl` files inside your FFXI install's `USER\` folder.

If Windower happens to be installed alongside your FFXI install, the
Rename dialog will surface any GearSwap-derived character names as
*suggestions* (since those files conveniently contain "Kalitzo" in
filenames like `Kalitzo_blm.lua`). This is a hint only — you're never
required to have Windower.

## Character names

FFXI stores macro pages per character but it doesn't store the
human-readable name anywhere on disk — the name lives server-side and
is only resolved while you're logged in. So we ask you to type it once
per folder via the **Rename...** button. The mapping is saved to
`%AppData%\FFXIMacroManager\characters.txt` and is auto-loaded next run.

First time you select an unnamed character, the Rename dialog opens
automatically.

## Folder layout

```
FFXI Macro Manager\
  README.md
  build.bat
  src\
    App.xaml
    MainWindow.xaml
    Themes\GSUI.xaml
    Models\MacroFile.cs    -- binary format read/write
    Models\Character.cs    -- USER folder discovery
    Data\SpellDb.cs        -- spell/JA/AT lookup tables
    FFXIMacroManager.csproj
    Properties\AssemblyInfo.cs
  data\
    spells.json
    job_abilities.json
    auto_translates.json
    jobs.json
  tools\
    generate-data.sh       -- regenerate data\ from Windower
    verify-roundtrip.cs    -- byte-perfect round-trip test
```

## Safety

- Saves write to `mcr<N>.dat.new` first, then rename existing
  `mcr<N>.dat` to `mcr<N>.dat.bak`, then rename `.new` to the target.
  A crash mid-save can leave one of `.new` or `.bak` behind — your
  original is always recoverable.
- The page-title block (16 bytes) is obfuscated by the game; the
  editor never touches it.
- Round-trip verifier in `tools\verify-roundtrip.cs` proves byte-for-byte
  preservation across every macro file on a live character account.

## Credits

- Built by **TWinn22** (GitHub: TWinn22 / FFXI: Jason, 2026)
- Binary format (especially the MD5-hashed integrity field at offset 8)
  cross-checked against the open-source **FFXI Macro Editor** by towbes
  / azenus — their `Functions.vb` was invaluable for confirming the
  per-macro 380-byte layout and the MD5 hash at offset 8
- Spell / JA / auto-translate data sourced from **Windower 4**'s
  `res/*.lua` resource tables (generated to JSON at build time)
- Binary format reverse-engineered from a live FFXI install (mcr*.dat
  hex dumps) plus public knowledge of FFXI's macro layout
- UI theming modeled after the in-game **GSUI** addon

## Known gaps (deferred)

- Page title (the 16-byte obfuscated block) is preserved verbatim, not
  editable. The in-game page title editor still works fine.
- Job-abilities tab is alphabetical, not job-filtered — FFXI's resource
  data doesn't tag JAs by job. Use the search box.
- Auto-translate insertion: existing AT codes round-trip via the
  `{AT:cat/kind/id}` placeholder but the action library always inserts
  plain ASCII. FFXI accepts both, so this is fine in practice.
