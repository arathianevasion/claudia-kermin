// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;

namespace ClaudiaKermin.Core.Config
{
    public enum HostOs { MacOS, Windows, Linux }

    /// <summary>Resolves the player's config folder per OS. Always under the user profile, never GameData.</summary>
    public static class ConfigDirectory
    {
        public const string FolderName = "ClaudiaKermin";

        public static string Resolve(HostOs os, string home, Func<string, string> env)
        {
            switch (os)
            {
                case HostOs.MacOS:
                    return Path.Combine(home, "Library", "Application Support", FolderName);
                case HostOs.Windows:
                    var appData = env("APPDATA");
                    return Path.Combine(string.IsNullOrEmpty(appData) ? Path.Combine(home, "AppData", "Roaming") : appData, FolderName);
                default:
                    var xdg = env("XDG_CONFIG_HOME");
                    return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, FolderName);
            }
        }

        public static HostOs CurrentOs()
        {
            if (Path.DirectorySeparatorChar == '\\') return HostOs.Windows;
            return Directory.Exists("/Library/Application Support") ? HostOs.MacOS : HostOs.Linux;
        }

        public static string ResolveCurrent()
        {
            return Resolve(CurrentOs(), Environment.GetFolderPath(Environment.SpecialFolder.Personal), Environment.GetEnvironmentVariable);
        }
    }
}
