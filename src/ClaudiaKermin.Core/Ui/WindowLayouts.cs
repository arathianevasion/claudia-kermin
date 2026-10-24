// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ClaudiaKermin.Core.Ui
{
    /// <summary>A window's position and size in screen pixels, top-left origin.</summary>
    public struct WindowBox
    {
        public float X, Y, Width, Height;

        public WindowBox(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// Where each window sat last time, per scene, kept in the mod's config folder (never the save), so the
    /// same window can sit in different places in the Space Center, the VAB/SPH and Flight. Format:
    /// <c>scene.window = x, y, width, height</c> lines, <c>#</c> comments; a line that doesn't parse is ignored.
    /// </summary>
    public sealed class WindowLayouts
    {
        public const string FileName = "windows.cfg";

        private readonly Dictionary<string, WindowBox> _boxes = new Dictionary<string, WindowBox>(StringComparer.Ordinal);

        public bool TryGet(string scene, string window, out WindowBox box)
        {
            return _boxes.TryGetValue(Key(scene, window), out box);
        }

        /// <summary>Remembers a box; returns false when it is the one already remembered.</summary>
        public bool Set(string scene, string window, WindowBox box)
        {
            box = new WindowBox((float)Math.Round(box.X), (float)Math.Round(box.Y), (float)Math.Round(box.Width), (float)Math.Round(box.Height));
            WindowBox old;
            if (_boxes.TryGetValue(Key(scene, window), out old) && old.Equals(box)) return false;
            _boxes[Key(scene, window)] = box;
            return true;
        }

        public static WindowLayouts Load(string directory)
        {
            var layouts = new WindowLayouts();
            var path = Path.Combine(directory, FileName);
            try
            {
                if (File.Exists(path)) layouts.Parse(File.ReadAllLines(path));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Unreadable: windows start at their defaults.
            }
            return layouts;
        }

        /// <summary>Writes the file; a folder that can't be written to just means positions aren't remembered.</summary>
        public void Save(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, FileName), Format());
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        public void Parse(IEnumerable<string> lines)
        {
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var eq = line.IndexOf('=');
                if (eq <= 0 || line.IndexOf('.') < 0 || line.IndexOf('.') > eq) continue;
                var key = line.Substring(0, eq).Trim();
                var numbers = line.Substring(eq + 1).Split(',');
                var values = new float[4];
                var ok = numbers.Length == 4;
                for (var i = 0; ok && i < 4; i++)
                    ok = float.TryParse(numbers[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                        && !float.IsNaN(values[i]) && !float.IsInfinity(values[i]);
                if (!ok || values[2] <= 0 || values[3] <= 0) continue;
                _boxes[key] = new WindowBox(values[0], values[1], values[2], values[3]);
            }
        }

        public string Format()
        {
            var text = new StringBuilder("# Claudia Kermin window positions and sizes, per scene. Written by the mod.\n");
            var keys = new List<string>(_boxes.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var box = _boxes[key];
                text.Append(key).Append(" = ")
                    .Append(box.X.ToString("0", CultureInfo.InvariantCulture)).Append(", ")
                    .Append(box.Y.ToString("0", CultureInfo.InvariantCulture)).Append(", ")
                    .Append(box.Width.ToString("0", CultureInfo.InvariantCulture)).Append(", ")
                    .Append(box.Height.ToString("0", CultureInfo.InvariantCulture)).Append('\n');
            }
            return text.ToString();
        }

        private static string Key(string scene, string window)
        {
            return scene + "." + window;
        }
    }
}
