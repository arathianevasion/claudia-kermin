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

        public StartScript(uint processorPartId, string label, string source)
        {
            ProcessorPartId = processorPartId;
            Label = label;
            Source = source;
        }
    }

    /// <summary>
    /// Abort the script running on the kOS processor in part <see cref="ProcessorPartId"/>, with make-safe
    /// (the existing abort path). Issued when the player ends a Mission that is Running.
    /// </summary>
    public sealed class AbortScript : AdapterCommand
    {
        public readonly uint ProcessorPartId;
        public readonly string Reason;

        public AbortScript(uint processorPartId, string reason)
        {
            ProcessorPartId = processorPartId;
            Reason = reason;
        }
    }

    /// <summary>A short on-screen message, for when Claudia needs the player (a plan to approve, a Debrief).</summary>
    public sealed class PostScreenMessage : AdapterCommand
    {
        public readonly string Text;
        public PostScreenMessage(string text) { Text = text; }
    }
}
