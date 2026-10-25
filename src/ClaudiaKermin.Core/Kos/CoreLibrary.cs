// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>One kerboscript file of the Core Library, as shipped inside the core assembly.</summary>
    public sealed class LibraryFile
    {
        public LibraryFile(string name, string source)
        {
            Name = name;
            Source = source.Replace("\r\n", "\n");
            Docs = Source.Split('\n')
                .Select(line => line.TrimStart())
                .Where(line => line.StartsWith(CoreLibrary.DocPrefix, StringComparison.Ordinal))
                .Select(line => line.Substring(CoreLibrary.DocPrefix.Length).TrimStart(' ').TrimEnd())
                .ToList();
        }

        /// <summary>The file name without <c>.ks</c>: the block's name, or <c>claudia_lib</c>.</summary>
        public string Name { get; private set; }

        public string Source { get; private set; }

        /// <summary>The file's <c>//|</c> lines: what Claudia reads about it. A block's first line is its summary.</summary>
        public IList<string> Docs { get; private set; }

        public bool IsBlock
        {
            get { return Name != CoreLibrary.SharedFile; }
        }
    }

    /// <summary>
    /// The Core Library: the Building Blocks (<c>kerboscript/library/</c>) compiled into the core
    /// assembly. The mod bundles them with each Mission Script into one compile unit, and their
    /// <c>//|</c> doc lines become the Building Block part of Claudia's cached prompt layers.
    /// </summary>
    public static class CoreLibrary
    {
        public const string DocPrefix = "//|";
        public const string SharedFile = "claudia_lib";
        private const string ResourcePrefix = "ClaudiaKermin.Core.library.";
        private const string RsvpResourcePrefix = "ClaudiaKermin.Core.rsvp.";
        private const string VendoredPrefix = "ClaudiaKermin.Core.kslib.";

        private static readonly Lazy<IList<LibraryFile>> LoadedFiles = new Lazy<IList<LibraryFile>>(Load);
        private static readonly Lazy<IList<LibraryFile>> LoadedRsvp = new Lazy<IList<LibraryFile>>(LoadRsvp);
        private static readonly Lazy<IList<LibraryFile>> LoadedVendored = new Lazy<IList<LibraryFile>>(LoadVendored);

        /// <summary>The shared helpers first, then every block by name.</summary>
        public static IList<LibraryFile> Files
        {
            get { return LoadedFiles.Value; }
        }

        /// <summary>
        /// Third-party scripts (<c>kerboscript/kslib/</c>) bundled as they are: part of the compile unit,
        /// but not Building Blocks, so they are not in <see cref="Files"/> and add nothing to the docs.
        /// </summary>
        public static IList<LibraryFile> Vendored
        {
            get { return LoadedVendored.Value; }
        }

        public static IEnumerable<LibraryFile> Blocks
        {
            get { return Files.Where(f => f.IsBlock); }
        }

        /// <summary>
        /// RSVP v9's files (<c>kerboscript/rsvp/</c>), by name. Not Building Blocks: <c>transfer_to</c> loads
        /// them through <c>transfer_to_rsvp()</c>; see <c>kerboscript/rsvp/NOTE.md</c>.
        /// </summary>
        public static IList<LibraryFile> Rsvp
        {
            get { return LoadedRsvp.Value; }
        }

        /// <summary>
        /// One compile unit: the Mission Script first (so kOS's line numbers in its errors match what
        /// Claudia wrote), then the Core Library, then RSVP (each file as a function
        /// <c>rsvp_file_&lt;name&gt;</c>). There is no wait cap to pass in: waits warp inside the script (ADR 0004).
        /// kOS hoists file-level functions, so the script can call blocks defined after it. The
        /// library's own <c>@lazyglobal</c> and <c>@clobberbuiltins</c> lines are dropped: kOS allows the directive only at the top.
        /// </summary>
        public static string Bundle(string missionScript)
        {
            var text = new StringBuilder();
            text.Append((missionScript ?? "").Replace("\r\n", "\n").TrimEnd('\n')).Append("\n\n");
            text.Append("// ---- The Core Library, bundled by Claudia Kermin ----\n");
            foreach (var file in Files.Concat(Vendored))
            {
                foreach (var line in file.Source.Split('\n'))
                {
                    if (IsDirective(line)) continue;
                    text.Append(line).Append('\n');
                }
            }
            // RSVP's files are programs of their own (parameter export., file-level locals), so each
            // becomes the body of one function, called by transfer_to_rsvp() (kerboscript/rsvp/NOTE.md).
            text.Append("// ---- RSVP v9 (GPL-3.0, https://github.com/maneatingape/rsvp), bundled by Claudia Kermin ----\n");
            foreach (var file in Rsvp)
            {
                text.Append("function rsvp_file_").Append(file.Name).Append(" {\n");
                foreach (var line in file.Source.Split('\n'))
                {
                    if (line.TrimStart().StartsWith("@lazyglobal", StringComparison.OrdinalIgnoreCase)) continue;
                    text.Append(line).Append('\n');
                }
                text.Append("}\n");
            }
            return text.ToString();
        }

        /// <summary>For the base layer: the shared rules and a one-line summary of every block.</summary>
        public static string Summaries()
        {
            var text = new StringBuilder();
            text.Append(string.Join("\n", Files.First(f => !f.IsBlock).Docs.ToArray())).Append("\n\nThe Building Blocks:");
            foreach (var block in Blocks) text.Append("\n- ").Append(block.Docs.Count > 0 ? block.Docs[0] : block.Name);
            return text.ToString();
        }

        /// <summary>For the per-Mission layer: the full docs of the given blocks (all of them by default).</summary>
        public static string FullDocs(IEnumerable<string> blockNames = null)
        {
            var wanted = blockNames == null ? null : new HashSet<string>(blockNames, StringComparer.OrdinalIgnoreCase);
            return string.Join("\n\n", Blocks
                .Where(b => wanted == null || wanted.Contains(b.Name))
                .Select(b => string.Join("\n", b.Docs.ToArray()))
                .ToArray());
        }

        private static bool IsDirective(string line)
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("@lazyglobal", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("@clobberbuiltins", StringComparison.OrdinalIgnoreCase);
        }

        private static IList<LibraryFile> Load()
        {
            return Resources(ResourcePrefix)
                .OrderBy(f => f.IsBlock ? 1 : 0)
                .ThenBy(f => f.Name, StringComparer.Ordinal)
                .ToList();
        }

        private static IList<LibraryFile> LoadRsvp()
        {
            return Resources(RsvpResourcePrefix).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        }

        private static IList<LibraryFile> LoadVendored()
        {
            return Resources(VendoredPrefix).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        }

        private static List<LibraryFile> Resources(string prefix)
        {
            var assembly = typeof(CoreLibrary).Assembly;
            var files = new List<LibraryFile>();
            foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
            {
                using (var stream = assembly.GetManifestResourceStream(resource))
                using (var reader = new StreamReader(stream))
                {
                    var name = resource.Substring(prefix.Length);
                    files.Add(new LibraryFile(name.Substring(0, name.Length - ".ks".Length), reader.ReadToEnd()));
                }
            }
            return files;
        }
    }
}
