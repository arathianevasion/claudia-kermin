// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>
    /// One debug block test (spec, Testing: seam 3), read from its <c>test.cfg</c>: which block, the
    /// reference craft and the orbit to fly it from, how long it may take, and the contract ranges its
    /// Script Result must meet. Format: <c>name = value</c> lines, <c>#</c> comments; each
    /// <c>check = field op value</c> compares one result field (dotted for nested lexicons, e.g.
    /// <c>orbit.ap</c>) with <c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c> or <c>&gt;=</c>.
    /// </summary>
    public sealed class BlockTestSpec
    {
        private static readonly string[] Operators = { "==", "!=", "<=", ">=", "<", ">" };

        public string Name { get; private set; }
        public string Block { get; private set; }
        public string Craft { get; private set; }
        public string OrbitBody { get; private set; }
        public double OrbitAltitude { get; private set; }
        public double OrbitInclination { get; private set; }

        /// <summary>
        /// Periapsis altitude in m, for a test that starts on an eccentric orbit (a suborbital hop has one
        /// below the ground). Null: circular at <c>orbit_altitude</c>. Otherwise <c>orbit_altitude</c> is the
        /// apoapsis and the craft starts there.
        /// </summary>
        public double? OrbitPeriapsis { get; private set; }

        /// <summary>Real seconds the run may take, warp included, before it is aborted and fails.</summary>
        public double TimeoutSeconds { get; private set; }

        public IList<string> Checks { get; private set; }

        /// <summary>Reads a spec; throws <see cref="FormatException"/> if it names no block or has a malformed check.</summary>
        public static BlockTestSpec Parse(string name, string text)
        {
            var spec = new BlockTestSpec
            {
                Name = name,
                Craft = "reference.craft",
                OrbitBody = "Kerbin",
                OrbitAltitude = 100000,
                TimeoutSeconds = 900,
                Checks = new List<string>(),
            };
            foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var eq = line.IndexOf('=');
                if (eq < 0) throw new FormatException(name + ": not 'name = value': " + line);
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "block": spec.Block = value; break;
                    case "craft": spec.Craft = value; break;
                    case "orbit_body": spec.OrbitBody = value; break;
                    case "orbit_altitude": spec.OrbitAltitude = Number(name, value); break;
                    case "orbit_inclination": spec.OrbitInclination = Number(name, value); break;
                    case "orbit_pe": spec.OrbitPeriapsis = Number(name, value); break;
                    case "timeout_s": spec.TimeoutSeconds = Number(name, value); break;
                    case "check":
                        Split(name, value);
                        spec.Checks.Add(value);
                        break;
                    default: throw new FormatException(name + ": unknown setting '" + key + "'");
                }
            }
            if (string.IsNullOrEmpty(spec.Block)) throw new FormatException(name + ": no block = ...");
            return spec;
        }

        /// <summary>Pass only if the run reported and every check holds.</summary>
        public BlockTestResult Judge(RunOutcome outcome)
        {
            var lines = new List<string>();
            if (outcome.Kind != RunOutcomeKind.Finished || outcome.Report == null)
            {
                lines.Add("the run ended " + outcome.Kind + (outcome.Reason.Length > 0 ? ": " + outcome.Reason : ""));
                if (outcome.ErrorText.Length > 0) lines.Add(outcome.ErrorText);
                return new BlockTestResult(false, "FAIL " + Name + ": " + lines[0], lines);
            }

            var passed = true;
            foreach (var check in Checks)
            {
                var parts = Split(Name, check);
                object actual;
                var found = TryField(outcome.Report, parts[0], out actual);
                var ok = found && Holds(actual, parts[1], parts[2]);
                passed &= ok;
                lines.Add((ok ? "ok   " : "FAIL ") + check + (found ? " (got " + Format(actual) + ")" : " (missing)"));
            }
            object reason;
            var why = TryField(outcome.Report, "reason", out reason) && Format(reason).Length > 0 ? Format(reason) : "a check failed";
            return new BlockTestResult(passed, passed ? "PASS " + Name : "FAIL " + Name + ": " + why, lines);
        }

        public BlockTestResult TimedOut()
        {
            var headline = "FAIL " + Name + ": timed out after " + TimeoutSeconds.ToString("0", CultureInfo.InvariantCulture) + " s";
            return new BlockTestResult(false, headline, new List<string>());
        }

        private static double Number(string name, string value)
        {
            double number;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                throw new FormatException(name + ": not a number: " + value);
            return number;
        }

        /// <summary>field, operator, expected value.</summary>
        private static string[] Split(string name, string check)
        {
            foreach (var op in Operators)
            {
                var at = check.IndexOf(" " + op + " ", StringComparison.Ordinal);
                if (at < 0 && check.EndsWith(" " + op, StringComparison.Ordinal)) at = check.Length - op.Length - 1;
                if (at <= 0) continue;
                var rest = check.Substring(at + op.Length + 1);
                return new[] { check.Substring(0, at).Trim(), op, rest.Trim() };
            }
            throw new FormatException(name + ": a check needs 'field op value': " + check);
        }

        private static bool TryField(IDictionary<string, object> report, string path, out object value)
        {
            value = report;
            foreach (var key in path.Split('.'))
            {
                var dictionary = value as IDictionary<string, object>;
                // kOS lexicon keys are case-insensitive.
                var match = dictionary == null ? null : dictionary.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    value = null;
                    return false;
                }
                value = dictionary[match];
            }
            return true;
        }

        private static bool Holds(object actual, string op, string expected)
        {
            double expectedNumber;
            if (IsNumber(actual) && double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out expectedNumber))
            {
                var number = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
                switch (op)
                {
                    case "==": return number == expectedNumber;
                    case "!=": return number != expectedNumber;
                    case "<": return number < expectedNumber;
                    case "<=": return number <= expectedNumber;
                    case ">": return number > expectedNumber;
                    case ">=": return number >= expectedNumber;
                }
            }
            var equal = string.Equals(Format(actual), expected, StringComparison.OrdinalIgnoreCase);
            if (op == "==") return equal;
            if (op == "!=") return !equal;
            return false;
        }

        private static bool IsNumber(object value)
        {
            return value is double || value is float || value is int || value is long || value is decimal;
        }

        private static string Format(object value)
        {
            return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    public sealed class BlockTestResult
    {
        public BlockTestResult(bool passed, string headline, IList<string> lines)
        {
            Passed = passed;
            Headline = headline;
            Lines = lines;
        }

        public bool Passed { get; private set; }

        /// <summary>"PASS name" or "FAIL name: why".</summary>
        public string Headline { get; private set; }

        /// <summary>One line per check (or why the run failed).</summary>
        public IList<string> Lines { get; private set; }
    }
}
