// FFXI Macro Manager — round-trip verifier
//
// Loads every mcr*.dat in a given character folder, saves it to a temp
// folder, and byte-compares.  Any difference means the parser is lossy.
//
// Build + run:
//     "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
//         -out:verify.exe -r:System.Web.Extensions.dll
//         tools\verify-roundtrip.cs src\Models\MacroFile.cs src\Data\SpellDb.cs
//     verify.exe "<character folder>"
//
// (the SpellDb reference is only needed because MacroFile.cs uses
//  AutoTranslateDb for the decode display — the round-trip itself only
//  exercises the binary path.)

using System;
using System.IO;
using System.Linq;
using FFXIMacroManager.Models;
using FFXIMacroManager.Data;

class Verify
{
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: verify.exe <character folder>");
            return 1;
        }

        string folder = args[0];
        if (!Directory.Exists(folder))
        {
            Console.WriteLine("not a folder: " + folder);
            return 1;
        }

        int total = 0, ok = 0, fail = 0;
        foreach (var f in Directory.GetFiles(folder, "mcr*.dat"))
        {
            string name = Path.GetFileName(f);
            if (name == "mcr.dat") continue; // session file, not a page

            total++;
            byte[] before = File.ReadAllBytes(f);
            MacroFile mf;
            try { mf = MacroFile.Load(f); }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL load] {0}: {1}", name, ex.Message);
                fail++; continue;
            }

            // Save to a sibling tmp path so we don't touch the original
            string tmp = Path.Combine(Path.GetTempPath(), "verify_" + name);
            if (File.Exists(tmp)) File.Delete(tmp);
            try { mf.Save(tmp); }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL save] {0}: {1}", name, ex.Message);
                fail++; continue;
            }

            byte[] after = File.ReadAllBytes(tmp);
            File.Delete(tmp);
            if (File.Exists(tmp + ".bak")) File.Delete(tmp + ".bak");
            if (File.Exists(tmp + ".new")) File.Delete(tmp + ".new");

            if (before.Length != after.Length)
            {
                Console.WriteLine("[FAIL size] {0}: {1} -> {2}", name, before.Length, after.Length);
                fail++; continue;
            }

            int diff = -1;
            for (int i = 0; i < before.Length; i++)
                if (before[i] != after[i]) { diff = i; break; }

            if (diff >= 0)
            {
                Console.WriteLine("[FAIL diff] {0}: byte {1} differs ({2:X2} vs {3:X2})",
                    name, diff, before[diff], after[diff]);
                fail++;
            }
            else
            {
                ok++;
            }
        }
        Console.WriteLine("");
        Console.WriteLine("Round-trip: {0}/{1} files byte-perfect, {2} failed.", ok, total, fail);
        return fail == 0 ? 0 : 1;
    }
}
