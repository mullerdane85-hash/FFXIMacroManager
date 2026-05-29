// FFXI Macro Manager — mcr.ttl reader
//
// mcr.ttl holds the in-game names of all 20 macro books. Layout:
//
//   0x000-0x017  24-byte header (magic + checksum/signature, opaque)
//   0x018-0x117  20 × 16-byte entries, one per book
//
// Each entry is null-padded ASCII (e.g. "WHM\0\0...", "Book13\0\0..."
// for an unnamed default). Total file size is exactly 344 bytes.
//
// On save, we write the modified names back into the same 16-byte
// slots, preserving the 24-byte header verbatim. Empty / default names
// are written as "BookN" so the in-game UI displays something readable.

using System;
using System.IO;
using System.Text;

namespace FFXIMacroManager.Models
{
    public sealed class BookTitles
    {
        public const int FILE_SIZE   = 344;
        public const int HEADER_SIZE = 24;
        public const int BOOK_COUNT  = 20;
        public const int ENTRY_SIZE  = 16;

        public string SourcePath { get; private set; }
        public byte[] RawHeader  { get; private set; } = new byte[HEADER_SIZE];
        public string[] Names    { get; private set; } = new string[BOOK_COUNT];
        public byte[][] RawNames { get; private set; } = new byte[BOOK_COUNT][];

        public static BookTitles LoadOrDefault(string characterFolder)
        {
            var bt = new BookTitles();
            string path = Path.Combine(characterFolder ?? "", "mcr.ttl");
            bt.SourcePath = path;
            for (int i = 0; i < BOOK_COUNT; i++)
            {
                bt.Names[i] = "Book " + (i + 1);
                bt.RawNames[i] = new byte[ENTRY_SIZE];
            }

            if (!File.Exists(path)) return bt;

            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length < FILE_SIZE) return bt;

                Array.Copy(bytes, 0, bt.RawHeader, 0, HEADER_SIZE);
                for (int i = 0; i < BOOK_COUNT; i++)
                {
                    var slot = new byte[ENTRY_SIZE];
                    Array.Copy(bytes, HEADER_SIZE + i * ENTRY_SIZE, slot, 0, ENTRY_SIZE);
                    bt.RawNames[i] = slot;
                    bt.Names[i] = DecodeName(slot, i);
                }
            }
            catch { /* leave defaults */ }
            return bt;
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(SourcePath))
                throw new InvalidOperationException("BookTitles has no source path");

            var bytes = new byte[FILE_SIZE];
            Array.Copy(RawHeader, 0, bytes, 0, HEADER_SIZE);
            for (int i = 0; i < BOOK_COUNT; i++)
            {
                var slot = EncodeName(Names[i] ?? ("Book" + (i + 1)));
                Array.Copy(slot, 0, bytes, HEADER_SIZE + i * ENTRY_SIZE, ENTRY_SIZE);
                RawNames[i] = slot;
            }

            string tmp = SourcePath + ".new";
            string bak = SourcePath + ".bak";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(SourcePath))
            {
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(SourcePath, bak);
            }
            File.Move(tmp, SourcePath);
        }

        // Default placeholder names ("Book11", "Book12", ...) get
        // pretty-printed back to "Book 11" so the dropdown reads naturally.
        // A real user-set name like "WHM" is returned unchanged.
        public static string DecodeName(byte[] slot, int index)
        {
            int len = 0;
            while (len < slot.Length && slot[len] != 0) len++;
            string raw = Encoding.ASCII.GetString(slot, 0, len).Trim();
            if (string.IsNullOrEmpty(raw)) return "Book " + (index + 1);

            string defaultBare = "Book" + (index + 1);
            if (string.Equals(raw, defaultBare, StringComparison.OrdinalIgnoreCase))
                return "Book " + (index + 1);
            return raw;
        }

        public static byte[] EncodeName(string name)
        {
            var slot = new byte[ENTRY_SIZE];
            if (string.IsNullOrEmpty(name)) return slot;
            // The in-game UI accepts ~15 ASCII chars; we truncate to keep
            // one trailing null in the 16-byte field.
            int n = Math.Min(name.Length, ENTRY_SIZE - 1);
            for (int i = 0; i < n; i++) slot[i] = (byte)(name[i] & 0xFF);
            return slot;
        }
    }
}
