// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.IO;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public sealed class WindowLayoutsTests
    {
        [Fact]
        public void EachSceneRemembersItsOwnBox()
        {
            var layouts = new WindowLayouts();
            layouts.Set("flight", "mission", new WindowBox(10, 20, 700, 600));
            layouts.Set("editor", "mission", new WindowBox(300, 40, 900, 500));
            WindowBox flight, editor, other;
            Assert.True(layouts.TryGet("flight", "mission", out flight));
            Assert.True(layouts.TryGet("editor", "mission", out editor));
            Assert.Equal(10, flight.X);
            Assert.Equal(900, editor.Width);
            Assert.False(layouts.TryGet("spacecenter", "mission", out other));
        }

        [Fact]
        public void SettingTheSameBoxAgainIsNotAChange()
        {
            var layouts = new WindowLayouts();
            Assert.True(layouts.Set("flight", "chat", new WindowBox(1, 2, 3, 4)));
            Assert.False(layouts.Set("flight", "chat", new WindowBox(1.2f, 2, 3, 4)));
            Assert.True(layouts.Set("flight", "chat", new WindowBox(5, 2, 3, 4)));
        }

        [Fact]
        public void BoxesSurviveSaveAndLoad()
        {
            var directory = Path.Combine(Path.GetTempPath(), "ck-layouts-" + Path.GetRandomFileName());
            try
            {
                var layouts = new WindowLayouts();
                layouts.Set("flight", "mission", new WindowBox(10, 20, 700, 600));
                layouts.Set("editor", "chat", new WindowBox(0, 5, 320, 260));
                layouts.Save(directory);

                var loaded = WindowLayouts.Load(directory);
                WindowBox box;
                Assert.True(loaded.TryGet("flight", "mission", out box));
                Assert.Equal(new WindowBox(10, 20, 700, 600), box);
                Assert.True(loaded.TryGet("editor", "chat", out box));
                Assert.Equal(260, box.Height);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void AMissingFileMeansDefaults()
        {
            WindowBox box;
            Assert.False(WindowLayouts.Load(Path.Combine(Path.GetTempPath(), "ck-none-" + Path.GetRandomFileName())).TryGet("flight", "mission", out box));
        }

        [Fact]
        public void LinesThatDontParseAreIgnored()
        {
            var layouts = new WindowLayouts();
            layouts.Parse(new[]
            {
                "# comment", "", "nonsense", "flight.mission = 1, 2, 3", "flight.mission = a, b, c, d",
                "flight.mission = 1, 2, 0, 4", "flight.chat = 5, 6, 7, 8", "x = 1, 2, 3, 4",
            });
            WindowBox box;
            Assert.False(layouts.TryGet("flight", "mission", out box));
            Assert.True(layouts.TryGet("flight", "chat", out box));
            Assert.Equal(new WindowBox(5, 6, 7, 8), box);
        }

        [Fact]
        public void TheFileIsOutsideTheSaveAndNamedForWindows()
        {
            Assert.Equal("windows.cfg", WindowLayouts.FileName);
        }

        [Fact]
        public void ForgettingABoxPutsTheWindowBackToItsDefaultInThatSceneOnly()
        {
            var layouts = new WindowLayouts();
            layouts.Set("flight", "capcom", new WindowBox(10, 600, 620, 120));
            layouts.Set("vab", "capcom", new WindowBox(10, 600, 620, 120));
            Assert.True(layouts.Remove("flight", "capcom"));
            Assert.False(layouts.Remove("flight", "capcom"));
            WindowBox box;
            Assert.False(layouts.TryGet("flight", "capcom", out box));
            Assert.True(layouts.TryGet("vab", "capcom", out box));
            Assert.DoesNotContain("flight.capcom", layouts.Format());
        }
    }
}
