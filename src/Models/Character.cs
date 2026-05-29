// FFXI Macro Manager — character discovery
//
// Each character is a numeric subfolder of FFXI's USER directory:
//
//     <install>\USER\<id>\mcr1.dat
//     <install>\USER\<id>\mcr2.dat
//     ...
//     <install>\USER\<id>\mcr200.dat
//
// FFXI stores the player's character name in ffxiusr.msg in the same
// folder, but the surface format is opaque, so for v1 we display the
// folder ID as a short label and the most-recent mcr file's mtime to
// help the user disambiguate (their most-recently-played character
// will have the freshest macro file).
//
// Macro filenames are mcr<N>.dat where N is the 1-based page index.
// FFXI exposes 20 books × 10 pages, so N ranges 1..200.  Some files
// (mcr.dat, mcr_2.dat, mcr.sys, mcr.ttl) hold session/ui state and
// are NOT macro pages.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace FFXIMacroManager.Models
{
    public sealed class Character
    {
        public string FolderPath { get; private set; }
        public string FolderId   { get { return Path.GetFileName(FolderPath); } }
        public string DisplayName{ get; set; }
        public DateTime LastPlayed { get; set; }

        public Character(string path)
        {
            FolderPath = path;
            DisplayName = FolderId;

            // Latest mcr*.dat mtime is a proxy for "last played"
            try
            {
                var dir = new DirectoryInfo(path);
                var files = dir.GetFiles("mcr*.dat");
                DateTime newest = DateTime.MinValue;
                foreach (var f in files)
                {
                    if (f.LastWriteTime > newest) newest = f.LastWriteTime;
                }
                LastPlayed = newest;
            }
            catch { LastPlayed = DateTime.MinValue; }
        }

        // The FFXI per-character folder is named by a hex/numeric internal ID,
        // not the character name. We can usually guess the friendly name by
        // looking at the GearSwap addon's data folder, which contains files
        // named like "<CharName>_<job>.lua" — for the most-recently-played
        // FFXI character that file's mtime will be close to ours.
        //
        // We also support an explicit override map: per-folder names typed by
        // the user are persisted to %APPDATA%\FFXIMacroManager\characters.txt
        // and take precedence over the GearSwap guess.
        public static Dictionary<string, string> LoadNameOverrides()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = NameOverridesPath();
                if (!File.Exists(path)) return map;
                foreach (var line in File.ReadAllLines(path))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                    int eq = trimmed.IndexOf('=');
                    if (eq <= 0) continue;
                    map[trimmed.Substring(0, eq).Trim()] = trimmed.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return map;
        }

        public static void SaveNameOverride(string folderId, string name)
        {
            var map = LoadNameOverrides();
            map[folderId] = name ?? "";
            try
            {
                string path = NameOverridesPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var w = new StreamWriter(path, false))
                {
                    w.WriteLine("# FFXI Macro Manager - character name overrides");
                    w.WriteLine("# format: <folder id>=<display name>");
                    foreach (var kv in map)
                        w.WriteLine(kv.Key + "=" + kv.Value);
                }
            }
            catch { }
        }

        private static string NameOverridesPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FFXIMacroManager", "characters.txt");
        }

        // Scan a GearSwap data folder for files named "<Name>_<anything>.lua"
        // and return unique <Name> prefixes ordered by total file mtime.
        // Result: most-recently-modified character first.
        public static List<string> ScanGearSwapNames(string gearswapDataDir)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(gearswapDataDir) || !Directory.Exists(gearswapDataDir))
                return names;
            var byName = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.GetFiles(gearswapDataDir, "*.lua"))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    int us = name.IndexOf('_');
                    if (us <= 0) continue;
                    string prefix = name.Substring(0, us);
                    // FFXI character names are 3-15 letters and start with
                    // a capital letter; that filters out shared GearSwap
                    // library files like "Mote_Includes_*.lua".
                    if (prefix.Length < 3 || prefix.Length > 15) continue;
                    if (!char.IsLetter(prefix[0]) || !char.IsUpper(prefix[0])) continue;
                    bool ok = true;
                    for (int i = 1; i < prefix.Length; i++)
                        if (!char.IsLetter(prefix[i])) { ok = false; break; }
                    if (!ok) continue;
                    var mt = File.GetLastWriteTime(f);
                    DateTime existing;
                    if (!byName.TryGetValue(prefix, out existing) || mt > existing)
                        byName[prefix] = mt;
                }
            }
            catch { }
            var ordered = new List<KeyValuePair<string, DateTime>>(byName);
            ordered.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var kv in ordered) names.Add(kv.Key);
            return names;
        }

        // Best-effort path to GearSwap's data folder.  We try a few common
        // layouts relative to the FFXI install root.
        public static string FindGearSwapDataDir(string installRoot)
        {
            if (string.IsNullOrEmpty(installRoot)) return null;
            // Walk up to 3 levels looking for "Windower\addons\GearSwap\data"
            var dir = new DirectoryInfo(installRoot);
            for (int i = 0; i < 4 && dir != null; i++)
            {
                string candidate = Path.Combine(dir.FullName, "Windower", "addons", "GearSwap", "data");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        // Yields all 200 possible macro page slots, marking which ones
        // physically exist on disk.  This lets the UI show every book/page
        // even if the file hasn't been created yet.
        public IEnumerable<MacroPageRef> MacroPages()
        {
            for (int book = 1; book <= 20; book++)
            {
                for (int page = 1; page <= 10; page++)
                {
                    int n = (book - 1) * 10 + page;
                    string file = Path.Combine(FolderPath, "mcr" + n + ".dat");
                    yield return new MacroPageRef {
                        Book = book, Page = page, FileNumber = n,
                        Path = file, Exists = File.Exists(file)
                    };
                }
            }
        }

        // ------------------------------------------------------------------
        // Discovery
        // ------------------------------------------------------------------
        // FFXI's install layout is e.g.
        //     D:\FFXI\SquareEnix\FINAL FANTASY XI\USER\<id>\mcr1.dat
        // The user may give us any of these levels (the SquareEnix root,
        // the FFXI install root, or the USER folder itself) so we scan
        // upward/downward a couple of levels looking for USER.
        public static string ResolveUserFolder(string startPath)
        {
            if (string.IsNullOrEmpty(startPath) || !Directory.Exists(startPath))
                return null;

            // Case 1: the path is already USER/
            if (HasCharacterFolders(startPath))
                return startPath;

            // Case 2: USER is one level down
            string user = Path.Combine(startPath, "USER");
            if (Directory.Exists(user) && HasCharacterFolders(user))
                return user;

            // Case 3: dig one more level (e.g. user gave us "SquareEnix\")
            foreach (var sub in Directory.GetDirectories(startPath))
            {
                string deeper = Path.Combine(sub, "USER");
                if (Directory.Exists(deeper) && HasCharacterFolders(deeper))
                    return deeper;
                if (HasCharacterFolders(sub))
                    return sub;
            }
            return null;
        }

        private static readonly Regex CHAR_FOLDER_PATTERN = new Regex(@"^[0-9a-f]+$");

        private static bool HasCharacterFolders(string dir)
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (!CHAR_FOLDER_PATTERN.IsMatch(name)) continue;
                    if (File.Exists(Path.Combine(sub, "mcr.sys"))) return true;
                }
            }
            catch { }
            return false;
        }

        public static List<Character> EnumerateAt(string userFolder)
        {
            var chars = new List<Character>();
            if (string.IsNullOrEmpty(userFolder) || !Directory.Exists(userFolder))
                return chars;

            foreach (var sub in Directory.GetDirectories(userFolder))
            {
                string name = Path.GetFileName(sub);
                if (!CHAR_FOLDER_PATTERN.IsMatch(name)) continue;
                if (!File.Exists(Path.Combine(sub, "mcr.sys"))) continue;
                chars.Add(new Character(sub));
            }
            // Most-recently-played first
            chars.Sort((a, b) => b.LastPlayed.CompareTo(a.LastPlayed));
            return chars;
        }
    }

    public sealed class MacroPageRef
    {
        public int    Book       { get; set; } // 1..20
        public int    Page       { get; set; } // 1..10
        public int    FileNumber { get; set; } // mcr<N>.dat
        public string Path       { get; set; }
        public bool   Exists     { get; set; }
        public string Label
        {
            get { return "Book " + Book + " / Page " + Page + (Exists ? "" : " (empty)"); }
        }
    }
}
