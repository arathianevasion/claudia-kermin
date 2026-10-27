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
    /// reference craft and the orbit (or launch pad) to fly it from, how long it may take, and the contract ranges its
    /// Script Result must meet. Format: <c>name = value</c> lines, <c>#</c> comments; each
    /// <c>check = field op value</c> compares one result field (dotted for nested lexicons, e.g.
    /// <c>orbit.ap</c>) with <c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c> or <c>&gt;=</c>.
    /// <c>value</c> is a literal, never another field: a number; for <c>==</c> and <c>!=</c> also <c>true</c>,
    /// <c>false</c>, a vessel situation (<c>LANDED</c>), a stock body (<c>Kerbin</c>) or nothing (an empty
    /// string). Anything else (a field name, say) is refused when the spec is read; a test that needs to
    /// compare two fields has its <c>test.ks</c> report the comparison. A field under <c>warp.</c> is read from the run's
    /// warp trace instead (<see cref="WarpTrace.CheckFields"/>, e.g. <c>check = warp.first_request_real_s &lt; 10</c>): the
    /// test's own block, or another block's first run as <c>warp.&lt;block&gt;.&lt;field&gt;</c>.
    /// </summary>
    public sealed class BlockTestSpec
    {
        private static readonly string[] Operators = { "==", "!=", "<=", ">=", "<", ">" };

        /// <summary>A check on <c>warp.&lt;field&gt;</c> reads the run's <see cref="WarpTrace"/> (#335), for the test's own block.</summary>
        private const string WarpPrefix = "warp";

        /// <summary>kOS's <c>status</c> values and the stock bodies' names: the words a check may compare with.</summary>
        private static readonly string[] KnownValues =
        {
            "PRELAUNCH", "LANDED", "SPLASHED", "FLYING", "SUB_ORBITAL", "ORBITING", "ESCAPING", "DOCKED",
            "Sun", "Moho", "Eve", "Gilly", "Kerbin", "Mun", "Minmus", "Duna", "Ike", "Dres", "Jool", "Laythe",
            "Vall", "Tylo", "Bop", "Pol", "Eeloo",
        };

        public string Name { get; private set; }
        public string Block { get; private set; }
        public string Craft { get; private set; }

        /// <summary>Where the craft starts: in orbit (the default) or on Kerbin's launch pad (<c>start = launchpad</c>), where the orbit settings don't apply.</summary>
        public BlockTestStart Start { get; private set; }

        public string OrbitBody { get; private set; }
        public double OrbitAltitude { get; private set; }
        public double OrbitInclination { get; private set; }

        /// <summary>
        /// Periapsis altitude in m, for a test that starts on an eccentric orbit (a suborbital hop has one
        /// below the ground). Null: circular at <c>orbit_altitude</c>. Otherwise <c>orbit_altitude</c> is the
        /// apoapsis and the craft starts there.
        /// </summary>
        public double? OrbitPeriapsis { get; private set; }

        /// <summary>
        /// Degrees per second the craft is set spinning about its pitch axis when the test starts (0, the
        /// default, leaves it steady): a scenario that starts tumbling.
        /// </summary>
        public double TumbleRate { get; private set; }

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
                    case "start":
                        if (value == "orbit") spec.Start = BlockTestStart.Orbit;
                        else if (value == "launchpad") spec.Start = BlockTestStart.LaunchPad;
                        else throw new FormatException(name + ": start must be 'orbit' or 'launchpad', not '" + value + "'");
                        break;
                    case "orbit_body": spec.OrbitBody = value; break;
                    case "orbit_altitude": spec.OrbitAltitude = Number(name, value); break;
                    case "orbit_inclination": spec.OrbitInclination = Number(name, value); break;
                    case "orbit_pe": spec.OrbitPeriapsis = Number(name, value); break;
                    case "tumble_rate": spec.TumbleRate = Number(name, value); break;
                    case "timeout_s": spec.TimeoutSeconds = Number(name, value); break;
                    case "check":
                        Readable(name, value, Split(name, value));
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

            // warp.* reads the run's warp trace, not the Script Result.
            var warp = new Dictionary<string, object>
            {
                { WarpPrefix, outcome.Trace == null ? new Dictionary<string, object>() : outcome.Trace.Checks(Block) },
            };
            var passed = true;
            foreach (var check in Checks)
            {
                var parts = Split(Name, check);
                object actual;
                var found = TryField(IsWarp(parts[0]) ? warp : outcome.Report, parts[0], out actual);
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

        private static bool IsWarp(string field)
        {
            return field.StartsWith(WarpPrefix + ".", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Throws unless the check's expected value is one <see cref="Holds"/> reads as a literal, and a <c>warp.</c> field is
        /// one the trace has (<c>warp.&lt;field&gt;</c> or <c>warp.&lt;block&gt;.&lt;field&gt;</c>).
        /// </summary>
        private static void Readable(string name, string check, string[] parts)
        {
            if (IsWarp(parts[0]))
            {
                var path = parts[0].Split('.');
                if (path.Length > 3 || !WarpTrace.CheckFields.Contains(path[path.Length - 1], StringComparer.OrdinalIgnoreCase))
                    throw new FormatException(name + ": '" + check + "' checks the warp trace for '" + parts[0].Substring(WarpPrefix.Length + 1)
                        + "', which it doesn't have (it has warp." + string.Join(", warp.", WarpTrace.CheckFields.ToArray()) + ", and each under warp.<block>.)");
            }
            double number;
            if (double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return;
            if (parts[1] == "==" || parts[1] == "!=")
            {
                var expected = parts[2];
                if (expected.Length == 0 || string.Equals(expected, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(expected, "false", StringComparison.OrdinalIgnoreCase)
                    || KnownValues.Any(k => string.Equals(k, expected, StringComparison.OrdinalIgnoreCase))) return;
                throw new FormatException(name + ": '" + check + "' compares with '" + expected
                    + "', which is not a number, true, false, a situation or a body (a check can't name another field)");
            }
            throw new FormatException(name + ": '" + check + "' needs a number after " + parts[1]);
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

    public enum BlockTestStart
    {
        Orbit,
        LaunchPad,
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
