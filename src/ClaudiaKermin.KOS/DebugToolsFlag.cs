// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// Reads <c>debug_tools</c> from the player config (macOS:
    /// <c>~/Library/Application Support/ClaudiaKermin/config.cfg</c>). A stopgap: the player config
    /// arrives with the API key ticket (#38), and this should read it from there once that merges.
    /// Off unless the file says <c>debug_tools = true</c>.
    /// </summary>
    internal static class DebugToolsFlag
    {
        public static bool Read()
        {
            try
            {
                var home = Environment.GetEnvironmentVariable("HOME") ?? "";
                var path = Path.Combine(home, "Library/Application Support/ClaudiaKermin/config.cfg");
                if (!File.Exists(path)) return false;
                foreach (var line in File.ReadAllLines(path))
                {
                    var equals = line.IndexOf('=');
                    if (equals < 0 || line.Substring(0, equals).Trim() != "debug_tools") continue;
                    return string.Equals(line.Substring(equals + 1).Trim(), "true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return false;
        }
    }
}
