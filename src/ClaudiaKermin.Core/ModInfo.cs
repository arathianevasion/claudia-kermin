// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Reflection;

namespace ClaudiaKermin.Core
{
    /// <summary>The mod's name and version, and the line it logs when KSP loads it.</summary>
    public static class ModInfo
    {
        public const string Name = "Claudia Kermin";

        /// <summary>Prefix for every line the mod writes to the KSP log.</summary>
        public const string LogTag = "[ClaudiaKermin]";

        public static string Version
        {
            get
            {
                var informational = typeof(ModInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                var version = informational != null ? informational.InformationalVersion : typeof(ModInfo).Assembly.GetName().Version.ToString();
                // Drop any "+<commit>" source-revision suffix the SDK appends.
                var plus = version.IndexOf('+');
                return plus < 0 ? version : version.Substring(0, plus);
            }
        }

        public static string LoadedLine
        {
            get { return LogTag + " " + Name + " " + Version + " loaded"; }
        }
    }
}
