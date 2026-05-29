// Diagnostic: print all 20 macros (with title + first non-empty line)
// from one mcr*.dat file, so we can compare the parser's interpretation
// against what the user sees in-game.
using System;
using System.IO;
using FFXIMacroManager.Models;
using FFXIMacroManager.Data;

class Dump
{
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: dump-macros.exe <mcrN.dat>");
            return 1;
        }
        var mf = MacroFile.Load(args[0]);
        Console.WriteLine("file: " + Path.GetFileName(args[0]));
        Console.WriteLine("---  index  hotkey   title         first line");
        for (int i = 0; i < mf.Macros.Count; i++)
        {
            var m = mf.Macros[i];
            string firstLine = "";
            foreach (var ln in m.Lines)
            {
                if (!string.IsNullOrEmpty(ln.Text)) { firstLine = ln.Text; break; }
            }
            Console.WriteLine(string.Format("    {0,2}     {1,-6}   {2,-12}  {3}",
                i, m.HotkeyLabel, m.Title ?? "", firstLine));
        }
        return 0;
    }
}
