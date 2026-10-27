// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using kOS.Safe.Encapsulation;

namespace ClaudiaKermin.KOS
{
    /// <summary>Turns kOS values into plain C# ones the core can hold, nested lexicons and lists included.</summary>
    public static class KosValues
    {
        public static Dictionary<string, object> ToDictionary(Lexicon lexicon)
        {
            var result = new Dictionary<string, object>();
            foreach (var entry in lexicon) result[entry.Key.ToString()] = ToPlain(entry.Value);
            return result;
        }

        /// <summary>Lexicon → dictionary, list → list, scalar → double, boolean → bool, anything else → its string.</summary>
        public static object ToPlain(Structure value)
        {
            var lexicon = value as Lexicon;
            if (lexicon != null) return ToDictionary(lexicon);
            var list = value as ListValue;
            if (list != null) return list.Select(ToPlain).ToList();
            var scalar = value as ScalarValue;
            if (scalar != null) return scalar.GetDoubleValue();
            var boolean = value as BooleanValue;
            if (boolean != null) return boolean.Value;
            return value == null ? null : value.ToString();
        }

        /// <summary>The reverse of <see cref="ToPlain"/>: dictionary → lexicon, list → list, double → scalar, bool → boolean, anything else → its string.</summary>
        public static Structure FromPlain(object value)
        {
            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null)
            {
                var lexicon = new Lexicon();
                foreach (var entry in dictionary) lexicon.Add(new StringValue(entry.Key), FromPlain(entry.Value));
                return lexicon;
            }
            var items = value as IList<object>;
            if (items != null)
            {
                var list = new ListValue();
                foreach (var item in items) list.Add(FromPlain(item));
                return list;
            }
            if (value is double) return ScalarValue.Create((double)value);
            if (value is bool) return new BooleanValue((bool)value);
            return new StringValue(value == null ? "" : value.ToString());
        }

        /// <summary>A one-line, kerboscript-like rendering of a converted report, for logs and the debug window.</summary>
        public static string Format(object value)
        {
            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null)
            {
                var text = new StringBuilder("{");
                var first = true;
                foreach (var entry in dictionary)
                {
                    text.Append(first ? " " : ", ").Append(entry.Key).Append(": ").Append(Format(entry.Value));
                    first = false;
                }
                return text.Append(" }").ToString();
            }
            var list = value as IList<object>;
            if (list != null) return "[" + string.Join(", ", list.Select(Format).ToArray()) + "]";
            if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is string) return "\"" + value + "\"";
            return value == null ? "null" : value.ToString();
        }
    }
}
