// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Ui
{
    /// <summary>Where the craft is, for the banner's "on the pad" / "in the VAB" / "in the SPH" line.</summary>
    public enum BannerSite
    {
        /// <summary>Anywhere without a craft to name (KSC, Tracking Station, main menu).</summary>
        None,
        Pad,
        Vab,
        Sph,
    }

    /// <summary>How the banner is laid out: art and text side by side in a box, or the art with the text below it.</summary>
    public enum BannerLayout
    {
        Wide,
        Narrow,
    }

    /// <summary>How a banner line is styled.</summary>
    public enum BannerTone
    {
        /// <summary>Bold, in the accent colour.</summary>
        Title,
        Secondary,
        Text,
    }

    public struct BannerLine
    {
        public readonly string Text;
        public readonly BannerTone Tone;

        public BannerLine(string text, BannerTone tone)
        {
            Text = text;
            Tone = tone;
        }
    }

    /// <summary>
    /// The welcome banner at the top of a fresh transcript: the Winged glare splat as block art, then Claudia's name and
    /// version, the tagline and the session's key facts. The adapter draws it; it is never a log entry, so it stays out of
    /// <see cref="MissionView.Log"/>, the transcript file and Claude's context.
    /// </summary>
    public static class WelcomeBanner
    {
        public const string Tagline = "I fly, you clean up";

        /// <summary>The transcript is at least this many monospace columns wide: the wide layout fits.</summary>
        public const int WideColumns = 66;

        /// <summary>
        /// The art: 7 rows by 28 columns of █ ▀ ▄ and spaces, each cell two vertical pixels, trailing spaces trimmed.
        /// Keep the characters and spacing exactly.
        /// </summary>
        public const string Art =
@"  ▄    ▄▄██▄▄  ▄▄██▄     ▀
    ▄██████████████████▄
  ▄██▀████████████████▀██▄
 ▄████▄   █▀████▀█   ▄████
  ▀█████▄▄▄██████▄▄▄█████▀▀
▀   ▀██████▀█████▀▀████▀  ▄
   ▄   ▀▀▀   ▀█▀    ▀▀  ▄";

        /// <summary>
        /// Whether a transcript starts with the banner. The transcript is never cleared, so the banner is its first item
        /// whether there is no Mission yet (a fresh window, or after End Mission) or a Mission is under way, where it stays
        /// above the Craft Check and is not repeated.
        /// </summary>
        public static bool Shows(MissionStage stage)
        {
            return true;
        }

        /// <summary>Wide when the transcript is at least <see cref="WideColumns"/> monospace columns wide.</summary>
        public static BannerLayout ChooseLayout(int columns)
        {
            return columns >= WideColumns ? BannerLayout.Wide : BannerLayout.Narrow;
        }

        /// <summary>How many whole monospace columns of <paramref name="charWidth"/> fit in <paramref name="width"/> after the padding.</summary>
        public static int Columns(float width, float charWidth, float padding)
        {
            if (charWidth <= 0) return 0;
            var columns = (int)((width - padding) / charWidth);
            return columns < 0 ? 0 : columns;
        }

        /// <summary>
        /// The text lines. The model, type and Spending Cap are the Mission's when one exists, otherwise the pending
        /// setup's (<paramref name="setupModel"/>, <paramref name="setupRemote"/>, <paramref name="setupCap"/>).
        /// <paramref name="goalSet"/> is the Mission Goal pinned in the start form.
        /// </summary>
        public static IList<BannerLine> Lines(MissionView view, string setupModel, bool setupRemote, decimal setupCap,
            BannerSite site, bool goalSet, string version)
        {
            var mission = view.Stage != MissionStage.None;
            var model = mission ? view.Model : setupModel;
            var remote = mission ? view.Remote : setupRemote;
            var cap = mission ? view.SpendingCap : setupCap;

            var lines = new List<BannerLine>
            {
                new BannerLine(ModInfo.Name + "  v" + version, BannerTone.Title),
                new BannerLine(Tagline, BannerTone.Secondary),
            };
            if (model.Length > 0) lines.Add(new BannerLine(model + " · " + (remote ? "Remote" : "Crewed"), BannerTone.Text));
            lines.Add(new BannerLine("Spending Cap $" + cap.ToString("0.00", CultureInfo.InvariantCulture), BannerTone.Text));

            var craft = view.CraftCheck.CraftName;
            if (craft.Length > 0 && site != BannerSite.None)
                lines.Add(new BannerLine(craft + (site == BannerSite.Pad ? " on the pad" : site == BannerSite.Vab ? " in the VAB" : " in the SPH"), BannerTone.Text));

            if (!mission)
                lines.Add(new BannerLine(goalSet ? "Check the craft, then Start Mission." : "Describe a Mission Goal to begin.", BannerTone.Secondary));
            return lines;
        }
    }
}
