// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class KosRunTests
    {
        private static readonly string[] Blank = { "", "" };

        private static Dictionary<string, object> Report(string key, object value)
        {
            return new Dictionary<string, object> { { key, value } };
        }

        [Fact]
        public void AReportBeforeTheProgramEndsMeansFinished()
        {
            var run = new KosRun(Blank);
            run.Report(Report("ok", true));
            run.ProgramEnded();

            var outcome = run.Finish(Blank);

            Assert.Equal(RunOutcomeKind.Finished, outcome.Kind);
            Assert.Equal(true, outcome.Report["ok"]);
        }

        [Fact]
        public void NoReportWithTheErrorTextKosPrintedNulsStripped()
        {
            var before = new[] { "my own output\0\0\0", "\0\0\0\0" };
            var run = new KosRun(before);
            run.ProgramEnded();

            var outcome = run.Finish(new[]
            {
                "my own output\0\0\0",
                "Undefined Variable Name 'nope'.\0\0\0\0",
                "At claudia-test on line 2:\0\0",
                "\0\0\0\0",
            });

            Assert.Equal(RunOutcomeKind.NoReport, outcome.Kind);
            Assert.Null(outcome.Report);
            Assert.Equal("Undefined Variable Name 'nope'.\nAt claudia-test on line 2:", outcome.ErrorText);
        }

        // ---- A block's trace (#365) ----

        private static Dictionary<string, object> Descent(string phase, double h)
        {
            return new Dictionary<string, object> { { "phase", phase }, { "h", h }, { "v_h", 201.5 } };
        }

        [Fact]
        public void ARunLostMidBlockReportsTheBlocksLastTracedState()
        {
            var run = new KosRun(Blank);
            run.BlockTraced("land", 428270, Descent("braking", 900));
            run.BlockTraced("land", 428272.4, Descent("braking", 450));
            run.Interrupted("the kOS processor was lost");
            run.ProgramEnded();

            var outcome = run.Finish(Blank);

            // Still a run that failed, but the Debrief gets where the block was.
            Assert.Equal(RunOutcomeKind.Interrupted, outcome.Kind);
            Assert.Equal(false, outcome.Report["success"]);
            Assert.Equal("land", outcome.Report["block"]);
            Assert.Contains("no Script Result", (string)outcome.Report["reason"]);
            var last = (IDictionary<string, object>)outcome.Report["last_state"];
            Assert.Equal("braking", last["phase"]);
            Assert.Equal(450.0, last["h"]);
            Assert.Equal(428272.4, last["ut"]);
        }

        [Fact]
        public void ABlockThatReturnedLeavesNoStateForANoReport()
        {
            var run = new KosRun(Blank);
            run.BlockTraced("land", 100, Descent("touchdown", 0));
            run.BlockEnded("land");
            run.ProgramEnded();

            var outcome = run.Finish(Blank);

            Assert.Equal(RunOutcomeKind.NoReport, outcome.Kind);
            Assert.Null(outcome.Report);
        }

        [Fact]
        public void AnInnerBlockEndingKeepsTheOuterBlocksState()
        {
            var run = new KosRun(Blank);
            run.BlockTraced("land", 100, Descent("fall", 3000));
            run.BlockEnded("exec_node");
            run.ProgramEnded();

            Assert.Equal("land", run.Finish(Blank).Report["block"]);
        }

        [Fact]
        public void TheScriptsOwnReportWinsOverATrace()
        {
            var run = new KosRun(Blank);
            run.BlockTraced("land", 100, Descent("braking", 900));
            run.Report(Report("ok", true));
            run.ProgramEnded();

            var outcome = run.Finish(Blank);

            Assert.Equal(RunOutcomeKind.Finished, outcome.Kind);
            Assert.False(outcome.Report.ContainsKey("last_state"));
        }

        [Fact]
        public void ATraceLineForTheLogNamesTheBlockTheUtAndEachValueInOrder()
        {
            var state = new Dictionary<string, object> { { "phase", "braking" }, { "h", 450.0 }, { "v_h", 201.5 }, { "flat", true } };

            Assert.Equal("land trace at UT 428272.4: phase braking, h 450, v_h 201.5, flat true", BlockTrace.Line("land", 428272.43, state));
        }

        [Fact]
        public void AnAbortTheModRecordedIsAbortedWithItsReason()
        {
            var run = new KosRun(Blank);
            run.Aborted("player pressed Abort");
            run.ProgramEnded();

            var outcome = run.Finish(new[] { "Program aborted." });

            Assert.Equal(RunOutcomeKind.Aborted, outcome.Kind);
            Assert.Equal("player pressed Abort", outcome.Reason);
        }

        [Fact]
        public void AnAbortAfterAReportStillFinishesButKeepsTheAbortOnRecord()
        {
            var run = new KosRun(Blank);
            run.Report(Report("ok", true));
            run.Aborted("player pressed Abort");
            run.ProgramEnded();

            var outcome = run.Finish(Blank);

            Assert.Equal(RunOutcomeKind.Finished, outcome.Kind);
            Assert.True(outcome.WasAborted);
            Assert.Equal("player pressed Abort", outcome.Reason);
        }

        [Fact]
        public void KosBreakMessageAfterTheStartMeansThePlayerStoppedIt()
        {
            var run = new KosRun(new[] { "counting", "" });
            run.ProgramEnded();

            var outcome = run.Finish(new[] { "counting", "1", "2", "Program aborted.\0\0" });

            Assert.Equal(RunOutcomeKind.StoppedByPlayer, outcome.Kind);
        }

        [Fact]
        public void ABreakMessageFromBeforeTheStartDoesNotCount()
        {
            var run = new KosRun(new[] { "Program aborted.", "" });
            run.ProgramEnded();

            var outcome = run.Finish(new[] { "Program aborted.", "Undefined Variable Name 'x'." });

            Assert.Equal(RunOutcomeKind.NoReport, outcome.Kind);
            Assert.Equal("Undefined Variable Name 'x'.", outcome.ErrorText);
        }

        [Fact]
        public void AReportAfterTheProgramEndedDoesNotCount()
        {
            var run = new KosRun(Blank);
            run.ProgramEnded();
            run.Report(Report("ok", true));

            Assert.True(run.HasEnded);
            Assert.Equal(RunOutcomeKind.NoReport, run.Finish(Blank).Kind);
        }

        [Fact]
        public void LeavingFlightWithoutAReportMeansInterrupted()
        {
            var run = new KosRun(Blank);
            run.Interrupted("left Flight");
            run.ProgramEnded();

            var outcome = run.Finish(new string[0]);

            Assert.Equal(RunOutcomeKind.Interrupted, outcome.Kind);
            Assert.Equal("left Flight", outcome.Reason);
        }

        [Fact]
        public void ErrorTextFollowsTheOldLinesAfterTheTerminalScrolls()
        {
            var run = new KosRun(new[] { "a", "b", "c", "", "" });
            run.ProgramEnded();

            var outcome = run.Finish(new[] { "b", "c", "x", "y", "" });

            Assert.Equal("x\ny", outcome.ErrorText);
        }

        [Fact]
        public void AfterAClearScreenEveryLineIsNew()
        {
            var run = new KosRun(new[] { "a", "b" });
            run.ProgramEnded();

            var outcome = run.Finish(new[] { "x", "", "y" });

            Assert.Equal("x\ny", outcome.ErrorText);
        }

        [Fact]
        public void ErrorTextKeepsOnlyTheTerminalTail()
        {
            var after = new List<string>();
            for (var i = 1; i <= 40; i++) after.Add("line " + i);
            var run = new KosRun(Blank);
            run.ProgramEnded();

            var lines = run.Finish(after).ErrorText.Split('\n');

            Assert.Equal(15, lines.Length);
            Assert.Equal("line 26", lines[0]);
            Assert.Equal("line 40", lines[14]);
        }
    }
}
