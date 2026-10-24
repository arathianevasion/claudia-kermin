// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using kOS.Safe.Execution;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// Where kOS thinks an in-memory script came from. Nothing exists on any volume: kOS asks it for
    /// source lines only to show them in error messages.
    /// </summary>
    internal sealed class SourcePath : InternalPath
    {
        private readonly string _label;
        private readonly string[] _lines;

        public SourcePath(string label, string source)
        {
            _label = label;
            _lines = source.Replace("\r\n", "\n").Split('\n');
        }

        public override string Line(int line)
        {
            return line >= 1 && line <= _lines.Length ? _lines[line - 1] : "";
        }

        public override string ToString()
        {
            return _label;
        }
    }
}
