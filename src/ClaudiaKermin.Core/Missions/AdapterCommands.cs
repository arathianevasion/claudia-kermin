// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>Something <see cref="MissionControl"/> asks the adapter to do in the game, on the main thread.</summary>
    public abstract class AdapterCommand
    {
    }

    /// <summary>
    /// Compile <see cref="Source"/> in memory and start it on the kOS processor in part
    /// <see cref="ProcessorPartId"/> (a <c>persistentId</c>). Only ever issued after an Approval button press
    /// (ADR 0002). The adapter answers with <see cref="KosRunRefused"/> or, when the run ends, <see cref="KosRunEnded"/>.
    /// </summary>
    public sealed class StartScript : AdapterCommand
    {
        public readonly uint ProcessorPartId;
        public readonly string Label;
        public readonly string Source;
        /// <summary>Issued by the core, unique across all Missions in the session; the adapter returns it on the run's events.</summary>
        public readonly int RunId;

        public StartScript(uint processorPartId, string label, string source, int runId)
        {
            RunId = runId;
            ProcessorPartId = processorPartId;
            Label = label;
            Source = source;
        }
    }

    /// <summary>
    /// Abort the script running on the kOS processor in part <see cref="ProcessorPartId"/>, with make-safe
    /// (the existing abort path). Issued when the player ends a Mission that is Running, and by the runaway hard stop.
    /// </summary>
    public sealed class AbortScript : AdapterCommand
    {
        public readonly uint ProcessorPartId;
        public readonly string Reason;
        /// <summary>When set, only that run is aborted: a later run on the same processor is left alone.</summary>
        public readonly int? RunId;

        public AbortScript(uint processorPartId, string reason)
        {
            ProcessorPartId = processorPartId;
            Reason = reason;
        }

        public AbortScript(uint processorPartId, string reason, int runId) : this(processorPartId, reason)
        {
            RunId = runId;
        }
    }

    /// <summary>
    /// Seat Claudia in the editor's crew manifest, in a free seat, for a crewed Mission that has just started
    /// with her not aboard. The rest of the manifest is left as the player set it: the other Kerbals ride as
    /// passengers.
    /// </summary>
    public sealed class SeatClaudia : AdapterCommand
    {
    }

    /// <summary>
    /// Make the Mission's craft safe with no script to abort: throttle 0, SAS on, and the kOS steering and throttle
    /// locks released on its processor. Issued when Claudia takes the craft back from Manual Control because the link
    /// dropped, and on a Failed Debrief. It acts on the Mission's craft, not whichever vessel the player is in.
    /// </summary>
    public sealed class MakeCraftSafe : AdapterCommand
    {
        public readonly string Reason;
        /// <summary>The Mission's craft (<see cref="MissionCraft"/>); "" when none is on record, which means the active vessel.</summary>
        public readonly string VesselId;
        /// <summary>The craft's original kOS processor part, whose locks are released; null when none is on record.</summary>
        public readonly uint? ProcessorPartId;
        public MakeCraftSafe(string reason, string vesselId = "", uint? processorPartId = null)
        {
            Reason = reason;
            VesselId = vesselId ?? "";
            ProcessorPartId = processorPartId;
        }

        /// <summary>
        /// The vessel id to make safe: the Mission's craft when one is on record, else the active vessel's; null when
        /// there is nothing to act on.
        /// </summary>
        public string Target(string activeVesselId)
        {
            if (VesselId.Length > 0) return VesselId;
            return string.IsNullOrEmpty(activeVesselId) ? null : activeVesselId;
        }
    }

    /// <summary>
    /// Turn SAS off on the Mission's craft, so the player's SAS doesn't fight kOS steering on the next run. Issued when
    /// the player hands back from Manual Control. It acts on the Mission's craft, not whichever vessel the player is in.
    /// </summary>
    public sealed class TurnSasOff : AdapterCommand
    {
        /// <summary>The Mission's craft (<see cref="MissionCraft"/>); "" when none is on record, which means the active vessel.</summary>
        public readonly string VesselId;
        public TurnSasOff(string vesselId = "") { VesselId = vesselId ?? ""; }

        public string Target(string activeVesselId)
        {
            if (VesselId.Length > 0) return VesselId;
            return string.IsNullOrEmpty(activeVesselId) ? null : activeVesselId;
        }
    }

    /// <summary>A short on-screen message, for when Claudia needs the player (a plan to approve, a Debrief).</summary>
    public sealed class PostScreenMessage : AdapterCommand
    {
        public readonly string Text;
        /// <summary>Shown even with the Mission window open or another alert firing (Claudia took the craft back).</summary>
        public readonly bool Always;
        public PostScreenMessage(string text) { Text = text; }
        public PostScreenMessage(string text, bool always) : this(text) { Always = always; }
    }
}
