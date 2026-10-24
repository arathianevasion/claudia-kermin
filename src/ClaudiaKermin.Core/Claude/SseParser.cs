// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Text;

namespace ClaudiaKermin.Core.Claude
{
    /// <summary>One server-sent event: its name, its data lines joined by '\n', and its text as received.</summary>
    internal sealed class SseEvent
    {
        public string Name;
        public string Data;
        public string Raw;
    }

    /// <summary>
    /// Turns response bytes into server-sent events (the WHATWG event-stream format): UTF-8 split
    /// across reads, CR, LF and CRLF line ends, comment lines and multi-line data are all handled.
    /// </summary>
    internal sealed class SseParser
    {
        private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
        private readonly StringBuilder _line = new StringBuilder();
        private readonly StringBuilder _raw = new StringBuilder();
        private readonly StringBuilder _data = new StringBuilder();
        private string _name;
        private bool _hasData;
        private bool _lastWasCr;
        private bool _started;
        private char[] _chars = new char[0];

        /// <summary>Feeds bytes; returns the events they complete, in order.</summary>
        public List<SseEvent> Feed(byte[] buffer, int count)
        {
            var events = new List<SseEvent>();
            var needed = _decoder.GetCharCount(buffer, 0, count);
            if (_chars.Length < needed) _chars = new char[needed];
            var length = _decoder.GetChars(buffer, 0, count, _chars, 0);
            for (var i = 0; i < length; i++)
            {
                var c = _chars[i];
                if (!_started)
                {
                    _started = true;
                    if (c == '﻿') continue;
                }
                if (c == '\n' && _lastWasCr)
                {
                    _lastWasCr = false;
                    _raw.Append('\n');
                    continue;
                }
                _lastWasCr = c == '\r';
                if (c == '\r' || c == '\n')
                {
                    _raw.Append(c);
                    EndLine(events);
                }
                else
                {
                    _line.Append(c);
                    _raw.Append(c);
                }
            }
            return events;
        }

        private void EndLine(List<SseEvent> events)
        {
            var line = _line.ToString();
            _line.Length = 0;
            if (line.Length == 0)
            {
                if (_hasData || _name != null)
                {
                    events.Add(new SseEvent
                    {
                        Name = _name ?? "message",
                        Data = _hasData ? _data.ToString() : "",
                        Raw = _raw.ToString(),
                    });
                }
                _raw.Length = 0;
                _data.Length = 0;
                _name = null;
                _hasData = false;
                return;
            }
            if (line[0] == ':') return;

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line.Substring(0, colon);
            var value = colon < 0 ? "" : line.Substring(colon + 1);
            if (value.Length > 0 && value[0] == ' ') value = value.Substring(1);
            if (field == "event") _name = value;
            else if (field == "data")
            {
                if (_hasData) _data.Append('\n');
                _data.Append(value);
                _hasData = true;
            }
        }
    }
}
