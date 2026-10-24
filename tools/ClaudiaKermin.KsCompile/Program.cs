// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKermin.ScriptCheck;

namespace ClaudiaKermin.KsCompile
{
    /// <summary>
    /// Runs the Script Check (ADR 0003) over each .ks file, or every .ks under each directory: each must
    /// compile with kOS.Safe and use nothing on the forbidden list.
    /// Exit code 0: all passed; 1: at least one failed; 2: bad usage or no files found.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: ClaudiaKermin.KsCompile <file.ks | directory>...");
                return 2;
            }

            var files = new List<string>();
            foreach (var arg in args)
            {
                if (Directory.Exists(arg))
                    files.AddRange(Directory.GetFiles(arg, "*.ks", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal));
                else if (File.Exists(arg))
                    files.Add(arg);
                else
                {
                    Console.Error.WriteLine("not found: " + arg);
                    return 2;
                }
            }

            if (files.Count == 0)
            {
                Console.Error.WriteLine("no .ks files found in: " + string.Join(" ", args));
                return 2;
            }

            var check = new KosScriptCheck();
            var failures = 0;
            foreach (var file in files)
            {
                var result = check.Check(File.ReadAllText(file));
                if (result.Passed)
                {
                    Console.WriteLine("ok   " + file);
                    continue;
                }
                failures++;
                Console.WriteLine("FAIL " + file);
                foreach (var problem in result.Problems)
                    Console.WriteLine("     " + problem.Replace("\n", "\n     "));
            }

            Console.WriteLine(failures == 0
                ? "Script Check passed " + files.Count + " file(s)"
                : failures + " of " + files.Count + " file(s) failed the Script Check");
            return failures == 0 ? 0 : 1;
        }
    }
}
