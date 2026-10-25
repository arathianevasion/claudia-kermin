// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The runtime Archive gate (#49) refuses any Archive write from a processor running a mod-started
    /// program, so nothing the Core Library bundles (blocks, RSVP, vendored KSLib) may write to 0:.
    /// </summary>
    public class ArchiveWriteTests
    {
        // kOS statements and functions that put a file on a volume or move the processor onto the Archive.
        // Matched against code with comments and string contents removed.
        private static readonly Regex[] Verbs =
        {
            new Regex(@"\blog\b", RegexOptions.IgnoreCase),
            new Regex(@"(^|[^:\w])(copypath|copy|movepath|move|rename|renamepath|writejson|create|createdir|deletepath|delete|edit|compile|open)\b", RegexOptions.IgnoreCase),
            new Regex(@"\bswitch\s+to\b", RegexOptions.IgnoreCase),
            new Regex(@"\bprint\b[^.]*\bto\b\s+\w", RegexOptions.IgnoreCase),
            new Regex(@"\b(volume|archive)\b", RegexOptions.IgnoreCase),
            new Regex(@":\s*(write|writeln|clear)\b", RegexOptions.IgnoreCase),
        };

        // A string literal naming the Archive volume: "0:" or "0:/...".
        private static readonly Regex ArchivePath = new Regex(@"""\s*0\s*:", RegexOptions.IgnoreCase);

        [Fact]
        public void TheCoreLibraryBundleContainsNoArchiveWrite()
        {
            var offences = new List<string>();
            foreach (var file in CoreLibrary.Files.Concat(CoreLibrary.Rsvp).Concat(CoreLibrary.Vendored))
            {
                var n = 0;
                foreach (var raw in file.Source.Split('\n'))
                {
                    n++;
                    if (Offends(raw)) offences.Add(file.Name + ".ks:" + n + ": " + raw.Trim());
                }
            }
            Assert.True(offences.Count == 0, "Archive write in the bundle:\n" + string.Join("\n", offences));
        }

        [Theory]
        [InlineData("log \"x\" to \"0:/log.txt\".")]
        [InlineData("copypath(\"a\", \"0:/b\").")]
        [InlineData("writejson(l, \"0:/a.json\").")]
        [InlineData("create(\"0:/a\").")]
        [InlineData("switch to 0.")]
        [InlineData("print \"x\" to \"0:/y\".")]
        public void TheScanCatchesTheWritesTheGateRefuses(string line)
        {
            Assert.True(Offends(line), line);
        }

        [Theory]
        [InlineData("local best is x:copy.")]
        [InlineData("problem(305, \"Cannot create manuever nodes\").")]
        [InlineData("print \"hello\".")]
        [InlineData("// log it to 0:/ later")]
        public void TheScanIgnoresSuffixCallsStringsCommentsAndPlainPrints(string line)
        {
            Assert.False(Offends(line), line);
        }

        private static bool Offends(string line)
        {
            var code = Strip(line, out var archiveString);
            return archiveString || Verbs.Any(v => v.IsMatch(code));
        }

        // The line without its // comment and with string contents blanked; reports a "0:" string literal.
        private static string Strip(string line, out bool archiveString)
        {
            var sb = new StringBuilder();
            var inString = false;
            archiveString = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (!inString && c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                if (c == '"')
                {
                    if (!inString && ArchivePath.IsMatch(line.Substring(i, System.Math.Min(6, line.Length - i)))) archiveString = true;
                    inString = !inString;
                    sb.Append(c);
                }
                else if (!inString) sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
