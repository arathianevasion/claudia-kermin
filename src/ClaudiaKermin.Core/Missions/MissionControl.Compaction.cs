// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Tools;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Compaction on purpose (#268, spec "Compaction on purpose"). The mod tracks the conversation's input size from each
    /// committed reply's usage. Past the threshold it compacts, as a request of its own (beta <c>compact-2026-09-04</c>, no reply
    /// schema), at the next start of Running whose Sequence Plan expects at least about two minutes, so the summary is written
    /// while Claudia is idle; past the hard ceiling it compacts at the next turn boundary whatever the sequence. It never runs
    /// mid-loop: it starts only with no turn in hand, and a turn begun while it runs waits for it, except an Alert, which goes
    /// at once on the conversation as it stood.
    ///
    /// The conversation itself stays whole (save markers and the rebuilt Mission log count its messages): the returned block
    /// is kept byte for byte beside it with how many messages it stands in for, and every later request sends the block first,
    /// then the messages after those (<see cref="Sent"/>). A failed compaction is tried once more, then skipped with a log line;
    /// with no room under the Spending Cap it waits for the next start of Running. The player sees only a Mission log line.
    /// </summary>
    public sealed partial class MissionControl
    {
        /// <summary>One compaction request in flight.</summary>
        private sealed class CompactionRun
        {
            /// <summary>How many of the conversation's messages the request summarizes: all of them when it was sent.</summary>
            public int Covers;
            public int Attempt;
            /// <summary>"threshold" (a long sequence started) or "ceiling" (the next turn boundary).</summary>
            public string Why;
            public CancellationTokenSource Cancel;
        }

        private sealed partial class Mission
        {
            /// <summary>The on-demand compaction block in force, byte for byte as the API sent it; null before the first compaction.</summary>
            public CompactionBlock CompactionBlock;
            /// <summary>How many of <see cref="Conversation"/>'s messages, from the start, the block stands in for.</summary>
            public int CompactedMessages;
            /// <summary>The conversation's input size, in tokens, from the last committed reply's usage; 0 when not known.</summary>
            public long ContextTokens;
            /// <summary>The compaction request in flight; null when none.</summary>
            public CompactionRun Compacting;
            /// <summary>The worst case of the compaction in flight. It counts against the Spending Cap beside a turn sent meanwhile.</summary>
            public decimal CompactionCharge;
            /// <summary>The conversation's length when a compaction was last skipped after its retry; -1 when none was.</summary>
            public int CompactionSkippedAt = -1;
        }

        /// <summary>What a request sends of the conversation: the compaction block first and the messages after those it stands in for, or every message.</summary>
        private static IEnumerable<Message> Sent(Mission mission)
        {
            if (mission.CompactionBlock == null) return mission.Conversation;
            var block = new Message { Role = Role.Assistant };
            block.Content.Add(mission.CompactionBlock);
            return new[] { block }.Concat(mission.Conversation.Skip(mission.CompactedMessages));
        }

        /// <summary>The effort the conversation as sent last set: per-message effort before the block is gone with what it summarized.</summary>
        private static string SentEffort(Mission mission)
        {
            string effort = null;
            foreach (var m in mission.Conversation.Skip(mission.CompactionBlock != null ? mission.CompactedMessages : 0))
                if (m.Effort != null) effort = m.Effort;
            return effort;
        }

        /// <summary>
        /// A reply was committed: its usage is the conversation's size (input, cache reads and writes, and its own output, which
        /// joins the conversation). A reply sent behind an older block than the one now in force measured a conversation that is
        /// gone, so it says nothing.
        /// </summary>
        private static void TrackContext(Mission mission, Turn turn, AssistantMessage message)
        {
            if (turn.SentAfter != mission.CompactionBlock) return;
            var usage = message.Usage;
            mission.ContextTokens = usage.InputTokens + usage.CacheReadInputTokens + usage.CacheCreationInputTokens + usage.OutputTokens;
        }

        private int CompactionCeiling
        {
            get { return Math.Max(_config.CompactionCeilingTokens, _config.CompactionTriggerTokens); }
        }

        /// <summary>A run has started: past the threshold, a sequence expected to take long enough is when to compact.</summary>
        private void CompactAtRunStart(Mission mission, SequencePlan plan)
        {
            if (mission.ContextTokens < _config.CompactionTriggerTokens) return;
            if (plan.ExpectedDurationSeconds < _config.CompactionMinRunSeconds)
            {
                Event(mission, "compaction_waits", "the sequence expects " + plan.ExpectedDurationSeconds.ToString("0", CultureInfo.InvariantCulture)
                    + " s, too short to compact in; the next long one will");
                return;
            }
            StartCompaction(mission, "threshold");
        }

        /// <summary>
        /// A turn is about to begin with nothing in hand: true when it must wait for a compaction, which is running or (past the
        /// hard ceiling) starts now. An Alert never waits: it goes on the conversation as it stands.
        /// </summary>
        private bool WaitsForCompaction(Mission mission, Turn turn)
        {
            if (turn.Purpose == Purpose.Alert) return false;
            // One try per boundary: a compaction skipped here waits for the next committed turn.
            if (mission.Compacting == null && mission.ContextTokens >= CompactionCeiling && mission.Conversation.Count != mission.CompactionSkippedAt)
                StartCompaction(mission, "ceiling");
            return mission.Compacting != null;
        }

        private MessagesRequest CompactionRequest(Mission mission)
        {
            // The conversation's own model, system and tools, so the cached prefix holds and the summarizer sees what Claudia saw.
            var request = new MessagesRequest
            {
                Model = mission.Model,
                MaxTokens = MissionPrompts.CompactionMaxTokens,
                Compaction = true,
                CompactionInstructions = MissionPrompts.CompactionInstructions,
            };
            request.Tools.AddRange(_tools.Definitions(ToolOffer.Mission));
            request.System.AddRange(mission.System);
            request.Messages.AddRange(Sent(mission));
            return request;
        }

        /// <summary>Sends a compaction of every committed message; false (and a log line) when there is nothing to compact or no room under the Spending Cap.</summary>
        private bool StartCompaction(Mission mission, string why, int attempt = 1)
        {
            if (mission.Conversation.Count <= (mission.CompactionBlock != null ? mission.CompactedMessages : 0) || !_hasKey()) return false;
            var request = CompactionRequest(mission);
            var worst = RequestCharge.WorstCase(_config.Prices, mission.Model, request);
            if (mission.Committed + worst > mission.SpendingCap)
            {
                LogCost(mission, "blocked", worst, "over_cap");
                Event(mission, "compaction_deferred", "it could pass the Spending Cap; the next start of Running tries again");
                return false;
            }
            var run = new CompactionRun { Covers = mission.Conversation.Count, Attempt = attempt, Why = why, Cancel = new CancellationTokenSource() };
            mission.Compacting = run;
            mission.CompactionCharge = worst;
            mission.Requests++;
            LogCost(mission, "pending", worst, "compaction");
            Event(mission, "compaction", why + ": " + mission.ContextTokens + " tokens, " + run.Covers + " messages" + (attempt > 1 ? ", second try" : ""));
            var cancel = run.Cancel.Token;
            var dump = mission.Dump;
            _background.Run(
                () => _client.Stream(request, update => { }, cancel, dump),
                result => Compacted(mission, run, result),
                error => Compacted(mission, run, null));
            return true;
        }

        /// <summary>Cancels a compaction in flight, as a dropped Mission's request is; its worst case is kept.</summary>
        private static void CancelCompaction(Mission mission)
        {
            if (mission != null && mission.Compacting != null) mission.Compacting.Cancel.Cancel();
        }

        private void Compacted(Mission mission, CompactionRun run, StreamResult result)
        {
            if (mission.Compacting != run) return;
            mission.Compacting = null;
            run.Cancel.Dispose();
            var settlement = RequestCharge.Settle(_config.Prices, mission.CompactionCharge, result, mission.Model);
            mission.CompactionCharge = 0m;
            mission.Spent += settlement.Spent;
            mission.Held += settlement.Held;
            var block = Block(result);
            LogCost(mission, settlement.Kind, settlement.Amount, settlement.Outcome ?? (block != null ? "compaction" : "compaction_failed"));
            if (mission != _mission || mission.Stage == MissionStage.Lost) return;
            try
            {
                if (block != null) Adopt(mission, run, block);
                else if (result == null || result.Status != StreamStatus.Cancelled) CompactionFailed(mission, run, result);
                AfterCharge(mission);
                if (mission == _mission && mission.Compacting == null) SendQueued(mission);
            }
            finally
            {
                Rebuild();
            }
        }

        /// <summary>The summary a compaction response carries: one compaction block with content, after the <c>compaction</c> stop reason; null otherwise.</summary>
        private static CompactionBlock Block(StreamResult result)
        {
            if (result == null || result.Status != StreamStatus.Completed || result.Message.StopReason != StopReason.Compaction) return null;
            var block = result.Message.Content.OfType<CompactionBlock>().FirstOrDefault();
            return block != null && !string.IsNullOrEmpty(block.Content) ? block : null;
        }

        /// <summary>
        /// The block stands in for the messages it summarized from now on. What was committed while it ran (an Alert) stays after
        /// it. Per-message effort went with the summarized messages, so the next turn sends its effort again.
        /// </summary>
        private void Adopt(Mission mission, CompactionRun run, CompactionBlock block)
        {
            var before = mission.ContextTokens;
            mission.CompactionBlock = block;
            mission.CompactedMessages = run.Covers;
            mission.Effort = SentEffort(mission);
            mission.ContextTokens = 0;
            KeepTurnsAfterTheBlock(mission);
            WriteTranscript(mission);
            Event(mission, "compacted", run.Why + ": " + run.Covers + " messages, " + before + " tokens, into a summary of " + block.Content.Length + " characters");
            Note(LogKind.Notice, "Mission Control summarized the conversation so far (" + run.Covers + " messages), so Claudia's requests stay small.");
        }

        /// <summary>
        /// A turn committed while the compaction ran follows the block. If it opens with a system message (an effort change),
        /// the API would merge it into the summarized messages and its reply's thinking could no longer be checked, so that
        /// thinking is dropped from the kept turns.
        /// </summary>
        private static void KeepTurnsAfterTheBlock(Mission mission)
        {
            if (mission.Conversation.Count == mission.CompactedMessages || mission.Conversation[mission.CompactedMessages].Role != Role.System) return;
            foreach (var message in mission.Conversation.Skip(mission.CompactedMessages).Where(m => m.Role == Role.Assistant))
                message.Content.RemoveAll(b => b is ThinkingBlock);
        }

        /// <summary>A compaction that brought back no summary is tried once more; after that it is skipped, and the next start of Running (or turn boundary past the ceiling) tries again.</summary>
        private void CompactionFailed(Mission mission, CompactionRun run, StreamResult result)
        {
            var why = result == null ? "the request failed"
                : result.Status != StreamStatus.Completed ? Describe(result)
                : "no summary came back (" + (result.Message.RawStopReason ?? "no stop reason") + ")";
            Event(mission, "compaction_failed", why + (run.Attempt == 1 ? "; trying once more" : "; skipped"));
            if (run.Attempt == 1 && StartCompaction(mission, run.Why, 2)) return;
            mission.CompactionSkippedAt = mission.Conversation.Count;
            Note(LogKind.Notice, "The conversation couldn't be summarized (" + why + "). The Mission carries on, and tries again later.");
        }
    }
}
