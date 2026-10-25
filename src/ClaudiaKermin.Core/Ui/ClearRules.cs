// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Ui
{
    /// <summary>What the Clear button does now.</summary>
    public enum ClearAction
    {
        /// <summary>No Mission, or nothing shown that isn't hidden already: the button is disabled.</summary>
        None,
        /// <summary>A finished Mission: send <c>DismissMission</c>, so the window starts fresh.</summary>
        Dismiss,
        /// <summary>An active Mission: hide the entries shown so far, in the window only.</summary>
        Hide,
    }

    /// <summary>
    /// Clear in the Mission window. It never changes what Claude knows: a finished Mission is dismissed (folder and
    /// transcript kept) and an active one is only hidden from view.
    /// </summary>
    public static class ClearRules
    {
        public const string Command = "/clear";

        public const string Hidden = "Earlier messages hidden";

        public const string Show = "Show";

        public const string ActiveTooltip = "Clear the window. Claudia keeps everything she knows.";

        public const string FinishedTooltip = "Close this finished Mission and start fresh. Its folder and transcript are kept.";

        /// <summary>Typing exactly <c>/clear</c> runs Clear; any other text, <c>/…</c> included, is sent as normal.</summary>
        public static bool IsCommand(string text)
        {
            return text != null && text.Trim() == Command;
        }

        public static bool Finished(MissionStage stage)
        {
            return stage == MissionStage.Succeeded || stage == MissionStage.Ended || stage == MissionStage.Lost;
        }

        /// <param name="hiddenBefore">How many log entries are hidden already (0 when the transcript is whole).</param>
        public static ClearAction Action(MissionView view, int hiddenBefore)
        {
            if (view.Stage == MissionStage.None) return ClearAction.None;
            if (Finished(view.Stage)) return ClearAction.Dismiss;
            return view.Log.Count > hiddenBefore ? ClearAction.Hide : ClearAction.None;
        }

        public static string Tooltip(ClearAction action)
        {
            return action == ClearAction.Dismiss ? FinishedTooltip : ActiveTooltip;
        }
    }
}
