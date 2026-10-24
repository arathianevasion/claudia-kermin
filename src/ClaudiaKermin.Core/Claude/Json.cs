// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClaudiaKermin.Core.Claude
{
    internal enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>
    /// A parsed JSON value that remembers the exact source text it came from (<see cref="Raw"/>), so
    /// blocks the client does not understand can be sent back byte for byte.
    /// </summary>
    internal sealed class JsonNode
    {
        public JsonKind Kind;
        public string Raw;
        public string StringValue;
        public bool BoolValue;
        public readonly List<KeyValuePair<string, JsonNode>> Members = new List<KeyValuePair<string, JsonNode>>();
        public readonly List<JsonNode> Items = new List<JsonNode>();

        /// <summary>The member's value, or null when this is not an object or has no such member.</summary>
        public JsonNode this[string name]
        {
            get
            {
                if (Kind != JsonKind.Object) return null;
                foreach (var member in Members)
                    if (member.Key == name) return member.Value;
                return null;
            }
        }

        /// <summary>The string value, or null for anything that is not a JSON string.</summary>
        public static string Str(JsonNode node)
        {
            return node != null && node.Kind == JsonKind.String ? node.StringValue : null;
        }

        /// <summary>The number's value, or null for anything that is not a JSON number.</summary>
        public static double? Double(JsonNode node)
        {
            double value;
            if (node == null || node.Kind != JsonKind.Number) return null;
            return double.TryParse(node.Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : (double?)null;
        }

        public static bool? Bool(JsonNode node)
        {
            return node != null && node.Kind == JsonKind.Bool ? node.BoolValue : (bool?)null;
        }

        /// <summary>The integer value, or null for anything that is not a whole JSON number.</summary>
        public static long? Long(JsonNode node)
        {
            long value;
            if (node == null || node.Kind != JsonKind.Number) return null;
            return long.TryParse(node.Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) ? value : (long?)null;
        }
    }

    internal sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }

    /// <summary>A strict JSON reader (RFC 8259) that keeps each value's raw text.</summary>
    internal sealed class JsonReader
    {
        private const int MaxDepth = 128;

        private readonly string _text;
        private int _pos;

        private JsonReader(string text)
        {
            _text = text;
        }

        public static JsonNode Parse(string text)
        {
            if (text == null) throw new JsonException("no text");
            var reader = new JsonReader(text);
            reader.SkipSpace();
            var node = reader.Value(0);
            reader.SkipSpace();
            if (reader._pos != text.Length) throw new JsonException("trailing text at " + reader._pos);
            return node;
        }

        /// <summary>True when <paramref name="text"/> is one complete JSON value.</summary>
        public static bool IsValid(string text)
        {
            try
            {
                Parse(text);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private JsonNode Value(int depth)
        {
            if (depth > MaxDepth) throw new JsonException("nested too deep");
            if (_pos >= _text.Length) throw new JsonException("unexpected end");
            var start = _pos;
            var node = new JsonNode();
            var c = _text[_pos];
            if (c == '{')
            {
                node.Kind = JsonKind.Object;
                _pos++;
                SkipSpace();
                if (Peek() == '}') _pos++;
                else
                {
                    while (true)
                    {
                        SkipSpace();
                        if (Peek() != '"') throw new JsonException("expected a member name at " + _pos);
                        var name = StringBody();
                        SkipSpace();
                        Expect(':');
                        SkipSpace();
                        node.Members.Add(new KeyValuePair<string, JsonNode>(name, Value(depth + 1)));
                        SkipSpace();
                        if (Peek() == ',') { _pos++; continue; }
                        Expect('}');
                        break;
                    }
                }
            }
            else if (c == '[')
            {
                node.Kind = JsonKind.Array;
                _pos++;
                SkipSpace();
                if (Peek() == ']') _pos++;
                else
                {
                    while (true)
                    {
                        SkipSpace();
                        node.Items.Add(Value(depth + 1));
                        SkipSpace();
                        if (Peek() == ',') { _pos++; continue; }
                        Expect(']');
                        break;
                    }
                }
            }
            else if (c == '"')
            {
                node.Kind = JsonKind.String;
                node.StringValue = StringBody();
            }
            else if (Word("true")) { node.Kind = JsonKind.Bool; node.BoolValue = true; }
            else if (Word("false")) { node.Kind = JsonKind.Bool; }
            else if (Word("null")) { node.Kind = JsonKind.Null; }
            else
            {
                node.Kind = JsonKind.Number;
                Number();
            }
            node.Raw = _text.Substring(start, _pos - start);
            return node;
        }

        private string StringBody()
        {
            Expect('"');
            var builder = new StringBuilder();
            while (true)
            {
                if (_pos >= _text.Length) throw new JsonException("unterminated string");
                var c = _text[_pos++];
                if (c == '"') return builder.ToString();
                if (c < 0x20) throw new JsonException("control character in string");
                if (c != '\\') { builder.Append(c); continue; }
                if (_pos >= _text.Length) throw new JsonException("unterminated escape");
                var e = _text[_pos++];
                switch (e)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        int code;
                        if (_pos + 4 > _text.Length
                            || !int.TryParse(_text.Substring(_pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                            throw new JsonException("bad \\u escape");
                        builder.Append((char)code);
                        _pos += 4;
                        break;
                    default: throw new JsonException("bad escape");
                }
            }
        }

        private void Number()
        {
            var start = _pos;
            if (Peek() == '-') _pos++;
            if (Peek() == '0') _pos++;
            else if (Digits() == 0) throw new JsonException("bad value at " + start);
            if (Peek() == '.')
            {
                _pos++;
                if (Digits() == 0) throw new JsonException("bad number at " + start);
            }
            if (Peek() == 'e' || Peek() == 'E')
            {
                _pos++;
                if (Peek() == '+' || Peek() == '-') _pos++;
                if (Digits() == 0) throw new JsonException("bad number at " + start);
            }
        }

        private int Digits()
        {
            var start = _pos;
            while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9') _pos++;
            return _pos - start;
        }

        private bool Word(string word)
        {
            if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0) return false;
            _pos += word.Length;
            return true;
        }

        private char Peek()
        {
            return _pos < _text.Length ? _text[_pos] : '\0';
        }

        private void Expect(char c)
        {
            if (Peek() != c) throw new JsonException("expected '" + c + "' at " + _pos);
            _pos++;
        }

        private void SkipSpace()
        {
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r') return;
                _pos++;
            }
        }
    }

    /// <summary>Builds compact JSON text. Non-ASCII is written as is; the body goes out as UTF-8.</summary>
    internal sealed class JsonWriter
    {
        private readonly StringBuilder _out = new StringBuilder();
        private readonly Stack<bool> _first = new Stack<bool>();
        private bool _afterName;

        public JsonWriter BeginObject() { Separate(); _out.Append('{'); _first.Push(true); return this; }
        public JsonWriter EndObject() { _first.Pop(); _out.Append('}'); return this; }
        public JsonWriter BeginArray() { Separate(); _out.Append('['); _first.Push(true); return this; }
        public JsonWriter EndArray() { _first.Pop(); _out.Append(']'); return this; }

        public JsonWriter Name(string name)
        {
            Separate();
            Quote(name);
            _out.Append(':');
            _afterName = true;
            return this;
        }

        public JsonWriter String(string value)
        {
            Separate();
            if (value == null) _out.Append("null");
            else Quote(value);
            return this;
        }

        public JsonWriter Number(long value) { Separate(); _out.Append(value.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Bool(bool value) { Separate(); _out.Append(value ? "true" : "false"); return this; }

        /// <summary>Round-trippable; NaN and infinities (which JSON can't hold) are written as null.</summary>
        public JsonWriter Number(double value)
        {
            Separate();
            if (double.IsNaN(value) || double.IsInfinity(value)) _out.Append("null");
            else _out.Append(value.ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Field(string name, double value) { return Name(name).Number(value); }
        public JsonWriter Null() { Separate(); _out.Append("null"); return this; }

        /// <summary>Writes already-encoded JSON text exactly as given.</summary>
        public JsonWriter Raw(string json) { Separate(); _out.Append(json); return this; }

        public JsonWriter Field(string name, string value) { return Name(name).String(value); }
        public JsonWriter Field(string name, long value) { return Name(name).Number(value); }
        public JsonWriter Field(string name, bool value) { return Name(name).Bool(value); }

        public override string ToString() { return _out.ToString(); }

        private void Separate()
        {
            if (_afterName) { _afterName = false; return; }
            if (_first.Count == 0) return;
            if (_first.Peek()) { _first.Pop(); _first.Push(false); }
            else _out.Append(',');
        }

        private void Quote(string value)
        {
            _out.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': _out.Append("\\\""); break;
                    case '\\': _out.Append("\\\\"); break;
                    case '\n': _out.Append("\\n"); break;
                    case '\r': _out.Append("\\r"); break;
                    case '\t': _out.Append("\\t"); break;
                    case '\b': _out.Append("\\b"); break;
                    case '\f': _out.Append("\\f"); break;
                    default:
                        if (c < 0x20) _out.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else _out.Append(c);
                        break;
                }
            }
            _out.Append('"');
        }
    }
}
