// FFXI Macro Manager — MainWindow code-behind.
//
// Holds all the state for the active session:
//   - the FFXI install folder + USER folder
//   - the list of characters found
//   - the currently-loaded MacroFile (one mcr*.dat page)
//   - the currently-selected macro within that page
//
// Saves are explicit (Save page button) so the user can revert by closing
// the window if they don't like their edits. The binary writer makes a
// .bak of the original file before overwriting.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

using FFXIMacroManager.Data;
using FFXIMacroManager.Models;

namespace FFXIMacroManager
{
    public partial class MainWindow : Window
    {
        // -------- state --------
        private string _installRoot;
        private string _userFolder;
        private List<Character> _characters = new List<Character>();
        // Friendly names found in GearSwap's data folder ("Kalitzo", etc.)
        // used as the default display name when the user hasn't overridden.
        private List<string> _gearswapNames = new List<string>();
        // Persistent folder-id -> user-typed name overrides
        private Dictionary<string, string> _nameOverrides = new Dictionary<string, string>();
        private Character _activeChar;
        private BookTitles _activeBookTitles;
        private MacroFile _activeFile;
        private int _activeMacroIndex = -1;
        private MacroPageRef _activePageRef;

        // The line currently focused in the editor (for double-click insert).
        private TextBox[] _lineBoxes = new TextBox[MacroFile.LINE_COUNT];
        private int _focusedLine = 0;

        private Button[] _slotButtons = new Button[MacroFile.MACRO_COUNT];

        // When true, ALL TextChanged handlers (TxtTitle and the 6 line
        // textboxes) short-circuit instead of running CommitEditorToModel
        // / dirty-tracking. We set this true around any programmatic
        // population (SelectMacro, ClearEditor) so the cascade of
        // TextBox.Text = "..." assignments doesn't re-enter the model
        // commit path with stale buffer contents. Without it, the
        // sequence "TxtTitle.Text = m.Title -> TextChanged fires ->
        // CommitEditorToModel reads the freshly-built empty line
        // textboxes -> overwrites m.Lines with empty strings -> marks
        // every line dirty -> RenderSlotButton + UpdateDirtyLabel ->
        // can lock the UI thread" was wiping macro content on click
        // and (per user report) freezing the app.
        private bool _loading = false;

        // Where settings persist
        private static readonly string SETTINGS_PATH = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FFXIMacroManager", "settings.txt");

        public MainWindow()
        {
            InitializeComponent();

            // Hook events
            BtnPickFolder.Click  += BtnPickFolder_Click;
            BtnRenameChar.Click  += BtnRenameChar_Click;
            BtnBackup.Click      += BtnBackup_Click;
            BtnReload.Click      += (_, __) => { FlushPendingEditsToDisk(); ReloadActivePage(); };
            BtnSavePage.Click    += BtnSavePage_Click;
            BtnSnapshotLive.Click += BtnSnapshotLive_Click;
            BtnRevert.Click      += BtnRevert_Click;
            BtnClear.Click       += BtnClear_Click;

            DdCharacter.SelectionChanged += DdCharacter_SelectionChanged;
            DdJob.SelectionChanged       += DdJob_SelectionChanged;
            DdBook.SelectionChanged      += DdBookOrPage_SelectionChanged;
            DdPage.SelectionChanged      += DdBookOrPage_SelectionChanged;
            // Weapon filter — populated in PopulateWeaponDropdown after data load.
            DdWeapon.SelectionChanged    += (_, __) => RefreshLibrary();

            // Wait stepper. + and - mutate _waitSeconds in 0.5 steps;
            // the TextBox shows the value and accepts manual input on
            // lose-focus (typed values parsed in TxtWait_LostFocus).
            BtnWaitMinus.Click  += (_, __) => StepWait(-0.5);
            BtnWaitPlus.Click   += (_, __) => StepWait(+0.5);
            BtnWaitAppend.Click += BtnWaitAppend_Click;
            TxtWait.LostFocus   += TxtWait_LostFocus;
            // Ctrl+wheel on the value box also steps (UX nicety).
            TxtWait.PreviewMouseWheel += (_, e) =>
            {
                StepWait(e.Delta > 0 ? +0.5 : -0.5);
                e.Handled = true;
            };
            // Library-kind change toggles the weapon-row visibility AND
            // refreshes the listing. Order matters: visibility first so
            // any layout knock-on settles before the listbox repaints.
            DdLibKind.SelectionChanged   += DdLibKind_SelectionChanged;

            // Arrow-key cycling on every selector. Clicking a ComboBox once
            // is enough to focus it; from there Up / Down cycle through the
            // values silently without re-opening the popup. Saves a click
            // per change when sweeping through books / pages while editing.
            WireArrowKeyCycling(DdCharacter);
            WireArrowKeyCycling(DdBook);
            WireArrowKeyCycling(DdPage);
            WireArrowKeyCycling(DdJob);
            WireArrowKeyCycling(DdLibKind);   // bonus: cycle Spells/JA/WS/Targets
            WireArrowKeyCycling(DdTarget);    // bonus: cycle target tokens
            WireArrowKeyCycling(DdWeapon);    // weapon filter on the WS tab
            TxtSearch.TextChanged        += (_, __) => RefreshLibrary();
            LbLibrary.MouseDoubleClick   += LbLibrary_MouseDoubleClick;

            TxtTitle.TextChanged += TxtTitle_TextChanged;

            // Build CTRL/ALT slot buttons
            BuildSlotGrid(GridCtrl, 0, 10);
            BuildSlotGrid(GridAlt, 10, 20);

            // Initial placeholder population — gets re-filled with real book
            // names and page-exists hints once a character is selected.
            PopulateBookPage(null);
            // Job dropdown is populated after spell data loads
            Loaded += MainWindow_Loaded;
        }

        // ------------------------------------------------------------------
        // Arrow-key cycling helper
        // ------------------------------------------------------------------
        // Wire a ComboBox so Up/Down arrow keys cycle the SelectedIndex
        // silently (no popup flash) when the dropdown is closed.
        //
        // Why a PreviewKeyDown handler instead of the built-in ComboBox
        // arrow handling: WPF's default sometimes opens the popup on the
        // first arrow press and the visual flicker is annoying when the
        // user is rapidly sweeping books/pages. Catching the key in
        // PreviewKeyDown and marking it Handled lets us cycle the value
        // without involving the popup at all. When the popup IS already
        // open (user pressed F4 or Alt+Down deliberately), we step out and
        // let WPF's standard list-navigation behavior do its thing.
        //
        // Page Up / Page Down are also wired up — they jump 5 items at a
        // time, which is handy on the 20-page book selector.
        //
        // Home / End jump to the first / last entry.
        private static void WireArrowKeyCycling(ComboBox box)
        {
            box.PreviewKeyDown += (s, e) =>
            {
                if (box.IsDropDownOpen) return;        // let the open popup handle it
                if (box.Items.Count == 0) return;

                int delta = 0;
                int abs   = -1;
                if      (e.Key == Key.Down)     delta = +1;
                else if (e.Key == Key.Up)       delta = -1;
                else if (e.Key == Key.PageDown) delta = +5;
                else if (e.Key == Key.PageUp)   delta = -5;
                else if (e.Key == Key.Home)     abs   = 0;
                else if (e.Key == Key.End)      abs   = box.Items.Count - 1;
                else return;

                int next = (abs >= 0) ? abs : box.SelectedIndex + delta;
                if (next < 0)                next = 0;
                if (next >= box.Items.Count) next = box.Items.Count - 1;
                if (next != box.SelectedIndex) box.SelectedIndex = next;
                e.Handled = true;
            };
        }

        // ------------------------------------------------------------------
        // startup
        // ------------------------------------------------------------------
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Load the bundled JSON data shipped next to the .exe in data/.
            string exeDir   = AppDomain.CurrentDomain.BaseDirectory;
            string dataPath = Path.Combine(exeDir, "data");
            if (!File.Exists(Path.Combine(dataPath, "spells.json")))
            {
                // dev-build fallback: look in ../../data relative to bin/
                var alt = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "data"));
                if (File.Exists(Path.Combine(alt, "spells.json"))) dataPath = alt;
            }

            if (!SpellDb.TryLoad(dataPath))
            {
                LblStatus.Text = "Failed to load data/ folder: " + SpellDb.LoadError +
                                 ".  Action library will be empty.";
            }
            else
            {
                LblStatus.Text = "Loaded " + SpellDb.Spells.Count + " spells, " +
                                 SpellDb.Abilities.Count + " job abilities.";
            }

            PopulateJobDropdown();
            PopulateWeaponDropdown();

            // Last resort: flush on window close so an unsaved-edits +
            // close-the-app combo doesn't silently lose what the user typed.
            this.Closing += (_, __) => FlushPendingEditsToDisk();

            // Try the last-used install folder
            string saved = LoadSavedInstallPath();
            if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved))
                UseInstallFolder(saved);
        }

        // ------------------------------------------------------------------
        // build helpers
        // ------------------------------------------------------------------
        private void BuildSlotGrid(UniformGrid host, int from, int to)
        {
            host.Children.Clear();
            for (int i = from; i < to; i++)
            {
                int captured = i;
                var btn = new Button();
                btn.Style = (Style)FindResource("MacroSlotButton");
                btn.Tag = captured;
                btn.Click += (_, __) => SelectMacro(captured);
                _slotButtons[i] = btn;
                host.Children.Add(btn);
                RenderSlotButton(i);
            }
        }

        // Refills the Book and Page dropdowns.
        //
        //   - Book labels read "Book 3: WHM" when mcr.ttl has a custom name
        //     ("WHM"), or just "Book 3" for default-named books.
        //   - Page labels read "Page 1  (mcr1.dat)" with a (empty) suffix if
        //     the .dat file doesn't exist yet for that slot.
        //
        // Called with null to render placeholder labels (no character picked),
        // and re-called every time the active character changes so the names
        // refresh to that character's mcr.ttl.
        private void PopulateBookPage(Character ch)
        {
            // Remember the user's current selection so we can restore it
            // after we rebuild the items (e.g. so switching characters
            // keeps you on Book 14 / Page 1 if both exist).
            int prevBook = 1, prevPage = 1;
            try { prevBook = SelectedBook; } catch { }
            try { prevPage = SelectedPage; } catch { }

            // Detach selection handlers so the rebuild doesn't trigger
            // ten ReloadActivePage() calls mid-populate.
            DdBook.SelectionChanged -= DdBookOrPage_SelectionChanged;
            DdPage.SelectionChanged -= DdBookOrPage_SelectionChanged;

            DdBook.Items.Clear();
            for (int b = 1; b <= 20; b++)
            {
                string label;
                if (_activeBookTitles != null)
                {
                    string name = _activeBookTitles.Names[b - 1] ?? ("Book " + b);
                    // If the title we read out is already "Book N" we just
                    // show "Book N" — no redundant suffix.
                    if (string.Equals(name, "Book " + b, StringComparison.OrdinalIgnoreCase))
                        label = "Book " + b;
                    else
                        label = "Book " + b + ":  " + name;
                }
                else
                {
                    label = "Book " + b;
                }
                DdBook.Items.Add(new ComboBoxItem { Content = label, Tag = b });
            }
            int wantBook = Math.Max(1, Math.Min(20, prevBook));
            DdBook.SelectedIndex = wantBook - 1;

            DdPage.Items.Clear();
            // Live (mcr.dat) entry. FFXI uses the unnumbered mcr.dat as its
            // "currently active page" snapshot -- this is the file that
            // actually mirrors what you see in-game. The numbered files
            // (mcrN.dat) are the saved-page slots FFXI restores from when
            // you navigate. We expose Live as Tag=0 (real pages are 1-10)
            // and ReloadActivePage treats 0 as "load mcr.dat instead".
            // Without this, the manager only saw the numbered slots and
            // missed every in-game macro that hadn't been explicitly
            // copied to a numbered slot.
            {
                string liveLabel = "Live  (mcr.dat — currently active in-game)";
                if (ch != null)
                {
                    string liveFile = System.IO.Path.Combine(ch.FolderPath, "mcr.dat");
                    if (!System.IO.File.Exists(liveFile))
                        liveLabel = "Live  (mcr.dat — not present)";
                }
                DdPage.Items.Add(new ComboBoxItem { Content = liveLabel, Tag = 0 });
            }
            for (int p = 1; p <= 10; p++)
            {
                int n = (wantBook - 1) * 10 + p;
                string label = "Page " + p;
                if (ch != null)
                {
                    string file = System.IO.Path.Combine(ch.FolderPath, "mcr" + n + ".dat");
                    if (System.IO.File.Exists(file))
                        label += "    (mcr" + n + ".dat)";
                    else
                        label += "    (empty)";
                }
                DdPage.Items.Add(new ComboBoxItem { Content = label, Tag = p });
            }
            // SelectedIndex 0 = Live, 1..10 = Pages 1..10
            int wantPage = Math.Max(1, Math.Min(10, prevPage));
            DdPage.SelectedIndex = wantPage;   // shifted by 1 due to Live entry

            DdBook.SelectionChanged += DdBookOrPage_SelectionChanged;
            DdPage.SelectionChanged += DdBookOrPage_SelectionChanged;
        }

        // When the user picks a different Book, the page labels need to
        // refresh so the "(empty)" hints reflect the new book.
        private void RefreshPageLabels()
        {
            if (_activeChar == null) return;
            int book = SelectedBook;
            int prev = SelectedPage;

            DdPage.SelectionChanged -= DdBookOrPage_SelectionChanged;
            DdPage.Items.Clear();
            // Same Live entry as RefillBookAndPage -- the Live snapshot is
            // character-wide (one mcr.dat per character) so it shows on
            // every book's page list. Tag=0 routes to mcr.dat on load.
            string liveFile = System.IO.Path.Combine(_activeChar.FolderPath, "mcr.dat");
            string liveLabel = System.IO.File.Exists(liveFile)
                ? "Live  (mcr.dat — currently active in-game)"
                : "Live  (mcr.dat — not present)";
            DdPage.Items.Add(new ComboBoxItem { Content = liveLabel, Tag = 0 });
            for (int p = 1; p <= 10; p++)
            {
                int n = (book - 1) * 10 + p;
                string file = System.IO.Path.Combine(_activeChar.FolderPath, "mcr" + n + ".dat");
                string label = "Page " + p + (System.IO.File.Exists(file)
                                ? "    (mcr" + n + ".dat)"
                                : "    (empty)");
                DdPage.Items.Add(new ComboBoxItem { Content = label, Tag = p });
            }
            // Items[0]=Live, Items[1..10]=Pages 1..10; preserve prev page,
            // mapping prev=1..10 -> SelectedIndex 1..10. prev=0 (Live) keeps Live.
            DdPage.SelectedIndex = Math.Max(0, Math.Min(10, prev));
            DdPage.SelectionChanged += DdBookOrPage_SelectionChanged;
        }

        // Standard FFXI weapon-skill IDs -> human label. Source: Windower
        // res/skills.lua and BG-Wiki. Skill 0 means "uncategorized / pet
        // WS" (Aegis Schism etc.) and is grouped under "Other / pet WS".
        // Values that don't appear in weapon_skills.json are still
        // listed so the dropdown matches the broader skill table, but
        // they'll just show 0 entries when picked.
        private static readonly (int Id, string Label)[] WeaponSkillTypes = new (int, string)[]
        {
            (-1, "(all weapons)"),
            ( 1, "Hand-to-Hand"),
            ( 2, "Dagger"),
            ( 3, "Sword"),
            ( 4, "Great Sword"),
            ( 5, "Axe"),
            ( 6, "Great Axe"),
            ( 7, "Scythe"),
            ( 8, "Polearm"),
            ( 9, "Katana"),
            (10, "Great Katana"),
            (11, "Club"),
            (12, "Staff"),
            (25, "Archery (Bow)"),
            (26, "Marksmanship (Gun / Crossbow)"),
            ( 0, "Other / pet WS"),
        };

        private void PopulateWeaponDropdown()
        {
            DdWeapon.Items.Clear();
            foreach (var t in WeaponSkillTypes)
            {
                DdWeapon.Items.Add(new ComboBoxItem {
                    Content    = t.Label,
                    Tag        = t.Id,
                    IsSelected = (t.Id == -1),  // default to "(all weapons)"
                });
            }
        }

        // Returns the skill ID currently selected in the weapon filter
        // (-1 means "all weapons", 0 means "uncategorized", positive ints
        // match the `skill` field on each WeaponSkill in weapon_skills.json).
        private int SelectedWeaponSkill
        {
            get {
                var it = DdWeapon == null ? null : DdWeapon.SelectedItem as ComboBoxItem;
                return it == null ? -1 : (int)it.Tag;
            }
        }

        // Show/hide the Weapon: row depending on whether the user is
        // looking at the Weapon Skills tab. When hidden, the row's grid
        // collapses to 0 px so the listbox below claims that space.
        private void DdLibKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool isWs = (DdLibKind.SelectedIndex == 2);
            if (WeaponFilterRow != null)
                WeaponFilterRow.Visibility = isWs ? Visibility.Visible : Visibility.Collapsed;
            RefreshLibrary();
        }

        private void PopulateJobDropdown()
        {
            DdJob.Items.Clear();
            DdJob.Items.Add(new ComboBoxItem { Content = "(all jobs)", Tag = -1, IsSelected = true });

            // All 22 jobs in standard FFXI order. The filter affects BOTH
            // the Spells tab (via SpellsForJob) and the Job Abilities tab
            // (via JobAbilityMap.Belongs as of v1.2), so the physical
            // jobs (WAR/MNK/THF/BST/RNG/SAM/DRG/COR/PUP/DNC) belong here
            // even though they don't cast spells.
            int[] order = {
                 1,  2,  3,  4,  5,  6,  7,  8,  9, 10,   // WAR..BRD
                11, 12, 13, 14, 15, 16, 17, 18, 19, 20,   // RNG..SCH
                21, 22                                     // GEO, RUN
            };
            foreach (var jid in order)
            {
                JobInfo j;
                if (!SpellDb.Jobs.TryGetValue(jid, out j)) continue;
                DdJob.Items.Add(new ComboBoxItem {
                    Content = j.Short + "  —  " + j.Long,
                    Tag = jid
                });
            }
        }

        // ------------------------------------------------------------------
        // install folder + character discovery
        // ------------------------------------------------------------------
        private void BtnPickFolder_Click(object sender, RoutedEventArgs e)
        {
            // We use the WinForms folder dialog (no extra dependencies on
            // OpenFileDialog hacks).
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Pick your FINAL FANTASY XI folder (the one containing USER\\)";
                dlg.SelectedPath = _installRoot ?? "";
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    UseInstallFolder(dlg.SelectedPath);
                }
            }
        }

        private void UseInstallFolder(string path)
        {
            string user = Character.ResolveUserFolder(path);
            if (user == null)
            {
                MessageBox.Show(this,
                    "No FFXI characters found under:\n" + path + "\n\n" +
                    "Pick the folder that contains \"USER\\\" — typically:\n" +
                    "  <drive>:\\...\\SquareEnix\\FINAL FANTASY XI",
                    "FFXI Macro Manager",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _installRoot = path;
            _userFolder  = user;
            LblInstall.Text = user;
            SaveInstallPath(path);

            // FFXI itself doesn't store the human-readable character name
            // anywhere on disk (we verified — names are server-side and
            // only resolved when logged in). So we ask the user to name
            // each folder once via the Rename button, and persist that to
            // %APPDATA%\FFXIMacroManager\characters.txt.
            //
            // As a *suggestion only*, if Windower happens to be installed
            // alongside this FFXI install, we look at its GearSwap data
            // folder for files like "Kalitzo_blm.lua" and offer those
            // names as defaults in the Rename dialog. Not a dependency —
            // the app works fine with no Windower around.
            _nameOverrides = Character.LoadNameOverrides();
            string gsData = Character.FindGearSwapDataDir(path);
            _gearswapNames = Character.ScanGearSwapNames(gsData);

            _characters = Character.EnumerateAt(user);

            RenderCharacterDropdown();
            if (DdCharacter.Items.Count > 0)
                DdCharacter.SelectedIndex = 0;
            else
                LblStatus.Text = "No characters with macro files found under " + user;
        }

        // Re-emit the character dropdown's labels using whatever we know
        // about each folder right now: display name (or "(unnamed)"), folder
        // id, and last-played mtime. Called when characters are first
        // discovered AND after Rename so the dropdown stays in sync.
        private void RenderCharacterDropdown()
        {
            int prev = DdCharacter.SelectedIndex;
            DdCharacter.SelectionChanged -= DdCharacter_SelectionChanged;
            DdCharacter.Items.Clear();
            foreach (var c in _characters)
            {
                c.DisplayName = NameFor(c);
                bool hasName = !string.Equals(c.DisplayName, c.FolderId, StringComparison.OrdinalIgnoreCase);
                string label;
                if (hasName)
                    label = c.DisplayName + "    (id " + c.FolderId + ")";
                else
                    label = "(unnamed)    id " + c.FolderId;
                if (c.LastPlayed > DateTime.MinValue)
                    label += "    last played " + c.LastPlayed.ToString("yyyy-MM-dd HH:mm");
                DdCharacter.Items.Add(new ComboBoxItem { Content = label, Tag = c });
            }
            if (prev >= 0 && prev < DdCharacter.Items.Count) DdCharacter.SelectedIndex = prev;
            DdCharacter.SelectionChanged += DdCharacter_SelectionChanged;
        }

        private string NameFor(Character c)
        {
            string name;
            if (_nameOverrides.TryGetValue(c.FolderId, out name) && !string.IsNullOrWhiteSpace(name))
                return name;
            return c.FolderId;
        }

        // Prompt the user for a friendly name for the currently-selected
        // character. Persisted to %APPDATA%\FFXIMacroManager\characters.txt
        // so the name sticks across runs.
        // -----------------------------------------------------------------
        // BACK UP MACROS
        // -----------------------------------------------------------------
        // Snapshots every "mcr*" file (mcr.dat / mcr1.dat .. mcr199.dat /
        // mcr.sys / mcr.ttl / any .bak that's already there) for the
        // currently-selected character into a timestamped folder under
        // %USERPROFILE%\Documents\FFXIMacroManager_backups\. Non-destructive
        // -- the original files in the FFXI install folder are untouched.
        //
        // Recommended workflow:
        //   1. In FFXI: Macros menu -> Macro List -> Save All to Server
        //      (covers worst-case "the whole client folder is toast")
        //   2. In this app: click "Back up..." (snapshot to disk, fast)
        //   3. Edit + Save Page as usual
        //
        // If the user has never picked a character, we just complain and
        // bail -- no install folder = no character folder = nothing to do.
        private void BtnBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_activeChar == null)
            {
                LblStatus.Text = "Pick a character before backing up.";
                return;
            }
            string src = _activeChar.FolderPath;
            if (string.IsNullOrEmpty(src) || !Directory.Exists(src))
            {
                LblStatus.Text = "Character folder is missing: " + src;
                return;
            }

            // Sanitise the display name so it's safe to put in a path.
            string charLabel = NameFor(_activeChar) ?? _activeChar.FolderId ?? "char";
            foreach (char bad in Path.GetInvalidFileNameChars())
                charLabel = charLabel.Replace(bad, '_');

            string ts = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string destRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "FFXIMacroManager_backups");
            string dest = Path.Combine(destRoot,
                charLabel + "_" + _activeChar.FolderId + "_" + ts);

            try
            {
                Directory.CreateDirectory(dest);
                int copied = 0;
                long totalBytes = 0;
                foreach (var f in Directory.GetFiles(src))
                {
                    string name = Path.GetFileName(f);
                    // Pull every macro-related file: mcr.dat, mcrN.dat,
                    // mcr.sys (system flags), mcr.ttl (book titles),
                    // any *.bak the editor itself created.
                    if (name.StartsWith("mcr", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(f, Path.Combine(dest, name), true);
                        copied++;
                        totalBytes += new FileInfo(f).Length;
                    }
                }
                LblStatus.Text = string.Format(
                    "Backed up {0} file(s), {1:N0} bytes to {2}",
                    copied, totalBytes, dest);

                var ans = MessageBox.Show(this,
                    string.Format(
                        "Backed up {0} macro file(s) for {1}.\n\nDestination:\n{2}\n\nOpen the backup folder in Explorer?",
                        copied, charLabel, dest),
                    "Backup complete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (ans == MessageBoxResult.Yes)
                {
                    // UseShellExecute=true is required to launch Explorer
                    // on a directory path with the default associations.
                    try
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo
                            {
                                FileName        = dest,
                                UseShellExecute = true,
                            });
                    }
                    catch (Exception openEx)
                    {
                        LblStatus.Text = "Backup saved, but couldn't open Explorer: " + openEx.Message;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Backup FAILED:\n\n" + ex.Message,
                    "FFXI Macro Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                LblStatus.Text = "Backup failed: " + ex.Message;
            }
        }

        private void BtnRenameChar_Click(object sender, RoutedEventArgs e)
        {
            var item = DdCharacter.SelectedItem as ComboBoxItem;
            var c = item == null ? null : item.Tag as Character;
            if (c == null) { LblStatus.Text = "Pick a character first."; return; }

            string current = NameFor(c);
            string suggested = current;
            if (string.Equals(suggested, c.FolderId, StringComparison.OrdinalIgnoreCase)
                && _gearswapNames.Count > 0) suggested = _gearswapNames[0];

            string answer = PromptForName(
                "Enter the FFXI character name for this folder.\n\n" +
                "Folder ID: " + c.FolderId + "\n" +
                (_gearswapNames.Count > 0
                    ? "GearSwap suggests: " + string.Join(", ", _gearswapNames.ToArray())
                    : "No GearSwap names detected."),
                suggested);
            if (answer == null) return;
            answer = answer.Trim();
            if (string.IsNullOrEmpty(answer)) answer = c.FolderId;

            _nameOverrides[c.FolderId] = answer;
            Character.SaveNameOverride(c.FolderId, answer);
            RenderCharacterDropdown();
            LblStatus.Text = "Renamed folder " + c.FolderId + " -> " + answer;
        }

        // Simple modal text-input dialog. We build it on the fly rather than
        // shipping a dedicated XAML window since this is the only prompt in
        // the app.
        private string PromptForName(string question, string defaultText)
        {
            var dlg = new Window {
                Title = "Set character name",
                Width = 440, Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("BgDeep"),
            };
            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock {
                Text = question,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextMain"),
                Margin = new Thickness(0,0,0,10),
            });
            var tb = new TextBox { Text = defaultText ?? "", FontSize = 14 };
            sp.Children.Add(tb);
            var btns = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0,12,0,0),
            };
            string result = null;
            var ok = new Button { Content = "Save", Padding = new Thickness(16,5,16,5), Margin = new Thickness(0,0,6,0) };
            ok.Style = (Style)FindResource("AccentButton");
            ok.Click += (_, __) => { result = tb.Text; dlg.Close(); };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14,5,14,5) };
            cancel.Click += (_, __) => { result = null; dlg.Close(); };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            sp.Children.Add(btns);
            dlg.Content = sp;
            tb.Focus();
            tb.SelectAll();
            dlg.ShowDialog();
            return result;
        }

        // ------------------------------------------------------------------
        // selection: character / book / page
        // ------------------------------------------------------------------
        private void DdCharacter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SAFETY: flush any pending edits on the OUTGOING character
            // before switching. See FlushPendingEditsToDisk's comment.
            FlushPendingEditsToDisk();

            var item = DdCharacter.SelectedItem as ComboBoxItem;
            _activeChar = item == null ? null : item.Tag as Character;

            // Pull this character's in-game book names from mcr.ttl so the
            // Book dropdown reads "Book 3:  WHM" etc.  Falls back to plain
            // "Book N" labels if mcr.ttl is missing or unreadable.
            _activeBookTitles = _activeChar == null
                ? null
                : BookTitles.LoadOrDefault(_activeChar.FolderPath);
            PopulateBookPage(_activeChar);

            ReloadActivePage();

            // If this character has never been given a friendly name, prompt
            // now so users discover the feature without reading the README.
            // Skipped if the user already clicked into the dropdown to
            // navigate, just on the first pick of an unnamed character.
            if (_activeChar != null
                && !_nameOverrides.ContainsKey(_activeChar.FolderId)
                && !_promptedForName.Contains(_activeChar.FolderId))
            {
                _promptedForName.Add(_activeChar.FolderId);
                Dispatcher.BeginInvoke(new Action(PromptCharacterName));
            }
        }

        // Tracks folder IDs we've already auto-prompted for so we don't
        // pester the user every time they re-select an unnamed folder.
        private HashSet<string> _promptedForName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Same flow as Rename button but only triggered automatically on
        // first selection of an unnamed character. Cancelling is OK — the
        // user can still rename later via the button.
        private void PromptCharacterName()
        {
            if (_activeChar == null) return;
            string suggested = _gearswapNames.Count > 0 ? _gearswapNames[0] : "";

            string body =
                "What's the in-game name for this character?\n\n" +
                "Folder ID: " + _activeChar.FolderId + "\n" +
                "FFXI itself doesn't store the character name on disk, so " +
                "you'll need to type it in just once. The name is saved to " +
                "%AppData%\\FFXIMacroManager\\characters.txt and will stick.";
            if (_gearswapNames.Count > 0)
                body += "\n\nWindower detected; GearSwap suggests: " +
                        string.Join(", ", _gearswapNames.ToArray());

            string answer = PromptForName(body, suggested);
            if (string.IsNullOrWhiteSpace(answer)) return; // user cancelled
            answer = answer.Trim();
            _nameOverrides[_activeChar.FolderId] = answer;
            Character.SaveNameOverride(_activeChar.FolderId, answer);
            RenderCharacterDropdown();
        }

        private void DdJob_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshLibrary();
        }

        private void DdBookOrPage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SAFETY: commit + flush any unsaved edits on the OUTGOING page
            // before we tear the editor down. Without this, typing into a
            // slot and then clicking the book/page dropdown silently wipes
            // every typed line -- when the user comes back and hits Save
            // Page, the empty editor state gets written to disk (looks like
            // "my macros aren't saving" from the user's perspective).
            FlushPendingEditsToDisk();

            // If the book changed, refresh the page labels so the "(empty)"
            // hints reflect the new book's pages.
            if (sender == DdBook) RefreshPageLabels();
            ReloadActivePage();
        }

        // Commits the editor's textboxes into the model and, if anything
        // is actually dirty, writes the file to disk. No-op when there's no
        // active file or no edits. Used by every code path that's about to
        // throw away the current editor state (page nav, character switch,
        // reload, window close, etc.) so user edits can never silently die.
        private void FlushPendingEditsToDisk()
        {
            if (_activeFile == null || _activeMacroIndex < 0) return;
            CommitEditorToModel();
            // Only write if SOMETHING is dirty -- saving a fully-clean file
            // is harmless (byte-perfect round-trip) but wastes a disk write
            // and bumps mtime, which makes debugging "which page did I last
            // touch?" harder.
            bool anyDirty = false;
            foreach (var m in _activeFile.Macros)
                foreach (var ln in m.Lines) if (ln.Dirty) { anyDirty = true; break; }
            if (!anyDirty) return;
            try
            {
                _activeFile.Save();
                LblStatus.Text = "Auto-saved " + _activeFile.SourcePath
                               + "  (page change flushed pending edits)";
                MarkAllClean();
            }
            catch (Exception ex)
            {
                // Surface the failure but DON'T pop a modal -- the user
                // is mid-navigation and a blocking dialog mid-click breaks
                // the flow worse than the silent-loss bug we're fixing.
                LblStatus.Text = "Auto-save failed on page change: " + ex.Message;
            }
        }

        private int SelectedBook
        {
            get {
                var it = DdBook.SelectedItem as ComboBoxItem;
                return it == null ? 1 : (int)it.Tag;
            }
        }
        private int SelectedPage
        {
            get {
                var it = DdPage.SelectedItem as ComboBoxItem;
                return it == null ? 1 : (int)it.Tag;
            }
        }
        private int SelectedJobId
        {
            get {
                var it = DdJob.SelectedItem as ComboBoxItem;
                return it == null ? -1 : (int)it.Tag;
            }
        }

        // ------------------------------------------------------------------
        // page load / save
        // ------------------------------------------------------------------
        private void ReloadActivePage()
        {
            _activeFile = null;
            _activeMacroIndex = -1;
            _activePageRef = null;
            ClearEditor();

            if (_activeChar == null) {
                LblPageTitle.Text = "Macro Hotbar";
                RenderAllSlots();
                return;
            }

            // SelectedPage == 0 is the "Live" sentinel -- load mcr.dat
            // (FFXI's currently active in-game page). Otherwise use the
            // normal (book-1)*10+page mapping for the numbered slot file.
            string file;
            int n;
            if (SelectedPage == 0)
            {
                n = 0;
                file = Path.Combine(_activeChar.FolderPath, "mcr.dat");
            }
            else
            {
                n = (SelectedBook - 1) * 10 + SelectedPage;
                file = Path.Combine(_activeChar.FolderPath, "mcr" + n + ".dat");
            }
            _activePageRef = new MacroPageRef {
                Book = SelectedBook, Page = SelectedPage, FileNumber = n,
                Path = file, Exists = File.Exists(file)
            };
            LblPageTitle.Text = SelectedPage == 0
                ? string.Format("Live page ({0}) — currently active in-game", Path.GetFileName(file))
                : string.Format("Book {0} / Page {1}  ({2})",
                                  SelectedBook, SelectedPage, Path.GetFileName(file));

            if (!_activePageRef.Exists)
            {
                LblStatus.Text = file + " — page is empty (file does not exist yet).";
                RenderAllSlots();
                return;
            }

            try
            {
                _activeFile = MacroFile.Load(file);
                LblStatus.Text = "Loaded " + file;
            }
            catch (Exception ex)
            {
                _activeFile = null;
                LblStatus.Text = "Failed to load " + file + ": " + ex.Message;
            }

            RenderAllSlots();
        }

        // Copy mcr.dat (FFXI's live "currently active in-game page") into the
        // currently-selected numbered Book/Page slot. Use case: user has
        // their real in-game macros in mcr.dat but the numbered slot
        // (mcrN.dat) is stale or empty -- one click parks the live state
        // into a slot the manager can navigate to. Refuses to overwrite if
        // the user has the Live page itself selected (it'd be a no-op
        // anyway -- you'd be copying mcr.dat onto mcr.dat). Always writes a
        // .bak backup of the destination before overwriting.
        private void BtnSnapshotLive_Click(object sender, RoutedEventArgs e)
        {
            if (_activeChar == null)
            {
                LblStatus.Text = "No character selected.";
                return;
            }
            if (SelectedPage == 0)
            {
                LblStatus.Text = "Snapshot Live is a no-op when the Live view is selected. Pick a numbered Page first.";
                return;
            }
            string liveFile = Path.Combine(_activeChar.FolderPath, "mcr.dat");
            if (!File.Exists(liveFile))
            {
                LblStatus.Text = "mcr.dat not found -- FFXI hasn't written a live snapshot yet. /logout in-game first.";
                return;
            }
            int n = (SelectedBook - 1) * 10 + SelectedPage;
            string destFile = Path.Combine(_activeChar.FolderPath, "mcr" + n + ".dat");

            // Backup existing destination first so a misclick is recoverable.
            try
            {
                if (File.Exists(destFile))
                {
                    string bak = destFile + ".snapshot-bak";
                    File.Copy(destFile, bak, overwrite: true);
                }
                File.Copy(liveFile, destFile, overwrite: true);
                LblStatus.Text = "Snapshot Live -> " + Path.GetFileName(destFile)
                    + " complete. (Previous content backed up to " + Path.GetFileName(destFile) + ".snapshot-bak.)";
                // Reload the now-fresh page so the editor reflects what we copied.
                ReloadActivePage();
                // Page labels need to refresh too -- destination went from (empty)
                // to populated, or its filename hint should re-render.
                RefreshPageLabels();
            }
            catch (Exception ex)
            {
                LblStatus.Text = "Snapshot Live failed: " + ex.Message;
            }
        }

        private void BtnSavePage_Click(object sender, RoutedEventArgs e)
        {
            if (_activeFile == null)
            {
                LblStatus.Text = "No page loaded.";
                return;
            }
            // Commit the active editor's content into the model first
            CommitEditorToModel();
            try
            {
                _activeFile.Save();
                LblStatus.Text = "Saved " + _activeFile.SourcePath +
                                 "  (backup: " + Path.GetFileName(_activeFile.SourcePath) + ".bak)";
                MarkAllClean();
                RenderAllSlots();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save failed:\n\n" + ex.Message,
                                "FFXI Macro Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ------------------------------------------------------------------
        // slot rendering
        // ------------------------------------------------------------------
        private void RenderAllSlots()
        {
            for (int i = 0; i < MacroFile.MACRO_COUNT; i++) RenderSlotButton(i);

            // If the ALT row is entirely empty on this page, show a clear
            // hint so the user doesn't think it's a parser bug. FFXI only
            // writes ALT-row macros if the player has actually saved them
            // on a particular page; lots of pages legitimately only use
            // CTRL row.
            string hint = "";
            if (_activeFile != null)
            {
                bool altEmpty = true;
                for (int i = 10; i < 20 && i < _activeFile.Macros.Count; i++)
                {
                    if (!_activeFile.Macros[i].IsEmpty) { altEmpty = false; break; }
                }
                if (altEmpty)
                    hint = "no ALT macros saved on this page in FFXI " +
                           "(this is normal — many pages only use CTRL row)";
            }
            LblAltHint.Text = hint;

            // NOTE: we deliberately do NOT call SelectMacro(_activeMacroIndex)
            // here. The earlier "reselect previous" call caused a recursive
            // loop (SelectMacro -> RenderAllSlots -> SelectMacro), and on
            // the recursive entry CommitEditorToModel saw a freshly-built
            // empty editor and overwrote the macro model with empty
            // strings. Net effect: clicking any populated slot wiped its
            // content. Slot highlight (active vs idle) is rendered by
            // RenderSlotButton based on _activeMacroIndex directly, so no
            // SelectMacro call is needed to keep the visuals in sync.
        }

        private void RenderSlotButton(int idx)
        {
            var btn = _slotButtons[idx];
            if (btn == null) return;

            string title = "";
            string preview = "";
            bool dirty = false;
            bool exists = (_activeFile != null);

            if (_activeFile != null && idx < _activeFile.Macros.Count)
            {
                var m = _activeFile.Macros[idx];
                title = m.Title ?? "";
                foreach (var ln in m.Lines)
                {
                    if (!string.IsNullOrEmpty(ln.Text))
                    {
                        preview = ln.Text;
                        break;
                    }
                    if (ln.Dirty) dirty = true;
                }
                foreach (var ln in m.Lines) if (ln.Dirty) { dirty = true; break; }
                if (string.IsNullOrEmpty(title) && m.IsEmpty) title = "(empty)";
            }
            else
            {
                title = "(empty)";
            }

            // Hotkey label is fixed: CTRL+1..0 / ALT+1..0
            int n = idx < 10 ? idx + 1 : idx - 9;
            string mod = idx < 10 ? "CTRL" : "ALT";
            string slot = (n == 10) ? "0" : n.ToString();
            string hk = mod + "+" + slot;

            // Compose 3-row content: hotkey / title / preview
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock {
                Text = hk,
                FontSize = 10,
                Foreground = (Brush)FindResource("TextDim"),
            });
            sp.Children.Add(new TextBlock {
                Text = string.IsNullOrEmpty(title) ? " " : title,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = exists ? (Brush)FindResource("TextMain") : (Brush)FindResource("TextDim"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            sp.Children.Add(new TextBlock {
                Text = string.IsNullOrEmpty(preview) ? " " : preview,
                FontSize = 10,
                Foreground = (Brush)FindResource("TextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            btn.Content = sp;
            btn.BorderBrush = (idx == _activeMacroIndex)
                ? (Brush)FindResource("Accent")
                : (dirty ? (Brush)FindResource("Red") : (Brush)FindResource("Border1"));
            btn.Background = (idx == _activeMacroIndex)
                ? (Brush)FindResource("BgRowSel")
                : (Brush)FindResource("BgRow");
        }

        // ------------------------------------------------------------------
        // macro selection / editor
        // ------------------------------------------------------------------
        private void SelectMacro(int idx)
        {
            // Wrap the whole population in try/catch + a top-level
            // _loading flag. The flag is the important part — see the
            // field comment above. The try/catch is a safety net so any
            // unexpected exception surfaces as a chat-bar message instead
            // of silently freezing or crashing the app.
            try
            {
                // Commit the OUTGOING slot's edits (still active until we
                // overwrite _activeMacroIndex below). Must happen BEFORE
                // _loading flips, since real edits should be saved.
                CommitEditorToModel();

                _loading = true;
                _activeMacroIndex = idx;
                BuildLineEditor();      // creates fresh empty textboxes
                RenderAllSlots();       // re-highlight selection

                if (_activeFile == null || idx < 0 || idx >= _activeFile.Macros.Count)
                {
                    LblEditTitle.Text = "No macro selected";
                    TxtTitle.Text = "";
                    LblDirty.Text  = "";
                    return;
                }

                var m = _activeFile.Macros[idx];
                LblEditTitle.Text = "Editing " + m.HotkeyLabel + "  ·  slot #" + (idx + 1);
                TxtTitle.Text     = m.Title ?? "";

                for (int i = 0; i < MacroFile.LINE_COUNT && i < m.Lines.Count; i++)
                {
                    _lineBoxes[i].Text = m.Lines[i].Text ?? "";
                    _lineBoxes[i].Tag  = false;   // not user-typed
                }
            }
            catch (Exception ex)
            {
                LblStatus.Text = "SelectMacro(" + idx + ") failed: " + ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                _loading = false;
                UpdateDirtyLabel();
            }
        }

        private void BuildLineEditor()
        {
            LineEditorHost.Children.Clear();
            for (int i = 0; i < MacroFile.LINE_COUNT; i++)
            {
                int captured = i;
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var lbl = new TextBlock {
                    Text = "Line " + (i + 1) + ":",
                    Width = 60,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("TextMuted"),
                };
                Grid.SetColumn(lbl, 0);
                row.Children.Add(lbl);

                var tb = new TextBox();
                tb.MaxLength = 60; // 61-byte slot minus null terminator
                tb.GotFocus += (_, __) => _focusedLine = captured;
                tb.TextChanged += (_, __) =>
                {
                    // Suppress during programmatic population (SelectMacro
                    // / ClearEditor sets _lineBoxes[i].Text from m.Lines,
                    // which fires this handler — but those changes aren't
                    // user edits and shouldn't mark the line dirty).
                    if (_loading) return;
                    _lineBoxes[captured].Tag = true;
                    UpdateDirtyLabel();
                };
                Grid.SetColumn(tb, 1);
                row.Children.Add(tb);

                _lineBoxes[i] = tb;
                LineEditorHost.Children.Add(row);
            }
        }

        private void ClearEditor()
        {
            try
            {
                _loading = true;
                BuildLineEditor();
                TxtTitle.Text = "";
                LblEditTitle.Text = "No macro selected";
                LblDirty.Text = "";
            }
            finally { _loading = false; }
        }

        private void TxtTitle_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Suppress during programmatic population. This is THE handler
            // that was causing the freeze/data-wipe on slot click: when
            // SelectMacro did `TxtTitle.Text = m.Title`, this fired and
            // called CommitEditorToModel against the freshly-rebuilt
            // empty line textboxes, which wrote empty strings back into
            // m.Lines. The whole cascade now no-ops while _loading is true.
            if (_loading) return;

            CommitEditorToModel();
            if (_activeMacroIndex >= 0) RenderSlotButton(_activeMacroIndex);
            UpdateDirtyLabel();
        }

        // Copy editor textboxes into the macro model + mark dirty lines.
        private void CommitEditorToModel()
        {
            if (_activeFile == null || _activeMacroIndex < 0) return;
            if (_activeMacroIndex >= _activeFile.Macros.Count) return;
            var m = _activeFile.Macros[_activeMacroIndex];

            // Title
            string newTitle = TxtTitle.Text ?? "";
            if (newTitle != (m.Title ?? "")) m.Title = newTitle;

            // Lines
            for (int i = 0; i < MacroFile.LINE_COUNT && i < m.Lines.Count; i++)
            {
                var box = _lineBoxes[i];
                if (box == null) continue;
                string newText = box.Text ?? "";
                if (newText != (m.Lines[i].Text ?? ""))
                {
                    m.Lines[i].Text = newText;
                    m.Lines[i].Dirty = true;
                }
            }
        }

        private void BtnRevert_Click(object sender, RoutedEventArgs e)
        {
            if (_activeFile == null || _activeMacroIndex < 0) return;
            // Re-load just this page from disk and re-pick the same macro.
            int idx = _activeMacroIndex;
            ReloadActivePage();
            if (idx < MacroFile.MACRO_COUNT) SelectMacro(idx);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (_activeFile == null || _activeMacroIndex < 0) return;
            TxtTitle.Text = "";
            for (int i = 0; i < _lineBoxes.Length; i++) _lineBoxes[i].Text = "";
        }

        private void UpdateDirtyLabel()
        {
            if (_activeFile == null) { LblDirty.Text = ""; return; }

            int dirty = 0;
            foreach (var m in _activeFile.Macros)
                foreach (var ln in m.Lines)
                    if (ln.Dirty) dirty++;
            LblDirty.Text = dirty > 0
                ? string.Format("{0} unsaved line edit{1} — click 'Save page' to write to disk", dirty, dirty == 1 ? "" : "s")
                : "";
        }

        private void MarkAllClean()
        {
            if (_activeFile == null) return;
            foreach (var m in _activeFile.Macros)
                foreach (var ln in m.Lines)
                    ln.Dirty = false;
            LblDirty.Text = "";
        }

        // ------------------------------------------------------------------
        // library pane
        // ------------------------------------------------------------------
        private string SelectedTargetToken()
        {
            var it = DdTarget == null ? null : DdTarget.SelectedItem as ComboBoxItem;
            return it == null ? "<t>" : (it.Tag as string ?? "<t>");
        }

        // ------------------------------------------------------------------
        // Wait stepper (appends " <wait N>" to a line)
        // ------------------------------------------------------------------
        // 0 = no wait appended. Stored as double so 0.5-step UI works
        // and decimal values round-trip cleanly to the macro file.
        private double _waitSeconds = 0.0;
        private const double WaitMax = 30.0;

        // Format a wait value the way an FFXI macro should see it:
        //   2.0 -> "2"   (no trailing .0)
        //   2.5 -> "2.5"
        //   0.5 -> "0.5"
        // "0.##" trims trailing zeros automatically and the invariant
        // culture stops a "," from being inserted in locales that use
        // comma as the decimal separator.
        private static string FormatWait(double seconds)
        {
            return seconds.ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        // Returns " <wait N>" or "" if no wait is set.
        // Always prefixed with a single space so it appends cleanly to
        // an existing line; caller doesn't add its own space.
        private string WaitSuffix()
        {
            if (_waitSeconds <= 0.0) return "";
            return " <wait " + FormatWait(_waitSeconds) + ">";
        }

        // Adjust the wait by delta and refresh the textbox. Clamps to
        // [0, WaitMax]. Snaps to 0.5 grid to keep the display clean.
        private void StepWait(double delta)
        {
            double v = _waitSeconds + delta;
            // snap to nearest 0.5
            v = System.Math.Round(v * 2.0) / 2.0;
            if (v < 0.0)      v = 0.0;
            if (v > WaitMax)  v = WaitMax;
            _waitSeconds = v;
            TxtWait.Text = FormatWait(v);
        }

        // Parse the textbox after the user types a value and tabs away.
        // Accepts integers, decimals, and rejects junk (resets to last value).
        private void TxtWait_LostFocus(object sender, RoutedEventArgs e)
        {
            double parsed;
            string txt = (TxtWait.Text ?? "").Trim();
            if (double.TryParse(txt,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out parsed))
            {
                if (parsed < 0.0)     parsed = 0.0;
                if (parsed > WaitMax) parsed = WaitMax;
                _waitSeconds = parsed;
            }
            // Re-format either way so "1.0" becomes "1" etc.
            TxtWait.Text = FormatWait(_waitSeconds);
        }

        // Append " <wait N>" to whatever line is currently focused. Used
        // by the "Append to line" button so the user can add a wait to a
        // line they typed by hand (like the /equipset lines that don't
        // come from double-click insert).
        private void BtnWaitAppend_Click(object sender, RoutedEventArgs e)
        {
            string suffix = WaitSuffix();
            if (string.IsNullOrEmpty(suffix))
            {
                LblStatus.Text = "Wait is 0 — nothing appended. Use + to set a wait time first.";
                return;
            }
            if (_focusedLine < 0 || _focusedLine >= _lineBoxes.Length) _focusedLine = 0;
            var box = _lineBoxes[_focusedLine];
            if (box == null) return;
            // Skip if the line already ends with the same suffix (avoids
            // double-tap producing "<wait 1> <wait 1>").
            string current = box.Text ?? "";
            if (current.EndsWith(suffix.TrimStart())) return;
            box.Text = current + suffix;
            box.Focus();
            box.CaretIndex = box.Text.Length;
        }

        // Build the right-hand macro fragment that's pasted into a line:
        //   "/ma \"Cure III\" <stal> <wait 2>"
        // Target token is whatever the Target dropdown shows; wait suffix
        // is appended when the Wait stepper is non-zero.
        private string ComposeCommand(string prefix, string actionName)
        {
            string target = SelectedTargetToken();
            string head = prefix + " \"" + actionName + "\"";
            string body = string.IsNullOrEmpty(target) ? head : (head + " " + target);
            return body + WaitSuffix();
        }

        private void RefreshLibrary()
        {
            LbLibrary.Items.Clear();
            if (!SpellDb.Loaded) return;

            string filter = (TxtSearch.Text ?? "").Trim().ToLowerInvariant();
            int kind = DdLibKind.SelectedIndex;
            int jobId = SelectedJobId;

            if (kind == 0) // spells
            {
                LblLibHint.Text = "Double-click a spell to insert it with the selected target.";
                List<Spell> list;
                if (jobId > 0) list = SpellDb.SpellsForJob(jobId);
                else
                {
                    list = new List<Spell>();
                    foreach (var s in SpellDb.Spells.Values)
                        if (s.IsRealSpell()) list.Add(s);
                    list.Sort(delegate(Spell a, Spell b) {
                        return string.Compare(a.En, b.En, StringComparison.OrdinalIgnoreCase);
                    });
                }
                foreach (var s in list)
                {
                    if (filter.Length > 0 && s.En.ToLowerInvariant().IndexOf(filter) < 0) continue;
                    string label;
                    if (jobId > 0)
                        label = string.Format("Lv {0,3}   {1}   ({2})", s.LearnLevelFor(jobId), s.En, s.Type);
                    else
                        label = string.Format("{0}   ({1})", s.En, s.Type);

                    // Prefix selection: spells/songs/ninjutsu/blue/summon/geo all macro as /ma
                    string pre = s.Prefix;
                    if (pre == "/magic" || pre == "/song" || pre == "/ninjutsu" ||
                        pre == "/summon" || pre == "/bluemagic" || pre == "/geomancy" || pre == "/trust")
                        pre = "/ma";
                    if (string.IsNullOrEmpty(pre)) pre = "/ma";

                    LbLibrary.Items.Add(new ListBoxItem {
                        Content = label,
                        Tag = new InsertSpec { Prefix = pre, Name = s.En },
                    });
                }
            }
            else if (kind == 1) // job abilities
            {
                LblLibHint.Text = "Double-click a job ability to insert it with the selected target.";
                var list = SpellDb.AbilitiesFor(jobId);
                foreach (var a in list)
                {
                    if (filter.Length > 0 && a.En.ToLowerInvariant().IndexOf(filter) < 0) continue;
                    string pre = a.Prefix;
                    if (pre == "/jobability") pre = "/ja";
                    if (string.IsNullOrEmpty(pre)) pre = "/ja";
                    LbLibrary.Items.Add(new ListBoxItem {
                        Content = a.En + "   (" + a.Type + ")",
                        Tag = new InsertSpec { Prefix = pre, Name = a.En },
                    });
                }
            }
            else if (kind == 2) // weapon skills
            {
                int weaponId = SelectedWeaponSkill;
                LblLibHint.Text = weaponId > 0
                    ? "Double-click a weapon skill to insert it with the selected target. (Filtered by weapon.)"
                    : "Double-click a weapon skill to insert it with the selected target.";
                var list = SpellDb.WeaponSkillsFor(jobId);
                foreach (var w in list)
                {
                    if (filter.Length > 0 && w.En.ToLowerInvariant().IndexOf(filter) < 0) continue;
                    if (weaponId >= 0 && w.Skill != weaponId) continue;
                    LbLibrary.Items.Add(new ListBoxItem {
                        Content = w.En,
                        Tag = new InsertSpec { Prefix = "/ws", Name = w.En },
                    });
                }
            }
            else // targets / snippets — these APPEND to the current line
            {
                LblLibHint.Text = "Double-click to append this token to the current line.";
                string[][] snippets = new string[][] {
                    new string[]{ "<me> — self",                  " <me>"      },
                    new string[]{ "<t> — current target",         " <t>"       },
                    new string[]{ "<bt> — battle target",         " <bt>"      },
                    new string[]{ "<lastst> — last sub-target",   " <lastst>"  },
                    new string[]{ "<stnpc> — sub-target NPC",     " <stnpc>"   },
                    new string[]{ "<stpc>  — sub-target PC",      " <stpc>"    },
                    new string[]{ "<stal>  — sub-target ally",    " <stal>"    },
                    new string[]{ "<p0> — party slot 0 (you)",    " <p0>"      },
                    new string[]{ "<p1> — party slot 1",          " <p1>"      },
                    new string[]{ "<p2> — party slot 2",          " <p2>"      },
                    new string[]{ "<wait 1>",                     " <wait 1>"  },
                    new string[]{ "<wait 2>",                     " <wait 2>"  },
                    new string[]{ "<wait 3>",                     " <wait 3>"  },
                    new string[]{ "/echo Ready",                  "/echo Ready"},
                    new string[]{ "/console gs c set ...",        "/console gs c set " },
                    new string[]{ "/equip main \"...\"",          "/equip main \"\"" },
                };
                foreach (var s in snippets)
                {
                    if (filter.Length > 0 && s[0].ToLowerInvariant().IndexOf(filter) < 0) continue;
                    LbLibrary.Items.Add(new ListBoxItem { Content = s[0], Tag = s[1] });
                }
            }
        }

        // Tag payload for a library row that becomes a full macro line on
        // double-click.  We carry the prefix + action name and join them
        // with the user's currently-selected target at insert time.
        private sealed class InsertSpec
        {
            public string Prefix = "/ma";
            public string Name   = "";
        }

        private void LbLibrary_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var item = LbLibrary.SelectedItem as ListBoxItem;
            if (item == null) return;

            if (_focusedLine < 0 || _focusedLine >= _lineBoxes.Length) _focusedLine = 0;
            var box = _lineBoxes[_focusedLine];
            if (box == null) return;

            // Two payload shapes:
            //   InsertSpec  -> compose "/<prefix> \"<name>\" <target>" using the
            //                  currently-selected Target dropdown value, REPLACING
            //                  whatever the line currently holds.
            //   string      -> a target token or boilerplate snippet (leading
            //                  space means "append to existing line", no leading
            //                  space means "replace line content").
            var spec = item.Tag as InsertSpec;
            if (spec != null)
            {
                box.Text = ComposeCommand(spec.Prefix, spec.Name);
                // Auto-fill the macro title with an abbreviated form of the
                // ability name -- BUT only when the title is empty or still
                // shows the previous auto-default. We never overwrite a user
                // edit. AbbreviateForTitle ensures the result fits FFXI's
                // 8-char macro title limit (e.g. "Absorb-STR" -> "AB. STR",
                // "Trick Attack" -> "Trck Atk", "Flee" -> "Flee").
                if (TxtTitle != null)
                {
                    bool emptyOrAuto = string.IsNullOrEmpty(TxtTitle.Text)
                        || string.Equals(TxtTitle.Text, _lastAutoTitle, StringComparison.Ordinal);
                    if (emptyOrAuto)
                    {
                        string abbr = AbbreviateForTitle(spec.Name);
                        TxtTitle.Text   = abbr;
                        _lastAutoTitle  = abbr;
                    }
                }
                box.Focus();
                box.CaretIndex = box.Text.Length;
                return;
            }

            string insert = item.Tag as string;
            if (string.IsNullOrEmpty(insert)) return;

            if (string.IsNullOrEmpty(box.Text))
                box.Text = insert.TrimStart();
            else if (insert.StartsWith(" "))
                box.Text = box.Text + insert;
            else
                box.Text = insert;

            box.Focus();
            box.CaretIndex = box.Text.Length;
        }

        // Tracks the most recent value we auto-filled into TxtTitle from a
        // library double-click. We compare on the next double-click so the
        // user's manual edits to the title are NEVER overwritten -- only the
        // previous auto-default gets replaced. Empty title is also treated
        // as "user hasn't claimed it" and gets the new default.
        private string _lastAutoTitle = null;

        // Turn an ability / spell name into something that fits FFXI's 8-char
        // macro title field while staying readable. Strategy:
        //
        //   1. If the name already fits (<= 8 chars), use it verbatim.
        //   2. Multi-word names: keep the LAST word in full and abbreviate
        //      every word before it to 2 chars + ".". So:
        //        "Trick Attack" -> "Tr.Attack"  ... still too long ->
        //                       -> "Tr. Atk" via second-word truncation if
        //                          needed, capped at 8.
        //        "Absorb STR"   -> "AB. STR"  (matches user's example)
        //        "Sneak Attack" -> "Sn. Attack" -> "Sn. Atk"
        //        "Magic Burst Bonus" -> "M.B. Bonus" -> "M.B.Bonu"
        //   3. Single-word names longer than 8: drop interior vowels first
        //      (after the first letter), e.g. "Benediction" -> "Bendctn".
        //   4. Anything still over 8 gets a hard truncate.
        //
        // The period is the visual hint that "this word was shortened" --
        // matches the user's "AB. STR" example.
        private static string AbbreviateForTitle(string name)
        {
            const int MAX = 8;
            if (string.IsNullOrEmpty(name)) return "";
            name = name.Trim();
            if (name.Length <= MAX) return name;

            // Split on whitespace, hyphen, and apostrophe-as-word-boundary
            // ("Cure's" stays one word; "Absorb-STR" splits in two).
            var words = name.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == 1)
            {
                // Single word: drop interior vowels after the first character.
                // "Benediction" -> "Bndctn" -> hard-truncate to 8 if still long.
                string w = words[0];
                var sb = new StringBuilder();
                sb.Append(w[0]);
                for (int i = 1; i < w.Length; i++)
                {
                    char c = w[i];
                    if ("aeiouAEIOU".IndexOf(c) < 0) sb.Append(c);
                }
                string compressed = sb.ToString();
                return compressed.Length <= MAX ? compressed : compressed.Substring(0, MAX);
            }

            // Multi-word: try increasingly aggressive shortening of all
            // words EXCEPT the last, then truncate the last word if still
            // too long.
            string last = words[words.Length - 1];

            // First attempt: 2 letters + period for every non-final word,
            // last word in full.
            string ShortPrefix(string w) =>
                (w.Length <= 2 ? w : w.Substring(0, 2)) + ".";

            var parts = new List<string>();
            for (int i = 0; i < words.Length - 1; i++) parts.Add(ShortPrefix(words[i]));
            parts.Add(last);
            string joined = string.Join(" ", parts);
            if (joined.Length <= MAX) return joined;

            // Still too long: drop the space between the prefix and the
            // last word ("Tr. Attack" -> "Tr.Attack").
            joined = string.Concat(parts);
            if (joined.Length <= MAX) return joined;

            // Still too long: shrink the last word to fit. Reserve the
            // total prefix length and use the remainder for the last word.
            int prefixLen = 0;
            for (int i = 0; i < parts.Count - 1; i++) prefixLen += parts[i].Length;
            int budget = MAX - prefixLen;
            if (budget < 1) budget = 1;
            string lastShort = last.Length <= budget ? last : last.Substring(0, budget);
            // Re-join with the shortened last word (no spaces).
            var sb2 = new StringBuilder();
            for (int i = 0; i < parts.Count - 1; i++) sb2.Append(parts[i]);
            sb2.Append(lastShort);
            string result = sb2.ToString();
            return result.Length <= MAX ? result : result.Substring(0, MAX);
        }

        // ------------------------------------------------------------------
        // settings: remember last install folder between runs
        // ------------------------------------------------------------------
        private string LoadSavedInstallPath()
        {
            try {
                if (!File.Exists(SETTINGS_PATH)) return null;
                return File.ReadAllText(SETTINGS_PATH).Trim();
            } catch { return null; }
        }

        private void SaveInstallPath(string path)
        {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(SETTINGS_PATH));
                File.WriteAllText(SETTINGS_PATH, path);
            } catch { }
        }
    }
}
