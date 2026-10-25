// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Text;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>
    /// Pretty-prints a Mission Script for display: one statement per line, the body of every { } block indented two
    /// spaces, blank lines kept. Display only: the stored and run script are never this text. Strings and // comments are
    /// skipped, so braces and dots inside them are left alone. A script it can't read (an unbalanced brace or an
    /// unterminated string) comes back as it was.
    /// </summary>
    public static class ScriptFormatter
    {
        public static string Format(string script)
        {
            if (string.IsNullOrEmpty(script)) return script ?? "";
            return new Writer().Run(script) ?? script;
        }

        private sealed class Writer
        {
            private readonly StringBuilder _output = new StringBuilder();
            private readonly StringBuilder _line = new StringBuilder();
            private string _eol = "\n";
            private int _depth;
            // The depth a line starts at is its indent, whatever braces it opens or closes after that.
            private int _lineDepth;

            // Null when the script is malformed.
            public string Run(string script)
            {
                if (script.Contains("\r\n")) _eol = "\r\n";
                var text = script.Replace("\r\n", "\n");
                for (var i = 0; i < text.Length; i++)
                {
                    var c = text[i];
                    if (c == '\n')
                    {
                        EndLine();
                    }
                    else if (c == '"')
                    {
                        var close = text.IndexOf('"', i + 1);
                        // A string that never closes, or runs onto another line, isn't a script we can read.
                        if (close < 0 || text.IndexOf('\n', i + 1, close - i) >= 0) return null;
                        Add(text.Substring(i, close - i + 1));
                        i = close;
                    }
                    else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                    {
                        var end = text.IndexOf('\n', i);
                        if (end < 0) end = text.Length;
                        Add(text.Substring(i, end - i));
                        i = end - 1;
                    }
                    else if (c == '{')
                    {
                        Add("{");
                        if (!RestOfLineIsEmpty(text, i + 1)) EndLine();
                        _depth++;
                    }
                    else if (c == '}')
                    {
                        _depth--;
                        if (_depth < 0) return null;
                        // The closing brace goes at the block's own level.
                        if (_line.ToString().Trim().Length > 0) EndLine();
                        Add("}");
                        if (!RestOfLineIsEmpty(text, i + 1) && !ElseFollows(text, i + 1)) EndLine();
                    }
                    else if (c == '.' && IsTerminator(text, i))
                    {
                        Add(".");
                        if (!RestOfLineIsEmpty(text, i + 1)) EndLine();
                    }
                    else if (_line.Length > 0 || (c != ' ' && c != '\t'))
                    {
                        Add(c.ToString());
                    }
                }
                if (_depth != 0) return null;
                WriteLine();
                return _output.ToString();
            }

            private void Add(string piece)
            {
                if (_line.Length == 0) _lineDepth = _depth;
                _line.Append(piece);
            }

            private void EndLine()
            {
                WriteLine();
                _output.Append(_eol);
            }

            private void WriteLine()
            {
                var content = _line.ToString().TrimEnd(' ', '\t');
                _line.Length = 0;
                if (content.Length == 0) return;
                _output.Append(' ', 2 * _lineDepth).Append(content);
            }
        }

        // A statement's full stop: not the point in 1.5, a suffix, or a file name like foo.ks.
        private static bool IsTerminator(string text, int i)
        {
            if (i + 1 >= text.Length) return true;
            var next = text[i + 1];
            return !(char.IsLetterOrDigit(next) || next == '_');
        }

        // Nothing but spaces before the end of the line, the end of the script, or a // comment.
        private static bool RestOfLineIsEmpty(string text, int from)
        {
            for (var i = from; i < text.Length; i++)
            {
                var c = text[i];
                if (c == ' ' || c == '\t') continue;
                return c == '\n' || (c == '/' && i + 1 < text.Length && text[i + 1] == '/');
            }
            return true;
        }

        // "} else": the else stays on the brace's line.
        private static bool ElseFollows(string text, int from)
        {
            var i = from;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            return string.CompareOrdinal(text, i, "else", 0, 4) == 0
                && (i + 4 >= text.Length || !(char.IsLetterOrDigit(text[i + 4]) || text[i + 4] == '_'));
        }
    }
}
