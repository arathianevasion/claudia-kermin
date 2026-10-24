// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;

namespace ClaudiaKermin.Core.Config
{
    /// <summary>
    /// The player config file (outside the save). This slice holds only <c>debug_tools</c>; later
    /// tickets add the rest of the spec's settings. Format: <c>name = value</c> lines, <c>#</c> comments.
    /// </summary>
    public sealed class PlayerConfig
    {
        public const string FileName = "config.cfg";

        public bool DebugTools;

        /// <summary>Loads the file, writing it with defaults first if it does not exist.</summary>
        public static PlayerConfig LoadOrCreate(string directory)
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, "# Claudia Kermin player config\ndebug_tools = false\n");
            }

            var config = new PlayerConfig();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var name = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                if (name == "debug_tools") config.DebugTools = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
            return config;
        }
    }
}
