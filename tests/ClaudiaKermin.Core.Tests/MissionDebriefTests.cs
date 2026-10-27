// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The full Debrief (#56) and its margin recheck (#101): the run's event log, the extended Debrief reply, the card
    /// the log drawer shows, the margin bar, and what follows a Failed or Off-Nominal Debrief. Driven through the Mission
    /// seam with hand-written streams.
    /// </summary>
    public sealed class MissionDebriefTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const double ApprovedAt = 1203.48;
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- The event log ----

        [Fact]
        public void TheEventLogKeepsEventsInOrderAndCountsTheSameEventTwiceInARowOnce()
        {
            var log = new RunEventLog();
            log.Add(110, RunEventKind.Staging, "stage 2 fired");
            log.Add(115, RunEventKind.Overheating, "Mk1 Command Pod at 93% of its limit");
            log.Add(116, RunEventKind.Overheating, "Mk1 Command Pod at 93% of its limit");
            log.Add(120, RunEventKind.Science, "Mystery Goo on Kerbin's surface: 3.0 science");
            log.Add(121, RunEventKind.PartLost, "  ");

            Assert.Equal(3, log.Count);
            Assert.Equal(new[] { "+10 s staging: stage 2 fired", "+15 s overheating: Mk1 Command Pod at 93% of its limit" }, log.EventLines(100));
            Assert.Equal(new[] { "+20 s Mystery Goo on Kerbin's surface: 3.0 science" }, log.ScienceLines(100));
        }

        [Fact]
        public void TheEventLogIsBoundedAndSaysHowManyEventsItLeftOut()
        {
            var log = new RunEventLog();
            for (var i = 0; i < RunEventLog.Max + 5; i++) log.Add(i, RunEventKind.PartLost, "part " + i);

            Assert.Equal(RunEventLog.Max, log.Count);
            Assert.Equal(5, log.Dropped);
            Assert.Equal("(5 more events not listed)", log.EventLines(0).Last());
            log.Clear();
            Assert.Equal(0, log.Count);
            Assert.Equal("", log.DebriefText(0));
        }

        // ---- The margin bar ----

        [Fact]
        public void TheMarginBarSaysEnoughTightOrShortAgainstTheRestOfThePlan()
        {
            Assert.Equal(MarginLevel.Enough, MarginBar.Compute(1200, 1000, 10).Level);
            Assert.Equal(MarginLevel.Tight, MarginBar.Compute(1050, 1000, 10).Level);
            Assert.Equal(MarginLevel.Short, MarginBar.Compute(900, 1000, 10).Level);

            var tight = MarginBar.Compute(1050, 1000, 10);
            Assert.Equal(1100, tight.Needed, 6);
            Assert.Equal(1050.0 / 1100, tight.Fraction, 6);
            Assert.Equal(5, tight.ActualPercent.Value, 6);
            Assert.Equal("1,050 m/s left against 1,000 m/s still to fly (+5%; 10% wanted): tight.", tight.Describe());
            Assert.Equal(1, MarginBar.Compute(5000, 1000, 10).Fraction);
        }

        [Fact]
        public void ThereIsNoMarginBarWhenNothingIsLeftToFlyOrTheSimulatorGaveNoFigure()
        {
            Assert.Null(MarginBar.Compute(1200, 0, 10));
            Assert.Null(MarginBar.Compute(null, 1000, 10));
        }

        // ---- The reply ----

        [Fact]
        public void TheSchemaRequiresEveryDebriefFieldTheMissionAsksFor()
        {
            // A Debrief request sends the flight schema (#199).
            using (var request = JsonDocument.Parse(MissionReply.FlightSchemaJson))
            {
                var schema = request.RootElement;
                var debrief = schema.GetProperty("properties").GetProperty("debrief").GetProperty("anyOf")[0];
                var required = debrief.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
                var properties = debrief.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
                foreach (var name in new[] { "outcome_vs_target", "plan_margin", "science", "goal_checklist", "follow_up", "follow_up_text" })
                {
                    Assert.Contains(name, required);
                    Assert.Contains(name, properties);
                }
                Assert.Equal(required.OrderBy(n => n), properties.OrderBy(n => n));
            }
        }

        // ---- What the Debrief request carries ----

        [Fact]
        public void TheDebriefRequestCarriesTheRunsEventsTheRestOfThePlanAndTheMarginRecheck()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.Router.Recheck = Reply("{\"summary\":\"3,200 m/s left against 1,000 m/s to fly: plenty.\",\"findings\":[]}");
            var start = rig.RunToRunning();

            rig.Mission.Send(new RunEventSeen(RunEventKind.Staging, "stage 1 fired", ApprovedAt + 12));
            rig.Mission.Send(new RunEventSeen(RunEventKind.Science, "Mystery Goo on the pad: 3.0 science", ApprovedAt + 20));
            rig.Mission.Send(new KosBlockEnded("launch", new Dictionary<string, object> { { "success", false }, { "reason", "abort min_ec_pct" } }, start.RunId));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            var request = LastUserText(rig.Transport.Requests[2]);
            Assert.Contains("+12 s staging: stage 1 fired", request);
            Assert.Contains("+20 s Mystery Goo on the pad: 3.0 science", request);
            Assert.Contains("block aborted: Building Block launch: failed (abort min_ec_pct)", request);
            Assert.Contains("Maneuver Sequences still to fly in the Mission Plan (quoted)", request);
            Assert.Contains("Second leg", request);
            Assert.Contains("The mod's margin recheck", request);
            Assert.Contains("3,200 m/s left against 1,000 m/s to fly: plenty.", request);
        }

        // ---- The whole run's delta-v (#384) ----

        [Fact]
        public void TheDebriefRequestCarriesTheDeltaVTheWholeRunUsedFromStock()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(s => s.Stages[0].DeltaVVac = 3913.3);

            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 2875.3));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains("Delta-v used this run, from stock: 1,038 m/s (left at start 3,913 m/s, now 2,875 m/s).", LastUserText(rig.Transport.Requests[2]));
        }

        [Fact]
        public void ADeltaVUsedBelowZeroIsShownAsZero()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(s => s.Stages[0].DeltaVVac = 2000);

            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 2100));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains("Delta-v used this run, from stock: 0 m/s (left at start 2,000 m/s, now 2,100 m/s).", LastUserText(rig.Transport.Requests[2]));
        }

        [Theory]
        [InlineData(true, false, true, "the stock delta-v simulator is off now")]
        [InlineData(false, true, true, "a stage's delta-v reading is unreliable now")]
        [InlineData(false, false, false, "no stock delta-v figure at the run's start")]
        public void WhenStocksDeltaVCantBeReadTheDebriefSaysWhyInsteadOfANumber(bool simulatorOffAtEnd, bool unreliableAtEnd, bool startReadable, string why)
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning(startReadable ? (Action<GameSnapshot>)(s => s.Stages[0].DeltaVVac = 3913.3) : (s => s.Stages[0].DeltaVUnreliable = true));

            rig.Mission.Send(Craft(s =>
            {
                s.DeltaVSimulator = !simulatorOffAtEnd;
                s.Stages[0].DeltaVUnreliable = unreliableAtEnd;
            }));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            var request = LastUserText(rig.Transport.Requests[2]);
            Assert.Contains("Delta-v used this run: unknown (" + why + ").", request);
            Assert.DoesNotContain("Delta-v used this run, from stock", request);
        }

        [Fact]
        public void AHandBackDebriefLeavesTheRunsDeltaVOut()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.RunToRunning(s => s.Stages[0].DeltaVVac = 3913.3);
            rig.Mission.Send(new TakeManualControl());
            rig.Mission.Send(Craft(s => s.Stages[0].DeltaVVac = 2875.3));

            rig.Mission.Send(new HandBack("Done by hand."));
            rig.DrainUntil(v => v.Stage != MissionStage.Debriefing);

            var request = LastUserText(rig.Transport.Requests[2]);
            Assert.Contains("took Manual Control", request);
            Assert.DoesNotContain("Delta-v used this run", request);
        }

        // ---- RCS Use (#398) ----

        private static Dictionary<string, object> Switch(bool on, string reason)
        {
            return new Dictionary<string, object> { { "on", on }, { "reason", reason } };
        }

        [Fact]
        public void TheRcsTallyCountsSwitchesOnAndSecondsOnPerBlockAndNamesTheBlockWithMost()
        {
            var tally = new RcsUseTally();
            tally.Switched(100, "circularize", true);
            tally.Switched(110, "circularize", false);
            tally.Switched(200, "land", true);
            tally.Switched(205, "land", true);   // already on: not counted again
            tally.Switched(210, "land", false);
            tally.Switched(210, "land", false);  // already off
            tally.Switched(300, "circularize", true);
            tally.Switched(302, "circularize", false);

            Assert.Equal(3, tally.TimesOn);
            Assert.Equal(22, tally.SecondsOn, 6);
            Assert.Equal("circularize", tally.MostBlock);
            Assert.Equal(2, tally.MostBlockTimes);
        }

        [Fact]
        public void TheRcsTallyCountsASwitchStillOnAtRunEndUpToThatMoment()
        {
            var tally = new RcsUseTally();
            tally.Switched(100, "land", true);
            tally.Close(130);
            tally.Close(500);

            Assert.Equal(1, tally.TimesOn);
            Assert.Equal(30, tally.SecondsOn, 6);
            tally.Clear();
            Assert.Equal(0, tally.TimesOn);
            Assert.Equal(0, tally.SecondsOn, 6);
        }

        [Fact]
        public void TheRcsUseLineSaysHowOftenHowLongAndWhereMostOrThatThereWasNone()
        {
            var tally = new RcsUseTally();
            Assert.Equal("RCS Use this run: none.", MissionPrompts.RcsUseLine(tally));
            for (var i = 0; i < 14; i++)
            {
                tally.Switched(i * 10, i < 11 ? "Circularize at 80 km" : "land", true);
                tally.Switched(i * 10 + (i < 12 ? 7 : 6), "x", false);
            }
            Assert.Equal("RCS Use this run: switched on 14 times, 96 s in all; most in \"Circularize at 80 km\" (11).", MissionPrompts.RcsUseLine(tally));

            var once = new RcsUseTally();
            once.Switched(0, "", true);
            once.Close(4);
            Assert.Equal("RCS Use this run: switched on 1 time, 4 s in all.", MissionPrompts.RcsUseLine(once));
        }

        [Fact]
        public void AnRcsSwitchLineCarriesTheReasonAndTheFiguresItWasGiven()
        {
            var values = Switch(true, "turning");
            values["error_deg"] = 34.2;
            values["rate_dps"] = 0.1;
            values["wheels_s"] = 85.0;
            values["left_s"] = 60;
            values["monoprop"] = 30.0;
            values["monoprop_max"] = 40.0;
            string problem;
            Assert.Equal("RCS on: turning (error 34.2°, rate 0.1°/s, wheels 85 s vs 60 s left, monoprop 30/40)", RcsSwitch.Parse(values, out problem).Line());

            var off = Switch(false, "aligned");
            off["error_deg"] = 1.4;
            off["rate_dps"] = 0.2;
            Assert.Equal("RCS off: aligned (error 1.4°, rate 0.2°/s)", RcsSwitch.Parse(off, out problem).Line());
            Assert.Equal("RCS off: block end", RcsSwitch.Parse(Switch(false, "block end"), out problem).Line());
        }

        [Fact]
        public void ABadRcsSwitchLexiconFailsWithAProblemAndNoException()
        {
            string problem;
            Assert.Null(RcsSwitch.Parse(null, out problem));
            Assert.Null(RcsSwitch.Parse(new Dictionary<string, object>(), out problem));
            Assert.Contains("'on'", problem);
            Assert.Null(RcsSwitch.Parse(Switch(true, "bored"), out problem));
            Assert.Contains("'reason'", problem);
            var text = Switch(true, "turning");
            text["error_deg"] = "big";
            Assert.Null(RcsSwitch.Parse(text, out problem));
            Assert.Contains("'error_deg'", problem);
        }

        [Fact]
        public void TheDebriefRequestCarriesTheRcsUseLineAndTheMissionLogHasEachSwitch()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosBlockStarted("circularize", start.RunId));
            rig.Mission.Send(new KosRcsSwitched(Switch(true, "turning"), ApprovedAt + 10, start.RunId));
            rig.Mission.Send(new KosRcsSwitched(Switch(false, "aligned"), ApprovedAt + 15, start.RunId));
            rig.Mission.Send(new KosRcsSwitched(Switch(true, "bored"), ApprovedAt + 16, start.RunId));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains("RCS Use this run: switched on 1 time, 5 s in all; most in \"circularize\" (1).", LastUserText(rig.Transport.Requests[2]));
            var log = rig.Mission.View.Log.Select(e => e.Text).ToList();
            Assert.Contains("RCS on: turning", log);
            Assert.Contains("RCS off: aligned", log);
            Assert.Contains(log, t => t != null && t.StartsWith("RCS switch not logged: 'reason'"));
        }

        [Fact]
        public void ARunWithNoRcsSwitchesSaysNoneAndAHandBackDebriefLeavesTheLineOut()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.Succeeded || v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Contains("RCS Use this run: none.", LastUserText(rig.Transport.Requests[2]));

            var hand = new Rig(this, DebriefJson("Nominal", ""));
            hand.RunToRunning();
            hand.Mission.Send(new TakeManualControl());
            hand.Mission.Send(new HandBack("Done by hand."));
            hand.DrainUntil(v => v.Stage != MissionStage.Debriefing);
            Assert.DoesNotContain("RCS Use this run", LastUserText(hand.Transport.Requests[2]));
        }

        [Fact]
        public void EveryDebriefRunsTheMarginRecheckThroughTheCraftCheckAndChargesItToTheMission()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            // The router's default Pass carries no usage; this one is billed, so the charge can be seen.
            rig.Router.Recheck = Reply("{\"summary\":\"Plenty left.\",\"findings\":[]}");
            var start = rig.RunToRunning();
            var checksBefore = rig.Router.CheckRequests.Count;
            var spentBefore = rig.Mission.View.Spent;

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            // One more Craft Check request, asked of the craft as it is now against the plan that is left.
            Assert.Equal(checksBefore + 1, rig.Router.CheckRequests.Count);
            var body = rig.Router.CheckRequests.Last().Body;
            Assert.Contains("Margin recheck after a Maneuver Sequence", body);
            Assert.Contains("Second leg", body);

            // The Mission started from a cleared Craft Check, so its cost opens the log as Craft Check entries and stays what it was.
            var all = rig.CostLog();
            Assert.Equal(new[] { "craft_check_pending", "craft_check_paid" }, all.Take(2).Select(c => c.Event));
            Assert.DoesNotContain(all.Skip(2), c => c.Event.StartsWith("craft_check"));

            // The recheck is charged like any Mission request: a pending worst case, then its real usage, before the Debrief's own.
            var cost = all.Skip(2).ToList();
            // The Mission Plan, the Sequence Plan, the recheck, then the Debrief itself (the next Sequence Plan follows it).
            Assert.Equal(new[] { "pending", "paid", "pending", "paid", "pending", "paid", "pending", "paid" }, cost.Take(8).Select(c => c.Event));
            Assert.Equal("margin_recheck", cost[4].Outcome);
            Assert.Equal("completed", cost[5].Outcome);
            Assert.True(cost[5].Amount > 0);
            Assert.True(rig.Mission.View.Spent >= spentBefore + cost[5].Amount);
            Assert.Equal(rig.Mission.View.Spent, all.Where(c => c.Event == "paid" || c.Event == "craft_check_paid").Sum(c => c.Amount));
        }

        [Fact]
        public void TheMarginRecheckIsSkippedWhenTheSpendingCapHasNoRoomForItBesideTheDebrief()
        {
            // Room for the plan, the sequence and the Debrief, but not for a recheck on top of them.
            var rig = new Rig(this, DebriefJson("Nominal", ""), cap: 0.45m);
            var start = rig.RunToRunning();
            var checksBefore = rig.Router.CheckRequests.Count;

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(checksBefore, rig.Router.CheckRequests.Count);
            Assert.Contains(rig.Mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("The margin recheck was skipped: it could pass the Spending Cap"));
            Assert.DoesNotContain("The mod's margin recheck", LastUserText(rig.Transport.Requests[2]));
            Assert.Contains(rig.CostLog(), c => c.Event == "blocked" && c.Outcome == "over_cap");
            Assert.Contains("Spending Cap", rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.RecheckSkipped);
            // The Mission is not Paused for a request it did not need.
            Assert.NotEqual(MissionStage.Paused, rig.Mission.View.Stage);
        }

        [Fact]
        public void AMarginRecheckThatFailsIsPaidForReportedAndTheDebriefGoesOnWithoutIt()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.Router.Recheck = Reply("this is not a craft check reply");
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains(rig.Mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("The margin recheck failed"));
            Assert.Contains("did not come back", LastUserText(rig.Transport.Requests[2]));
            Assert.Contains("the recheck failed", rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.RecheckSkipped);
            // After the held Craft Check entries: Mission Plan, Sequence Plan, then the recheck's pair.
            Assert.Equal("off_schema", rig.CostLog().Skip(2).ElementAt(5).Outcome);
        }

        [Fact]
        public void ARefusedMarginRecheckIsPaidForAndPausesTheDebriefForRetryPerRefusals()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.Router.Recheck = ScriptedResponse.Fixture("refusal.sse");
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Paused);

            Assert.Equal(new[] { QuickReply.Retry, QuickReply.RetryWithNote, QuickReply.EndMission }, rig.Mission.View.QuickReplies);
            Assert.Equal("refused", rig.CostLog().Skip(2).ElementAt(5).Outcome);
            // The Debrief itself has not been sent.
            Assert.Equal(2, rig.Transport.Requests.Count);
        }

        [Fact]
        public void AMarginRecheckThatFindsTheModelGonePausesForChooseModel()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.Router.Recheck = ScriptedResponse.Error(404, "not_found_error", "model: claude-sonnet-5-5");
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Paused);

            Assert.Equal(new[] { QuickReply.ChooseModel, QuickReply.EndMission }, rig.Mission.View.QuickReplies);
            Assert.Equal(2, rig.Transport.Requests.Count);
        }

        [Fact]
        public void AMarginRecheckFindingReachesTheCardAndTheRequest()
        {
            var rig = new Rig(this, DebriefJson("Off-Nominal", ""));
            rig.Router.Recheck = Reply("{\"summary\":\"Short by 300 m/s.\",\"findings\":[{\"code\":\"delta_v_short\",\"explanation\":\"900 m/s left against 1,200 needed.\",\"fix\":\"Add a tank.\"}]}");
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            var detail = rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief;
            Assert.Equal("Short by 300 m/s.", detail.RecheckSummary);
            Assert.Single(detail.RecheckFindings);
            Assert.Contains("900 m/s left against 1,200 needed.", detail.RecheckFindings[0]);
            Assert.Contains("Short by 300 m/s.", LastUserText(rig.Transport.Requests[2]));
        }

        [Fact]
        public void EventsFromBeforeTheRunAreNotInItsDebrief()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.Mission.Send(new RunEventSeen(RunEventKind.Staging, "a stage before the run", ApprovedAt));
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.DoesNotContain("a stage before the run", LastUserText(rig.Transport.Requests[2]));
            Assert.Empty(rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.Events);
        }

        [Fact]
        public void TheDebriefCardReportsTheRcsSwitchAndTheMonopropellantLeft()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();
            var snapshot = OnPad();
            snapshot.RcsOn = true;
            snapshot.Resources.Add(new SnapshotResource { Name = "MonoPropellant", Amount = 15, Max = 60 });
            rig.Mission.Send(snapshot);
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            var detail = rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief;
            Assert.Equal("RCS on", detail.RcsState);
            Assert.Contains("MonoPropellant 25%", detail.ResourcesLeft);
        }

        [Fact]
        public void TheDebriefCardSaysNothingOfRcsWhenTheSnapshotDidNotReadIt()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal("", rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.RcsState);
        }

        [Fact]
        public void TheRcsSwitchSurvivesTheSnapshotJsonAndIsOmittedWhenUnread()
        {
            var on = new GameSnapshot { RcsOn = true };
            Assert.Contains("\"rcs_on\":true", on.ToJson());
            Assert.True(GameSnapshot.Parse(on.ToJson()).RcsOn);
            Assert.False(GameSnapshot.Parse(new GameSnapshot { RcsOn = false }.ToJson()).RcsOn);
            Assert.DoesNotContain("rcs_on", new GameSnapshot().ToJson());
            Assert.Null(GameSnapshot.Parse(new GameSnapshot().ToJson()).RcsOn);
        }

        // ---- The card ----

        [Fact]
        public void TheDebriefCardShowsOutcomeMarginScienceEventsChecklistAndTheFollowUp()
        {
            var rig = new Rig(this, DebriefJson("Off-Nominal", FullExtras));
            var start = rig.RunToRunning();
            rig.Mission.Send(new RunEventSeen(RunEventKind.PartLost, "Spark Engine broke off", ApprovedAt + 30));
            rig.Mission.Send(new RunEventSeen(RunEventKind.Science, "Mystery Goo on the pad: 3.0 science", ApprovedAt + 31));
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            var card = rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            var detail = card.Debrief;
            Assert.Equal("Off-Nominal", detail.Rating);
            Assert.Equal("Off-Nominal", card.Outcome);
            Assert.Equal("Apoapsis 74 km against 80 km planned.", detail.OutcomeVsTarget);
            Assert.Equal("3,200 m/s left against 1,000 m/s to fly.", detail.PlanMargin);
            // The bar is the mod's own sum: the simulator's 3,200 m/s against the 1,000 m/s Second leg still to fly.
            Assert.Equal(3200, detail.Margin.DeltaVLeft);
            Assert.Equal(1000, detail.Margin.RestOfPlan);
            Assert.Equal(MarginLevel.Enough, detail.Margin.Level);
            Assert.Equal("Two goo samples.", detail.Science);
            Assert.Equal(new[] { "+30 s part lost: Spark Engine broke off" }, detail.Events);
            Assert.Equal(new[] { "+31 s Mystery Goo on the pad: 3.0 science" }, detail.ScienceSeen);
            Assert.Equal(1, detail.ChecklistDone);
            Assert.Equal(2, detail.Checklist.Count);
            Assert.Equal("Corrective sequence next: Raise the apoapsis by 6 km.", detail.FollowUp);
            Assert.Equal("", detail.ScriptError);
        }

        [Fact]
        public void AFailedRunsDebriefCardIncludesTheScriptsErrorText()
        {
            var rig = new Rig(this, DebriefJson("Failed", ""));
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.NoReport, null, "Undefined Variable Name: APOAPSIS2.\nat line 4", "", false), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingReducedGoal && !v.Busy);

            var detail = rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief;
            Assert.Equal("Undefined Variable Name: APOAPSIS2.\nat line 4", detail.ScriptError);
            Assert.Contains("Undefined Variable Name: APOAPSIS2.", LastUserText(rig.Transport.Requests[2]));
        }

        [Fact]
        public void ARunThatReportedHasNoScriptErrorOnItsCard()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Sequencing || v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal("", rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.ScriptError);
        }

        // ---- Off-Nominal ----

        [Fact]
        public void AnOffNominalDebriefWithARevisedPlanAsksForTheMissionPlanNotTheNextSequence()
        {
            var rig = new Rig(this, DebriefJson("Off-Nominal", ",\"follow_up\":\"revised_plan\",\"follow_up_text\":\"Add a second burn.\""), PlanReply());
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => rig.Transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Contains("Revise the Mission Plan after the Off-Nominal Debrief", LastUserText(rig.Transport.Requests[3]));
            Assert.Contains("Revised Mission Plan next: Add a second burn.", rig.Mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.FollowUp);
        }

        [Fact]
        public void AnOffNominalDebriefWithNoRevisedPlanAsksForACorrectiveSequence()
        {
            var rig = new Rig(this, DebriefJson("Off-Nominal", ""), Rig.SequenceReply());
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => rig.Transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Contains("Continue the Mission with a corrective sequence.", LastUserText(rig.Transport.Requests[3]));
        }

        // ---- Failed ----

        [Fact]
        public void AFailedDebriefWithAReducedGoalWaitsForApprovalAndEndMissionStaysOnOffer()
        {
            var rig = new Rig(this, DebriefJson("Failed", ",\"follow_up\":\"reduced_goal\",\"follow_up_text\":\"Reach a stable orbit of Kerbin.\""));
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingReducedGoal);

            var view = rig.Mission.View;
            Assert.Equal(new[] { QuickReply.AcceptReducedGoal, QuickReply.EndMission }, view.QuickReplies);
            Assert.False(view.CanType);
            Assert.True(view.CanEndMission);
            Assert.Equal(Goal, view.Goal);
            Assert.Contains("Reduced goal, for your Approval: Reach a stable orbit of Kerbin.", view.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.FollowUp);
            // It survives a save and a load.
            var saved = rig.Mission.ExportStates().Single();
            Assert.Equal("Reach a stable orbit of Kerbin.", MissionSaveState.FromValues(saved.ToValues()).ReducedGoal);

            rig.Mission.Send(new EndMission());
            Assert.Equal(MissionStage.None, rig.Mission.View.Stage);
        }

        [Fact]
        public void ApprovingTheReducedGoalChangesTheGoalAndAsksClaudiaToReviseTheMissionPlanForApproval()
        {
            var rig = new Rig(this, DebriefJson("Failed", ",\"follow_up\":\"reduced_goal\",\"follow_up_text\":\"Reach a stable orbit of Kerbin.\""), PlanReply());
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingReducedGoal);

            rig.Mission.Send(new AcceptReducedGoal());
            Assert.Equal("Reach a stable orbit of Kerbin.", rig.Mission.View.Goal);
            rig.DrainUntil(v => rig.Transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingPlanApproval);

            var request = LastUserText(rig.Transport.Requests[3]);
            Assert.Contains("approved a reduced Mission Goal", request);
            Assert.Contains("Reach a stable orbit of Kerbin.", request);
            Assert.Contains(Goal, request);
            // Nothing runs before the player approves the revised plan.
            Assert.Contains(QuickReply.Approve, rig.Mission.View.QuickReplies);
            Assert.Empty(rig.Mission.TakeCommands().OfType<StartScript>());
        }

        [Fact]
        public void AFailedDebriefWithNoReducedGoalWaitsWithEndMissionAndRetryWithANote()
        {
            var rig = new Rig(this, DebriefJson("Failed", ",\"follow_up\":\"end_mission\",\"follow_up_text\":\"\""));
            var start = rig.RunToRunning();

            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingReducedGoal);

            var view = rig.Mission.View;
            Assert.Equal(new[] { QuickReply.RetryWithNote, QuickReply.EndMission }, view.QuickReplies);
            Assert.True(view.CanEndMission);
            Assert.Contains("End Mission, or Retry with a note", view.Log.Last(e => e.Kind == LogKind.Debrief).Debrief.FollowUp);

            rig.Mission.Send(new EndMission());
            Assert.Equal(MissionStage.None, rig.Mission.View.Stage);
        }

        [Fact]
        public void EndingAFailedMissionWithNoReducedGoalRecordsItInTheMissionsListAsFailed()
        {
            var rig = new Rig(this, DebriefJson("Failed", ",\"follow_up\":\"end_mission\",\"follow_up_text\":\"\""));
            var start = rig.RunToRunning();
            var id = rig.Mission.View.MissionId;
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingReducedGoal);

            rig.Mission.Send(new EndMission());

            Assert.Equal(MissionStage.None, rig.Mission.View.Stage);
            var entry = Assert.Single(rig.Mission.View.Missions, m => m.Id == id);
            Assert.Equal(MissionStatus.Ended, entry.Status);
            Assert.Equal("Failed", entry.FinalDebrief.Outcome);
            Assert.True(entry.CanDelete);
            Assert.False(entry.CanResume);
            Assert.True(File.Exists(Path.Combine(_save.Path, "ClaudiaKermin", "missions", id, MissionStore.FinishedFile)));
        }

        [Fact]
        public void RetryWithANoteAfterAFailedDebriefWithNoReducedGoalAsksClaudiaForOne()
        {
            var rig = new Rig(this, DebriefJson("Failed", ",\"follow_up\":\"end_mission\",\"follow_up_text\":\"\""),
                Reply(DebriefJson("Failed", ",\"follow_up\":\"reduced_goal\",\"follow_up_text\":\"Reach a stable orbit of Kerbin.\"")));
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingReducedGoal);

            rig.Mission.Send(new RetryWithNote("Any orbit will do."));
            rig.DrainUntil(v => rig.Transport.Requests.Count == 4 && v.Stage == MissionStage.AwaitingReducedGoal && !v.Busy);

            Assert.Contains("Any orbit will do.", LastUserText(rig.Transport.Requests[3]));
            Assert.Equal(new[] { QuickReply.AcceptReducedGoal, QuickReply.EndMission }, rig.Mission.View.QuickReplies);
        }

        [Fact]
        public void ApproveReducedGoalDoesNothingOutsideItsStage()
        {
            var rig = new Rig(this, DebriefJson("Nominal", ""));
            rig.RunToRunning();
            var goal = rig.Mission.View.Goal;

            rig.Mission.Send(new AcceptReducedGoal());

            Assert.Equal(goal, rig.Mission.View.Goal);
            Assert.Equal(MissionStage.Running, rig.Mission.View.Stage);
        }

        // ---- Helpers ----

        private const string FullExtras =
            ",\"outcome_vs_target\":\"Apoapsis 74 km against 80 km planned.\",\"plan_margin\":\"3,200 m/s left against 1,000 m/s to fly.\","
            + "\"science\":\"Two goo samples.\",\"goal_checklist\":[{\"item\":\"Reach orbit\",\"done\":true},{\"item\":\"Land on the Mun\",\"done\":false}],"
            + "\"follow_up\":\"corrective_sequence\",\"follow_up_text\":\"Raise the apoapsis by 6 km.\"";

        private static string DebriefJson(string outcome, string extra)
        {
            return "{\"kind\":\"debrief\",\"say\":\"Debrief.\",\"reply_intent\":\"none\",\"mission_plan\":null,\"sequence_plan\":null,\"alert\":null,"
                + "\"debrief\":{\"outcome\":\"" + outcome + "\",\"why\":\"It went.\",\"what_happened\":\"It went.\",\"goal_reached\":false,\"next\":\"Fly on.\",\"eva_science\":\"\"" + extra + "}}";
        }

        private static ScriptedResponse PlanReply()
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Plan.\",\"reply_intent\":\"none\",\"sequence_plan\":null,\"debrief\":null,\"alert\":null,"
                + "\"mission_plan\":{\"why\":\"Two legs.\",\"sequences\":["
                + "{\"name\":\"Link check\",\"intent\":\"Prove the link.\",\"delta_v\":0,\"timing\":\"now\",\"due_time_ut\":-1,\"manual\":false},"
                + "{\"name\":\"Second leg\",\"intent\":\"Go on.\",\"delta_v\":1000,\"timing\":\"after\",\"due_time_ut\":-1,\"manual\":false}],"
                + "\"total_delta_v\":1000,\"expected_duration\":\"short\",\"mission_time_s\":-1}}");
        }

        /// <summary>A reply as the stream carries it: the JSON as one text block, priced by its usage.</summary>
        private static ScriptedResponse Reply(string json)
        {
            var text = JsonSerializer.Serialize(json);
            var sse = new StringBuilder();
            sse.Append("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_t\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],"
                + "\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3900,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n");
            sse.Append("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
            sse.Append("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + text + "}}\n\n");
            sse.Append("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
            sse.Append("event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},"
                + "\"usage\":{\"output_tokens\":90,\"iterations\":[{\"type\":\"message\",\"input_tokens\":3900,\"output_tokens\":90}]}}\n\n");
            sse.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse.ToString()) };
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private static GameSnapshot Craft(Action<GameSnapshot> shape)
        {
            var snapshot = OnPad();
            shape(snapshot);
            return snapshot;
        }

        private static string LastUserText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var user = doc.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
                var content = user.GetProperty("content");
                if (content.ValueKind == JsonValueKind.String) return content.GetString();
                return string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
            }
        }

        private sealed class CostLine
        {
            public string Event;
            public string Outcome;
            public decimal Amount;
        }

        /// <summary>A Mission with a two-sequence plan, one Sequence Plan, the Debrief under test and any replies after it.</summary>
        private sealed class Rig
        {
            private readonly MissionDebriefTests _tests;
            private readonly decimal _cap;
            public readonly StreamingFakeTransport Transport;
            public readonly CraftCheckRouter Router;
            public readonly MissionControl Mission;

            public Rig(MissionDebriefTests tests, string debrief, ScriptedResponse after = null, decimal cap = 10m)
            {
                _tests = tests;
                _cap = cap;
                Transport = new StreamingFakeTransport(PlanReply(), SequenceReply(), Reply(debrief));
                // What follows a Debrief that doesn't end the Mission: the next Sequence Plan unless the test says otherwise.
                Transport.Add(after ?? SequenceReply());
                Router = new CraftCheckRouter(Transport);
                var client = new ClaudeClient(Router, () => "sk-ant-test", l => { });
                client.Timing.Wait = (d, c) => { };
                var config = new PlayerConfig { CheckDurationPercent = 1000 };
                Mission = new MissionControl(client, tests._background, config, () => true, new PassingScriptCheck());
                Mission.Send(new SaveLoaded(tests._save.Path));
                Mission.Send(new SceneChanged(GameScene.Flight));
                Mission.Send(OnPad());
            }

            public static ScriptedResponse SequenceReply()
            {
                return ScriptedResponse.Fixture("mission-sequence-plan.sse");
            }

            /// <param name="craft">Shapes the Game Snapshot the Mission starts from, so the run's start figure is what the test wants.</param>
            public StartScript RunToRunning(Action<GameSnapshot> craft = null)
            {
                var snapshot = Craft(craft ?? (s => { }));
                Mission.Send(snapshot);
                CraftCheckRouter.StartChecked(Mission, _tests._background, snapshot, new StartMission(Goal, _cap, Sonnet));
                DrainUntil(v => v.Stage == MissionStage.AwaitingPlanApproval);
                Mission.Send(new Approve());
                DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval);
                Mission.Send(new Approve());
                Assert.Equal(MissionStage.Running, Mission.View.Stage);
                return Mission.TakeCommands().OfType<StartScript>().Single();
            }

            public void DrainUntil(Func<MissionView, bool> condition)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!condition(Mission.View) && DateTime.UtcNow < deadline)
                {
                    _tests._background.Drain();
                    Thread.Sleep(2);
                }
                _tests._background.Drain();
                Assert.True(condition(Mission.View), "never reached; stage is " + Mission.View.Stage + " " + Mission.View.PauseReason);
            }

            public IList<CostLine> CostLog()
            {
                var path = Path.Combine(_tests._save.Path, "ClaudiaKermin", "missions", Mission.View.MissionId, "cost-log.jsonl");
                return File.ReadAllText(path).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line =>
                {
                    var root = JsonDocument.Parse(line).RootElement;
                    JsonElement outcome, amount;
                    return new CostLine
                    {
                        Event = root.GetProperty("event").GetString(),
                        Outcome = root.TryGetProperty("outcome", out outcome) ? outcome.GetString() : "",
                        Amount = root.TryGetProperty("amount", out amount) ? amount.GetDecimal() : 0m,
                    };
                }).ToList();
            }
        }
    }
}
