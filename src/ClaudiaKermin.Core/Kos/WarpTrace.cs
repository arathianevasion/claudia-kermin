// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>What a warp trace entry is.</summary>
    public enum WarpTraceKind
    {
        /// <summary>The program asked for warp (<c>WARPTO</c>, or a <c>WARP</c> or <c>RATE</c> above 1x).</summary>
        Request,
        /// <summary>The rate fell to 1x mid-wait, before the warp's target, or the player lowered it.</summary>
        Drop,
    }

    /// <summary>One line of a <see cref="WarpTrace"/>: a warp request or a drop.</summary>
    public sealed class WarpTraceEntry
    {
        public WarpTraceKind Kind { get; internal set; }

        /// <summary>The innermost Building Block running, or "" outside every block.</summary>
        public string Block { get; internal set; }

        /// <summary>Real seconds since the run started.</summary>
        public double RealS { get; internal set; }

        public double Ut { get; internal set; }

        /// <summary>The UT the warp was asked to reach: a <c>WARPTO</c>'s, else the wait's; null outside a wait.</summary>
        public double? TargetUt { get; internal set; }

        /// <summary>"rails" or "physics": KSP's warp mode when the request was made.</summary>
        public string Mode { get; internal set; }

        /// <summary>The rate asked for; null for a <c>WARPTO</c>, where KSP picks the rate.</summary>
        public double? RateAsked { get; internal set; }

        /// <summary>A request: the highest rate seen after it, until the next request or its wait ended. A drop: the rate it fell from.</summary>
        public double RateReached { get; internal set; }

        /// <summary>A drop: the rate it fell to, 1 but for the player lowering warp to a rate above 1x. 0 for a request.</summary>
        public double RateTo { get; internal set; }

        /// <summary>KSP's refusal message when it refused or lowered the rate (its <c>getMaxOnRailsRateIdx</c> limits), else "".</summary>
        public string Refusal { get; internal set; }

        /// <summary>
        /// A drop's cause: "cut" (KSP, or no one the mod could name), "held" (WARPHOLD), "player" (the player lowered or
        /// stopped warp, #336) and, as the mod learns to tell it apart, "thrust" (physics warp off a Coast). "" for a request.
        /// </summary>
        public string Drop { get; internal set; }

        /// <summary>A request's place among its wait's requests, from 1; 0 outside a wait.</summary>
        public int Attempt { get; internal set; }

        /// <summary>The wait had asked for warp before: a retry after a drop.</summary>
        public bool Retry { get { return Attempt > 1; } }
    }

    /// <summary>KSP's own message for a rails-warp limit, for the trace (research #300: <c>TimeWarp.getMaxOnRailsRateIdx</c>).</summary>
    public static class WarpRefusal
    {
        /// <param name="reason">The <c>ClearToSaveStatus</c> name KSP gave.</param>
        /// <param name="rate">The rate KSP allows.</param>
        /// <param name="limitM">For an altitude limit, the height in m the next rate needs.</param>
        public static string Message(string reason, double rate, double? limitM)
        {
            var at = rate.ToString("0.##", CultureInfo.InvariantCulture) + "x";
            switch (reason)
            {
                case "NOT_IN_ATMOSPHERE": return "Cannot warp faster than " + at + " while in atmosphere.";
                case "NOT_UNDER_ACCELERATION": return "Cannot warp faster than " + at + " while the ship is under acceleration.";
                case "NOT_WHILE_ON_A_LADDER": return "Cannot warp faster than " + at + " while Kerbals are on a ladder.";
                case "NOT_WHILE_MOVING_OVER_SURFACE": return "Cannot warp faster than " + at + " while moving over terrain.";
                case "NOT_WHILE_ABOUT_TO_CRASH":
                    return "Cannot warp faster than " + at + (limitM.HasValue ? " while below " + limitM.Value.ToString("0", CultureInfo.InvariantCulture) + "m." : " this low.");
                case "ORBIT_EVENT_IMMINENT": return "Orbit event imminent. Dropping TimeWarp to max limit: " + at;
                default: return "KSP limited warp to " + at + " (" + reason + ")";
            }
        }
    }

    /// <summary>One Building Block's run, as the warp trace sums it up.</summary>
    public sealed class WarpTraceBlock
    {
        public string Name { get; internal set; }

        /// <summary>Real seconds since the run started.</summary>
        public double StartRealS { get; internal set; }

        /// <summary>Real seconds from the block's start to its first warp request (its own or a block it called); null with none.</summary>
        public double? FirstRequestRealS { get; internal set; }

        /// <summary>Real seconds spent at 1x inside waits while the block ran.</summary>
        public double RealSAt1x { get; internal set; }

        /// <summary>The highest warp rate seen while the block ran (1 if it never warped).</summary>
        public double MaxRate { get; internal set; }

        public int Requests { get; internal set; }
        public int Retries { get; internal set; }
        public int Drops { get; internal set; }

        internal bool Open;
    }

    /// <summary>
    /// The warp trace of one mod-started run (spec #314, Warp trace; #335): every warp request the program made, every drop,
    /// and per Building Block the real time to its first request and the real time at 1x inside waits, so warp problems are
    /// measured instead of guessed and block tests can check them (<c>check = warp.first_request_real_s &lt; 10</c>). The
    /// adapter feeds it from KSP: the requests from the runtime gate patches, the blocks from <c>BLOCKSTART</c> and
    /// <c>BLOCKRESULT</c>, the waits from the Core Library's <c>WAITSTART</c> and <c>WAITEND</c>, and the rate every frame.
    /// Times are passed in, so it runs without KSP. Real time is wall-clock seconds, not game time.
    /// </summary>
    public sealed class WarpTrace
    {
        /// <summary>A rate at or under this is 1x (KSP lerps between rates, so a float is never quite 1).</summary>
        private const double At1x = 1.01;

        /// <summary>Falling to 1x within this many game seconds of the warp's target is the warp ending, not a drop.</summary>
        private const double EndMargin = 10;

        /// <summary>The fields a block test may check, as <c>warp.&lt;field&gt;</c> or <c>warp.&lt;block&gt;.&lt;field&gt;</c>.</summary>
        public static readonly IList<string> CheckFields = new[] { "first_request_real_s", "real_s_at_1x", "max_rate", "retries", "requests", "drops" };

        private readonly double _startReal;
        private readonly List<WarpTraceEntry> _entries = new List<WarpTraceEntry>();
        private readonly List<WarpTraceBlock> _blocks = new List<WarpTraceBlock>();
        private bool _waiting;
        private double _waitStartReal;
        private double _waitTarget;
        private int _waitAttempts;
        private WarpTraceEntry _lastRequest;
        private double? _lastSampleReal;
        private double _lastRate = 1;
        /// <summary>The rate a fall to 1x came from, between the <see cref="Sample"/> that saw it and the <see cref="Dropped"/> naming it.</summary>
        private double? _fellFrom;
        /// <summary>The player stopped warp (<see cref="PlayerDropped"/>), so the fall to 1x that follows is already named.</summary>
        private bool _playerFall;

        /// <param name="startReal">The real time (any clock in seconds) the run started at.</param>
        public WarpTrace(double startReal)
        {
            _startReal = startReal;
        }

        public IList<WarpTraceEntry> Entries { get { return _entries; } }

        public IList<WarpTraceBlock> Blocks { get { return _blocks; } }

        public void BlockStarted(string block, double real)
        {
            _blocks.Add(new WarpTraceBlock { Name = block ?? "", StartRealS = real - _startReal, MaxRate = 1, Open = true });
        }

        /// <summary>Closes the innermost open block of that name (blocks nest: <c>circularize</c> runs <c>exec_node</c>).</summary>
        public void BlockEnded(string block, double real)
        {
            var open = _blocks.LastOrDefault(b => b.Open && b.Name == (block ?? ""));
            if (open != null) open.Open = false;
        }

        /// <summary>A Core Library wait began; warpEnd is the UT its warp must be over by.</summary>
        public void WaitStarted(double real, double ut, double warpEnd)
        {
            _waiting = true;
            _waitStartReal = real;
            _waitTarget = warpEnd;
            _waitAttempts = 0;
            _lastRequest = null;
        }

        public void WaitEnded(double real, double ut)
        {
            _waiting = false;
            _lastRequest = null;
        }

        /// <summary>
        /// The program asked for warp. targetUt: a <c>WARPTO</c>'s target, or null to take the wait's; rateAsked null for a
        /// <c>WARPTO</c>; refusal: KSP's message if its limits refuse or lower the rate now, else "".
        /// </summary>
        public WarpTraceEntry Requested(double real, double ut, double? targetUt, string mode, double? rateAsked, string refusal)
        {
            var entry = new WarpTraceEntry
            {
                Kind = WarpTraceKind.Request,
                Block = Innermost(),
                RealS = real - _startReal,
                Ut = ut,
                TargetUt = targetUt ?? (_waiting ? _waitTarget : (double?)null),
                Mode = mode ?? "",
                RateAsked = rateAsked,
                RateReached = 1,
                Refusal = refusal ?? "",
                Drop = "",
                Attempt = _waiting ? ++_waitAttempts : 0,
            };
            _entries.Add(entry);
            _lastRequest = entry;
            _playerFall = false;
            foreach (var block in OpenBlocks())
            {
                if (!block.FirstRequestRealS.HasValue) block.FirstRequestRealS = entry.RealS - block.StartRealS;
                block.Requests++;
                if (entry.Retry) block.Retries++;
            }
            return entry;
        }

        /// <summary>
        /// The warp rate this frame. Returns true when the rate just fell to 1x mid-wait, short of the warp's target, with no
        /// <see cref="Dropped"/> naming it first: the caller then names it with <see cref="Dropped"/>.
        /// </summary>
        public bool Sample(double real, double ut, double rate)
        {
            if (_waiting && _lastSampleReal.HasValue && _lastRate <= At1x)
            {
                var at1x = real - Math.Max(_lastSampleReal.Value, _waitStartReal);
                if (at1x > 0)
                    foreach (var block in OpenBlocks()) block.RealSAt1x += at1x;
            }
            foreach (var block in OpenBlocks()) block.MaxRate = Math.Max(block.MaxRate, rate);
            if (_lastRequest != null) _lastRequest.RateReached = Math.Max(_lastRequest.RateReached, rate);

            var fell = _waiting && _lastRate > At1x && rate <= At1x && ut < Target() - EndMargin;
            // KSP lerps a stop down over a few frames, so the player's fall comes after PlayerDropped named it. A rise on
            // the way means warp was asked for again, and a later fall is someone else's.
            if (_playerFall && (rate <= At1x || rate > _lastRate))
            {
                if (rate <= At1x) fell = false;
                _playerFall = false;
            }
            _fellFrom = fell ? _lastRate : (double?)null;
            _lastSampleReal = real;
            _lastRate = rate;
            return fell;
        }

        /// <summary>A frame with the game paused, instead of a <see cref="Sample"/>: the real time since the last sample isn't time at 1x.</summary>
        public void Paused(double real)
        {
            _lastSampleReal = real;
        }

        /// <summary>
        /// Warp dropped to 1x, for why ("cut", "held", "player", "thrust"), with KSP's refusal message if its limits explain it.
        /// Called after a <see cref="Sample"/> that returned true, or by the mod when it drops warp itself or sees who did, before
        /// that frame's sample (which then doesn't report the fall again).
        /// </summary>
        public void Dropped(double real, double ut, string why, string refusal)
        {
            var from = _fellFrom ?? _lastRate;
            _fellFrom = null;
            _lastRate = 1;
            _entries.Add(new WarpTraceEntry
            {
                Kind = WarpTraceKind.Drop,
                Block = Innermost(),
                RealS = real - _startReal,
                Ut = ut,
                TargetUt = _waiting ? Target() : (double?)null,
                Mode = _lastRequest == null ? "" : _lastRequest.Mode,
                RateReached = from,
                RateTo = 1,
                Refusal = refusal ?? "",
                Drop = why ?? "",
            });
            foreach (var block in OpenBlocks()) block.Drops++;
            // The next request is a fresh one, not this one reaching a rate.
            _lastRequest = null;
        }

        /// <summary>
        /// The player lowered or stopped warp (#336), from one rate to another, at the moment they did. Traced only inside
        /// a wait, as a <c>player</c> drop; a stop to 1x isn't reported again by the <see cref="Sample"/> that sees the fall.
        /// </summary>
        public void PlayerDropped(double real, double ut, double from, double to)
        {
            if (!_waiting) return;
            _entries.Add(new WarpTraceEntry
            {
                Kind = WarpTraceKind.Drop,
                Block = Innermost(),
                RealS = real - _startReal,
                Ut = ut,
                TargetUt = Target(),
                Mode = _lastRequest == null ? "" : _lastRequest.Mode,
                RateReached = from,
                RateTo = to,
                Refusal = "",
                Drop = "player",
            });
            foreach (var block in OpenBlocks()) block.Drops++;
            if (to > At1x) return;
            _playerFall = true;
            _lastRequest = null;
        }

        /// <summary>
        /// What a block test checks as <c>warp.*</c>: the fields of the first run of <paramref name="block"/> (the test's block),
        /// and under each block's name the fields of its first run. A block that never asked for warp has no
        /// <c>first_request_real_s</c>, so a check on it fails.
        /// </summary>
        public IDictionary<string, object> Checks(string block)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var each in _blocks.GroupBy(b => b.Name).Select(g => g.First()))
                if (!CheckFields.Contains(each.Name, StringComparer.OrdinalIgnoreCase)) result[each.Name] = Fields(each);
            var main = _blocks.FirstOrDefault(b => b.Name == (block ?? ""));
            if (main != null)
                foreach (var field in Fields(main)) result[field.Key] = field.Value;
            return result;
        }

        /// <summary>The trace for KSP.log: one line per entry, then one per block.</summary>
        public IList<string> Lines()
        {
            var lines = new List<string>();
            foreach (var e in _entries)
            {
                var line = e.Kind == WarpTraceKind.Request
                    ? "request real " + Seconds(e.RealS) + ", UT " + Whole(e.Ut) + ", target " + (e.TargetUt.HasValue ? Whole(e.TargetUt.Value) : "none")
                      + ", " + e.Mode + ", asked " + (e.RateAsked.HasValue ? Rate(e.RateAsked.Value) : "KSP's pick") + ", reached " + Rate(e.RateReached)
                      + ", attempt " + e.Attempt
                    : "drop (" + e.Drop + ") real " + Seconds(e.RealS) + ", UT " + Whole(e.Ut) + ", from " + Rate(e.RateReached)
                      + (e.RateTo > At1x ? ", to " + Rate(e.RateTo) : "");
                if (e.Refusal.Length > 0) line += ", KSP: " + e.Refusal;
                if (e.Block.Length > 0) line += ", in " + e.Block;
                lines.Add(line);
            }
            foreach (var b in _blocks)
                lines.Add("block " + b.Name + ": first request " + (b.FirstRequestRealS.HasValue ? Seconds(b.FirstRequestRealS.Value) : "none") + ", "
                    + Seconds(b.RealSAt1x) + " at 1x in waits, top rate " + Rate(b.MaxRate) + ", " + b.Requests + " requests, " + b.Retries
                    + " retries, " + b.Drops + " drops");
            return lines;
        }

        private static Dictionary<string, object> Fields(WarpTraceBlock block)
        {
            var fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "real_s_at_1x", Math.Round(block.RealSAt1x, 2) },
                { "max_rate", block.MaxRate },
                { "retries", (double)block.Retries },
                { "requests", (double)block.Requests },
                { "drops", (double)block.Drops },
            };
            if (block.FirstRequestRealS.HasValue) fields["first_request_real_s"] = Math.Round(block.FirstRequestRealS.Value, 2);
            return fields;
        }

        private double Target()
        {
            return _lastRequest != null && _lastRequest.TargetUt.HasValue ? _lastRequest.TargetUt.Value : _waitTarget;
        }

        private IEnumerable<WarpTraceBlock> OpenBlocks()
        {
            return _blocks.Where(b => b.Open);
        }

        private string Innermost()
        {
            var open = _blocks.LastOrDefault(b => b.Open);
            return open == null ? "" : open.Name;
        }

        private static string Seconds(double s)
        {
            return s.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        private static string Whole(double value)
        {
            return value.ToString("0", CultureInfo.InvariantCulture);
        }

        private static string Rate(double rate)
        {
            return rate.ToString("0.##", CultureInfo.InvariantCulture) + "x";
        }
    }
}
