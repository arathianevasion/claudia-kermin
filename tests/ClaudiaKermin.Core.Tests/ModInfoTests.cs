// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class ModInfoTests
    {
        [Fact]
        public void LoadedLineNamesTheModAndItsVersion()
        {
            Assert.Equal("[ClaudiaKermin] Claudia Kermin " + ModInfo.Version + " loaded", ModInfo.LoadedLine);
        }

        [Fact]
        public void VersionHasNoSourceRevisionSuffix()
        {
            Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", ModInfo.Version);
        }

        [Fact]
        public void CoreReferencesNoUnityOrKspAssemblies()
        {
            foreach (var reference in typeof(ModInfo).Assembly.GetReferencedAssemblies())
            {
                Assert.DoesNotContain("Unity", reference.Name);
                Assert.DoesNotContain("Assembly-CSharp", reference.Name);
                Assert.DoesNotContain("KSP", reference.Name);
            }
        }
    }
}
