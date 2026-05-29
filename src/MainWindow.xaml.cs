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
            BtnReload.Click      += (_, __) => ReloadActivePage();
            BtnSavePage.Click    += BtnSavePage_Click;
            BtnRevert.Click      += BtnRevert_Click;
            BtnClear.Click       += BtnClear_Click;

            DdCharacter.SelectionChanged += DdCharacter_SelectionChanged;
            DdJob.SelectionChanged       += DdJob_SelectionChanged;
            DdBook.SelectionChanged      += DdBookOrPage_SelectionChanged;
            DdPage.SelectionChanged      += DdBookOrPage_SelectionChanged;

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
            DdLibKind.SelectionChanged   += (_, __) => RefreshLibrary();
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
            int wantPage = Math.Max(1, Math.Min(10, prevPage));
            DdPage.SelectedIndex = wantPage - 1;

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
            for (int p = 1; p <= 10; p++)
            {
                int n = (book - 1) * 10 + p;
                string file = System.IO.Path.Combine(_activeChar.FolderPath, "mcr" + n + ".dat");
                string label = "Page " + p + (System.IO.File.Exists(file)
                                ? "    (mcr" + n + ".dat)"
                                : "    (empty)");
                DdPage.Items.Add(new ComboBoxItem { Content = label, Tag = p });
            }
            DdPage.SelectedIndex = Math.Max(0, Math.Min(9, prev - 1));
            DdPage.SelectionChanged += DdBookOrPage_SelectionChanged;
        }

        private void PopulateJobDropdown()
        {
            DdJob.Items.Clear();
            DdJob.Items.Add(new ComboBoxItem { Content = "(all jobs)", Tag = -1, IsSelected = true });

            // Show only the magic-capable jobs by default — these are the
            // ones whose spell macros need management.
            int[] order = { 3, 4, 5, 7, 8, 10, 13, 15, 16, 20, 21, 22 };
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
            // If the book changed, refresh the page labels so the "(empty)"
            // hints reflect the new book's pages.
            if (sender == DdBook) RefreshPageLabels();
            ReloadActivePage();
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

            int n = (SelectedBook - 1) * 10 + SelectedPage;
            string file = Path.Combine(_activeChar.FolderPath, "mcr" + n + ".dat");
            _activePageRef = new MacroPageRef {
                Book = SelectedBook, Page = SelectedPage, FileNumber = n,
                Path = file, Exists = File.Exists(file)
            };
            LblPageTitle.Text = string.Format("Book {0} / Page {1}  ({2})",
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

            // Reselect the previously-selected macro if still in range
            if (_activeMacroIndex >= 0 && _activeMacroIndex < MacroFile.MACRO_COUNT)
                SelectMacro(_activeMacroIndex);
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
            // Commit any pending edits into the model first so we don't lose them.
            CommitEditorToModel();

            _activeMacroIndex = idx;
            BuildLineEditor();
            RenderAllSlots(); // re-highlight selection

            if (_activeFile == null || idx < 0 || idx >= _activeFile.Macros.Count) {
                LblEditTitle.Text = "No macro selected";
                TxtTitle.Text = "";
                return;
            }
            var m = _activeFile.Macros[idx];
            LblEditTitle.Text = "Editing " + m.HotkeyLabel + "  ·  slot #" + (idx + 1);
            TxtTitle.Text = m.Title ?? "";
            for (int i = 0; i < MacroFile.LINE_COUNT && i < m.Lines.Count; i++)
            {
                _lineBoxes[i].Text = m.Lines[i].Text ?? "";
                _lineBoxes[i].Tag = false; // not yet dirty since user hasn't typed
            }
            UpdateDirtyLabel();
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
                tb.TextChanged += (_, __) => { _lineBoxes[captured].Tag = true; UpdateDirtyLabel(); };
                Grid.SetColumn(tb, 1);
                row.Children.Add(tb);

                _lineBoxes[i] = tb;
                LineEditorHost.Children.Add(row);
            }
        }

        private void ClearEditor()
        {
            BuildLineEditor();
            TxtTitle.Text = "";
            LblEditTitle.Text = "No macro selected";
            LblDirty.Text = "";
        }

        private void TxtTitle_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Update preview live
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

        // Build the right-hand macro fragment that's pasted into a line:
        //   "/ma \"Cure III\" <stal>"
        // Target token is whatever the Target dropdown currently shows.
        private string ComposeCommand(string prefix, string actionName)
        {
            string target = SelectedTargetToken();
            string head = prefix + " \"" + actionName + "\"";
            return string.IsNullOrEmpty(target) ? head : (head + " " + target);
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
                LblLibHint.Text = "Double-click a weapon skill to insert it with the selected target.";
                var list = SpellDb.WeaponSkillsFor(jobId);
                foreach (var w in list)
                {
                    if (filter.Length > 0 && w.En.ToLowerInvariant().IndexOf(filter) < 0) continue;
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
