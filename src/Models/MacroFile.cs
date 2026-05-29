// FFXI Macro Manager — binary format for mcr*.dat files
//
// File layout (7624 bytes total):
//
//     0x0000-0x0003  Magic         01 00 00 00 (version)
//     0x0004-0x0007  Flags         usually 00 00 00 00, sometimes 04 01 00 00
//     0x0008-0x0017  MD5 hash      16 bytes — MD5 of the 7600-byte macro
//                                  payload (bytes 0x0018-0x1DC7). FFXI
//                                  computes this when it writes the file
//                                  and we MUST recompute it whenever the
//                                  payload changes or the game will refuse
//                                  to load our edits.
//                                  (Cross-checked against the towbes /
//                                  azenus open-source FFXI Macro Editor.)
//     0x0018-0x1DC7  Macros        20 macros × 380 bytes each
//                                  (10 CTRL macros + 10 ALT macros per page)
//
// Per-macro layout (380 bytes):
//
//     0x000-0x003    Reserved      always zero in observed files
//     0x004-0x171    Lines         6 lines × 61 bytes each = 366 bytes
//                                  Each line is null-padded ASCII text;
//                                  embedded auto-translate codes use the
//                                  six-byte pattern  FD <cat> <kind> <id-hi> <id-lo> FD
//     0x172-0x17B    Title         10 bytes, null-padded ASCII (max ~8 chars)
//
// FFXI stores macros as plain text, so the simplest round-trip strategy is:
// preserve the raw 61-byte buffer for each unchanged line, and re-encode
// only the lines the user actually edits. The page-title block (which is
// obfuscated) is preserved verbatim so we never corrupt it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using FFXIMacroManager.Data;

namespace FFXIMacroManager.Models
{
    public sealed class MacroFile
    {
        public const int FILE_SIZE       = 7624;
        public const int HEADER_SIZE     = 24;
        public const int MACRO_COUNT     = 20;
        public const int MACRO_SIZE      = 380;
        public const int MACRO_RESERVED  = 4;
        public const int LINE_COUNT      = 6;
        public const int LINE_SIZE       = 61;
        public const int TITLE_OFFSET    = 370;
        public const int TITLE_SIZE      = 10;

        public string SourcePath { get; private set; }
        public byte[] RawHeader  { get; private set; } = new byte[HEADER_SIZE];
        public List<Macro> Macros { get; private set; } = new List<Macro>();

        public static MacroFile Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != FILE_SIZE)
                throw new InvalidDataException(
                    string.Format("{0}: expected {1} bytes, got {2}",
                                  Path.GetFileName(path), FILE_SIZE, bytes.Length));

            var f = new MacroFile { SourcePath = path };
            Array.Copy(bytes, 0, f.RawHeader, 0, HEADER_SIZE);

            for (int i = 0; i < MACRO_COUNT; i++)
            {
                int macroStart = HEADER_SIZE + i * MACRO_SIZE;
                var m = new Macro { Index = i };
                Array.Copy(bytes, macroStart, m.RawReserved, 0, MACRO_RESERVED);

                for (int line = 0; line < LINE_COUNT; line++)
                {
                    int lineStart = macroStart + MACRO_RESERVED + line * LINE_SIZE;
                    var buf = new byte[LINE_SIZE];
                    Array.Copy(bytes, lineStart, buf, 0, LINE_SIZE);
                    m.Lines.Add(new MacroLine(buf));
                }

                var titleBuf = new byte[TITLE_SIZE];
                Array.Copy(bytes, macroStart + TITLE_OFFSET, titleBuf, 0, TITLE_SIZE);
                m.RawTitle = titleBuf;

                f.Macros.Add(m);
            }
            return f;
        }

        // Saves the in-memory representation back to disk.  Writes to a
        // .new file, renames the original to .bak, then renames .new to the
        // target so a partial write never destroys the existing file.
        public void Save(string path = null)
        {
            path = path ?? SourcePath;
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("Save needs a path");

            var bytes = new byte[FILE_SIZE];
            Array.Copy(RawHeader, 0, bytes, 0, HEADER_SIZE);

            for (int i = 0; i < MACRO_COUNT && i < Macros.Count; i++)
            {
                var m = Macros[i];
                int macroStart = HEADER_SIZE + i * MACRO_SIZE;
                Array.Copy(m.RawReserved, 0, bytes, macroStart, MACRO_RESERVED);

                for (int line = 0; line < LINE_COUNT && line < m.Lines.Count; line++)
                {
                    int lineStart = macroStart + MACRO_RESERVED + line * LINE_SIZE;
                    var buf = m.Lines[line].ToBytes(LINE_SIZE);
                    Array.Copy(buf, 0, bytes, lineStart, LINE_SIZE);
                }

                // RawTitle holds the original 10 bytes verbatim; the Title
                // setter (used by the UI when the user edits the name)
                // updates RawTitle in-place. Writing RawTitle here keeps
                // titles byte-perfect across pages we don't touch — some
                // legitimate FFXI saves contain trailing bytes after a null
                // that null-terminated decoding alone would drop.
                Array.Copy(m.RawTitle, 0, bytes, macroStart + TITLE_OFFSET, TITLE_SIZE);
            }

            // Recompute the MD5 hash over the 7600-byte macro payload and
            // write it into header bytes 8-23. FFXI computes this hash on
            // save and uses it to verify file integrity; if we leave the
            // original hash in place after edits, the game silently rejects
            // the file. Confirmed by reading towbes/azenus' open-source
            // FFXI Macro Editor (Functions.vb).
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(bytes, HEADER_SIZE, FILE_SIZE - HEADER_SIZE);
                Array.Copy(hash, 0, bytes, 8, 16);
            }

            var tmp = path + ".new";
            var bak = path + ".bak";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path))
            {
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(path, bak);
            }
            File.Move(tmp, path);
        }
    }

    public sealed class Macro
    {
        public int    Index        { get; set; }
        public byte[] RawReserved  { get; set; } = new byte[MacroFile.MACRO_RESERVED];
        public byte[] RawTitle     { get; set; } = new byte[MacroFile.TITLE_SIZE];
        public List<MacroLine> Lines { get; set; } = new List<MacroLine>();

        // Display title — decoded from RawTitle on read, re-encoded on save.
        public string Title
        {
            get { return MacroLine.DecodeTitle(RawTitle); }
            set { RawTitle = MacroLine.EncodeFixed(value, MacroFile.TITLE_SIZE); }
        }

        // Is this macro slot effectively blank?  Used to grey out empty slots.
        public bool IsEmpty
        {
            get
            {
                if (!string.IsNullOrEmpty(Title)) return false;
                foreach (var ln in Lines) if (!ln.IsEmpty) return false;
                return true;
            }
        }

        // Macro slots 0..9 are CTRL 1..0, 10..19 are ALT 1..0.
        public string HotkeyLabel
        {
            get
            {
                int n = Index < 10 ? Index + 1 : Index - 9;
                string mod = Index < 10 ? "CTRL" : "ALT";
                string slot = (n == 10) ? "0" : n.ToString();
                return mod + "+" + slot;
            }
        }
    }

    // A macro line is up to 61 bytes of null-padded text. Auto-translate
    // entries appear inline as `FD <cat> <kind> <id-hi> <id-lo> FD` — six
    // raw bytes inside what is otherwise plain ASCII.  For display we leave
    // them as `{AT:cat/kind/id}` placeholders so the user can see them and
    // even leave them alone; for save, anything the user typed is re-encoded
    // as ASCII so the in-game macro receives plain text (which FFXI accepts).
    public sealed class MacroLine
    {
        public byte[] OriginalBytes { get; private set; }
        public string Text          { get; set; }
        public bool   Dirty         { get; set; }

        public MacroLine(byte[] raw)
        {
            OriginalBytes = raw;
            Text          = Decode(raw);
        }

        public bool IsEmpty { get { return string.IsNullOrEmpty(Text); } }

        // Re-emit this line as a 61-byte buffer.  Unchanged lines reuse the
        // original buffer (preserves auto-translate codes byte-for-byte);
        // dirty lines get encoded fresh from the typed text.
        public byte[] ToBytes(int size)
        {
            if (!Dirty && OriginalBytes != null && OriginalBytes.Length == size)
                return OriginalBytes;
            return EncodeFixed(Text ?? "", size);
        }

        // ------------------------------------------------------------------
        // decode: bytes -> displayable string
        // ------------------------------------------------------------------
        public static string Decode(byte[] buf)
        {
            if (buf == null || buf.Length == 0) return "";
            var sb = new StringBuilder();
            int i = 0;
            while (i < buf.Length)
            {
                byte b = buf[i];
                if (b == 0x00)
                {
                    // null byte = end-of-line padding
                    break;
                }
                if (b == 0xFD && i + 5 < buf.Length && buf[i + 5] == 0xFD)
                {
                    int cat  = buf[i + 1];
                    int kind = buf[i + 2];
                    int id   = (buf[i + 3] << 8) | buf[i + 4];
                    string name = AutoTranslateDb.Lookup(cat, kind, id);
                    sb.Append('{').Append(name ?? string.Format("AT:{0}/{1}/{2}", cat, kind, id)).Append('}');
                    i += 6;
                    continue;
                }
                // shift_jis-ish bytes >= 0x80 occasionally show up in Japanese-set
                // macros; treat them as latin-1 so we round-trip unchanged.
                if (b >= 0x80)
                {
                    sb.Append((char)b);
                    i++;
                    continue;
                }
                sb.Append((char)b);
                i++;
            }
            return sb.ToString();
        }

        // Title decoding is the same scheme — text starts at byte 0 of the
        // title field and runs until the first null.
        public static string DecodeTitle(byte[] buf)
        {
            return Decode(buf);
        }

        // ------------------------------------------------------------------
        // encode: string -> fixed-size null-padded bytes
        // ------------------------------------------------------------------
        public static byte[] EncodeFixed(string text, int size)
        {
            var buf = new byte[size];
            if (string.IsNullOrEmpty(text)) return buf;

            int dst = 0;
            int i = 0;
            while (i < text.Length && dst < size)
            {
                char c = text[i];
                // Handle our own {AT:cat/kind/id} placeholder format so users
                // who don't touch an existing AT code still get one back.
                if (c == '{' && i + 4 < text.Length && text[i + 1] == 'A' && text[i + 2] == 'T' && text[i + 3] == ':')
                {
                    int close = text.IndexOf('}', i + 4);
                    if (close > 0)
                    {
                        var spec = text.Substring(i + 4, close - i - 4); // "cat/kind/id"
                        int cat, kind, id;
                        if (TryParseAtSpec(spec, out cat, out kind, out id))
                        {
                            if (dst + 6 > size) break;
                            buf[dst++] = 0xFD;
                            buf[dst++] = (byte)cat;
                            buf[dst++] = (byte)kind;
                            buf[dst++] = (byte)((id >> 8) & 0xFF);
                            buf[dst++] = (byte)(id & 0xFF);
                            buf[dst++] = 0xFD;
                            i = close + 1;
                            continue;
                        }
                    }
                }
                // Plain ASCII byte (truncate anything multi-byte to its low
                // 8 bits — same as how Decode round-trips >=0x80 bytes).
                buf[dst++] = (byte)(c & 0xFF);
                i++;
            }
            return buf;
        }

        // accepts "cat/kind/id" or "cat/kind/id; <english>" — we tolerate
        // the friendly english label after a semicolon because Lookup() may
        // produce nicer text we want to discard at encode time.
        private static bool TryParseAtSpec(string spec, out int cat, out int kind, out int id)
        {
            cat = kind = id = 0;
            if (string.IsNullOrEmpty(spec)) return false;
            // strip anything after ' '
            int sp = spec.IndexOf(' ');
            if (sp >= 0) spec = spec.Substring(0, sp);
            var parts = spec.Split('/');
            if (parts.Length != 3) return false;
            return int.TryParse(parts[0], out cat)
                && int.TryParse(parts[1], out kind)
                && int.TryParse(parts[2], out id);
        }
    }
}
