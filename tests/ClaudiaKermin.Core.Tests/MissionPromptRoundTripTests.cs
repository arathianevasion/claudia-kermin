// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The Mission log rebuild parses turns by the phrases <see cref="MissionPrompts"/> writes them with (#201): each
    /// prompt kind is built, put in a transcript and rebuilt, so rewording one can't silently drop the player's line,
    /// a Telemetry Check crossing or a Due Time span from a rebuilt log.
    /// </summary>
    public sealed class MissionPromptRoundTripTests
    {
        private const string Quote = "Go \"gently\" over the\nridge";

        [Fact]
        public void AReplyKeepsThePlayersWords()
        {
            Assert.Equal(new[] { Quote }, PlayerLines(MissionPrompts.PlayerReplied(Quote)));
        }

        [Fact]
        public void AChangeKeepsThePlayersWordsMarkedAsAChange()
        {
            Assert.Equal(new[] { "Change: " + Quote }, PlayerLines(MissionPrompts.PlayerWantsChange(Quote)));
        }

        [Fact]
        public void AQuestionKeepsThePlayersWords()
        {
            Assert.Equal(new[] { Quote }, PlayerLines(MissionPrompts.PlayerAsks(Quote)));
        }

        [Fact]
        public void AQuestionAskedWhileRunningKeepsThePlayersWordsToo()
        {
            Assert.Equal(new[] { Quote }, PlayerLines(MissionPrompts.PlayerAsks(Quote) + "\n" + MissionPrompts.Facts(10, null)));
        }

        [Fact]
        public void ADebriefRequestKeepsTheTelemetryChecksCrossed()
        {
            var turn = MissionPrompts.CrossedChecks("delta-v used 100 m/s over 50 m/s: Abort recommended (Overrun.)");
            var log = Rebuild(turn, DebriefReply("Nominal"));
            var debrief = log.Single(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Debrief: Off-Nominal", debrief.Title);
            Assert.Contains("Telemetry Checks crossed: delta-v used 100 m/s over 50 m/s: Abort recommended (Overrun.)", debrief.Text);
        }

        [Fact]
        public void APlanRequestKeepsTheDueTimeSpanAgainstItsUt()
        {
            var log = Rebuild(MissionPrompts.Facts(1000.5, null), SequenceReply(1000.5 + 3600));
            Assert.Contains("(in " + KerbinTime.Span(3600) + ")", log.Single(e => e.Kind == LogKind.SequencePlan).Text);
        }

        [Fact]
        public void APlanRequestWithTheUtOnALaterLineStillKeepsTheSpan()
        {
            var log = Rebuild("A line first.\n" + MissionPrompts.Facts(500, null), SequenceReply(500 + 7200));
            Assert.Contains("(in " + KerbinTime.Span(7200) + ")", log.Single(e => e.Kind == LogKind.SequencePlan).Text);
        }

        [Fact]
        public void ThePhrasesTheRebuildParsesAreUnchanged()
        {
            Assert.Equal("The player replied (quoted data): \"x\"", MissionPrompts.PlayerReplied("x"));
            Assert.Equal("The player wants a change (quoted data): \"x\"", MissionPrompts.PlayerWantsChange("x"));
            Assert.Equal("The player asks (quoted data): \"x\"", MissionPrompts.PlayerAsks("x"));
            Assert.Equal("UT: 12.5 s.", MissionPrompts.Facts(12.5, null));
            Assert.Equal("\nTelemetry Checks crossed during the run, with your mid-run verdicts: a.", MissionPrompts.CrossedChecks("a"));
        }

        // ---- Helpers ----

        private static string[] PlayerLines(string turn)
        {
            return Rebuild(turn, null).Where(e => e.Kind == LogKind.Player).Select(e => e.Text).ToArray();
        }

        private static List<LogEntry> Rebuild(string userTurn, string replyJson)
        {
            var transcript = new StoredTranscript();
            transcript.Messages.Add(Message.UserText(userTurn));
            if (replyJson != null)
            {
                var reply = new Message { Role = Role.Assistant };
                reply.Content.Add(new TextBlock { Text = replyJson });
                transcript.Messages.Add(reply);
            }
            return MissionLogRebuild.From(transcript, "", "Kerbal X", new PlayerConfig());
        }

        private static string DebriefReply(string outcome)
        {
            return "{\"kind\":\"debrief\",\"say\":\"Debrief.\",\"reply_intent\":\"none\",\"mission_plan\":null,\"sequence_plan\":null,\"alert\":null,"
                + "\"debrief\":{\"outcome\":\"" + outcome + "\",\"why\":\"It went.\",\"what_happened\":\"It went.\",\"goal_reached\":false,\"next\":\"Fly on.\",\"eva_science\":\"\"}}";
        }

        private static string SequenceReply(double dueUt)
        {
            return "{\"kind\":\"sequence_plan\",\"say\":\"Plan.\",\"reply_intent\":\"none\",\"mission_plan\":null,\"debrief\":null,\"alert\":null,"
                + "\"sequence_plan\":{\"name\":\"Burn\",\"intent\":\"Burn.\",\"why\":\"w\",\"target\":\"t\",\"tolerances\":\"t\",\"expected_delta_v\":1,"
                + "\"expected_duration_s\":2,\"abort_conditions\":[],\"risks\":[],\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"s\","
                + "\"due_time_ut\":" + dueUt.ToString(CultureInfo.InvariantCulture) + "}}";
        }
    }
}
