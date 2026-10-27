// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The Mission log (chat lines, plan cards and Debriefs) built again from a Mission's saved transcript, so a Mission
    /// loaded from a save, or read from the history, shows what it showed before. The player's words and Claudia's replies
    /// are all in the transcript; the mod's own notices (approvals, a script's start and end) are not, and come from the Mission folder's event log.
    /// </summary>
    internal static class MissionLogRebuild
    {
        // The phrases it parses live in MissionPrompts, with the text that writes them.
        private const string Replied = MissionPrompts.RepliedPrefix;
        private const string Changed = MissionPrompts.ChangedPrefix;
        private const string Asked = MissionPrompts.AskedPrefix;
        private const string CrossingsMarker = MissionPrompts.CrossingsMarker;
        private const string RunEndedMarker = "\n" + MissionPrompts.RunEndedPrefix;

        /// <param name="craftName">The Mission's craft, so the plan cards mark what flies a Child Vessel as the loop's did.</param>
        /// <param name="notices">The mod's own notices the Mission folder kept, each placed by the conversation's length when it was written.</param>
        public static List<LogEntry> From(StoredTranscript transcript, string goal, string craftName, PlayerConfig config, IList<SavedNotice> notices = null)
        {
            var log = new List<LogEntry>();
            if (!string.IsNullOrEmpty(goal)) log.Add(new LogEntry { Kind = LogKind.Player, Text = "Mission Goal: " + goal });
            if (transcript == null) return log;

            var previousUser = "";
            var next = 0;
            var count = 0;
            foreach (var message in transcript.Messages)
            {
                next = AddNotices(log, notices, next, count++);
                if (message.Role == Role.User)
                {
                    previousUser = Text(message);
                    var said = PlayerLine(previousUser);
                    if (said != null) log.Add(new LogEntry { Kind = LogKind.Player, Text = said });
                }
                else if (message.Role == Role.Assistant)
                {
                    var reply = MissionReply.Parse(Text(message));
                    if (reply != null) Add(log, reply, previousUser, craftName, config);
                }
            }
            AddNotices(log, notices, next, int.MaxValue);
            return log;
        }

        /// <summary>Puts the notices written while the conversation held at most <paramref name="messages"/> messages into the log; returns where the next unplaced one is.</summary>
        private static int AddNotices(List<LogEntry> log, IList<SavedNotice> notices, int next, int messages)
        {
            while (notices != null && next < notices.Count && notices[next].Messages <= messages)
                log.Add(new LogEntry { Kind = LogKind.Notice, Text = notices[next++].Text });
            return next;
        }

        private static string Text(Message message)
        {
            return string.Concat(message.Content.OfType<TextBlock>().Select(b => b.Text));
        }

        /// <summary>What the player typed, as the loop logged it, from the turn that carried it; null for a turn the mod made up.</summary>
        private static string PlayerLine(string userText)
        {
            string typed;
            if (Unquote(userText, Replied, out typed)) return typed;
            if (Unquote(userText, Changed, out typed)) return "Change: " + typed;
            if (Unquote(userText, Asked, out typed)) return typed;
            return null;
        }

        /// <summary>The JSON string that follows <paramref name="prefix"/> (the loop quotes the player's words as data), whatever comes after it.</summary>
        private static bool Unquote(string text, string prefix, out string value)
        {
            value = null;
            if (!text.StartsWith(prefix, System.StringComparison.Ordinal)) return false;
            var start = prefix.Length;
            if (start >= text.Length || text[start] != '"') return false;
            for (var i = start + 1; i < text.Length; i++)
            {
                if (text[i] == '\\') i++;
                else if (text[i] == '"')
                {
                    try
                    {
                        value = JsonNode.Str(JsonReader.Parse(text.Substring(start, i - start + 1)));
                    }
                    catch (JsonException)
                    {
                        return false;
                    }
                    return value != null;
                }
            }
            return false;
        }

        private static void Add(List<LogEntry> log, MissionReply reply, string userText, string craftName, PlayerConfig config)
        {
            var plan = reply.MissionPlan;
            if (reply.Kind == MissionReply.AlertKind && reply.Alert != null)
            {
                log.Add(new LogEntry { Kind = LogKind.Claudia, Text = "Alert: " + (reply.Alert.Abort ? "Abort recommended. " : "carry on. ") + reply.Say });
                return;
            }
            var declined = plan != null && plan.Sequences.Count == 0 && !string.IsNullOrWhiteSpace(plan.Why);
            log.Add(new LogEntry { Kind = LogKind.Claudia, Text = declined ? reply.Say + " " + plan.Why : reply.Say });

            if (reply.Kind == MissionReply.MissionPlanKind && plan != null && plan.Sequences.Count > 0)
                log.Add(new LogEntry { Kind = LogKind.MissionPlan, Title = "Mission Plan", Text = MissionCards.MissionPlanBody(plan, craftName), Why = plan.Why });
            else if (reply.Kind == MissionReply.SequencePlanKind && reply.SequencePlan != null && (reply.SequencePlan.Manual || reply.SequencePlan.Script.Trim().Length > 0))
            {
                var sequence = reply.SequencePlan;
                log.Add(new LogEntry
                {
                    Kind = LogKind.SequencePlan,
                    Title = sequence.Name,
                    Text = MissionCards.SequencePlanBody(sequence, craftName, TurnUt(userText), config),
                    Why = sequence.Why,
                    Script = sequence.Script,
                    CustomScript = sequence.CustomScript,
                    CustomScriptReason = sequence.CustomScriptReason,
                });
            }
            else if (reply.Kind == MissionReply.DebriefKind && reply.Debrief != null) AddDebrief(log, reply.Debrief, userText);
        }

        private static void AddDebrief(List<LogEntry> log, Debrief debrief, string userText)
        {
            // The turn that asked for it listed the Telemetry Checks crossed; any of them rules out Nominal, as the loop rated it.
            var crossings = Crossings(userText);
            if (crossings.Length > 0 && debrief.Outcome == "Nominal")
            {
                debrief.Outcome = "Off-Nominal";
                log.Add(new LogEntry { Kind = LogKind.Notice, Text = "Rated Off-Nominal, not Nominal: a Telemetry Check was crossed during the run." });
            }
            var outcome = debrief.Outcome;
            log.Add(new LogEntry
            {
                Kind = LogKind.Debrief,
                Title = "Debrief: " + outcome + StopReason(userText),
                Text = MissionCards.DebriefBody(debrief, crossings),
                Why = debrief.Why,
                Outcome = outcome,
                Debrief = MissionCards.DebriefDetail(debrief),
            });
            if (debrief.GoalReached) log.Add(new LogEntry { Kind = LogKind.Notice, Text = "Mission Goal met: the Mission is over." });
            else if (outcome == "Failed") log.Add(new LogEntry { Kind = LogKind.Notice, Text = "The Mission Goal can't be reached as stated: the Mission has ended." });
        }

        /// <summary>
        /// " (blocked: …)", " (timed out)", " (aborted by the player)", " (aborted by Auto Mode)" or "": the reason the mod stopped the run, which the
        /// turn asking for the Debrief carried in its "kOS run ended: Aborted (reason)." line. Any other reason on that line
        /// (the game was loaded, Manual Control) is not the mod stopping the run and never reached the live title.
        /// </summary>
        private static string StopReason(string userText)
        {
            var at = userText.IndexOf(RunEndedMarker, System.StringComparison.Ordinal);
            if (at < 0) return "";
            var start = at + RunEndedMarker.Length;
            var end = userText.IndexOf('\n', start);
            var line = end < 0 ? userText.Substring(start) : userText.Substring(start, end - start);
            var open = line.IndexOf(" (", System.StringComparison.Ordinal);
            if (open < 0 || !line.EndsWith(").", System.StringComparison.Ordinal)) return "";
            var reason = line.Substring(open + 2, line.Length - open - 4);
            var stop = reason == RunawayStop.Reason || reason == MissionControl.AbortedByThePlayer || reason == MissionControl.AbortedByAutoMode || reason.StartsWith("blocked: ", System.StringComparison.Ordinal);
            return stop ? " (" + reason + ")" : "";
        }

        /// <summary>The game time the turn asking for a plan gave Claudia ("UT: 123.4 s."), which the card's Due Time was "in" so long against; null without one.</summary>
        private static double? TurnUt(string userText)
        {
            const string marker = MissionPrompts.UtMarker;
            var at = userText.StartsWith(marker, System.StringComparison.Ordinal) ? 0 : userText.IndexOf("\n" + marker, System.StringComparison.Ordinal);
            if (at < 0) return null;
            var start = userText.IndexOf(marker, at, System.StringComparison.Ordinal) + marker.Length;
            var end = userText.IndexOf(MissionPrompts.UtUnit, start, System.StringComparison.Ordinal);
            double ut;
            if (end < 0 || !double.TryParse(userText.Substring(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out ut)) return null;
            return ut;
        }

        private static string Crossings(string userText)
        {
            var at = userText.IndexOf(CrossingsMarker, System.StringComparison.Ordinal);
            if (at < 0) return "";
            var start = at + CrossingsMarker.Length;
            var end = userText.IndexOf('\n', start);
            var line = end < 0 ? userText.Substring(start) : userText.Substring(start, end - start);
            return line.EndsWith(".") ? line.Substring(0, line.Length - 1) : line;
        }

        /// <summary>The Debrief that closes a finished Mission's log, or null.</summary>
        public static LogEntry FinalDebrief(IList<LogEntry> log)
        {
            return log.LastOrDefault(e => e.Kind == LogKind.Debrief);
        }
    }
}
