// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.IO;
using System.Runtime.InteropServices;

namespace ClaudiaKermin.Core.Config
{
    /// <summary>Owner-only permissions via chmod (net48/Mono has no File.SetUnixFileMode). A no-op on Windows, where the profile folder is already per-user.</summary>
    internal static class OwnerOnly
    {
        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, uint mode);

        private static bool IsUnix
        {
            get { return Path.DirectorySeparatorChar == '/'; }
        }

        public static void Directory(string path)
        {
            Apply(path, 448); // 0700
        }

        public static void File(string path)
        {
            Apply(path, 384); // 0600
        }

        private static void Apply(string path, uint mode)
        {
            if (!IsUnix) return;
            if (chmod(path, mode) != 0)
                throw new IOException("Could not restrict permissions on " + path + " (errno " + Marshal.GetLastWin32Error() + ")");
        }
    }
}
