// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Ui
{
    /// <summary>
    /// The default size of each window and the Capcom strip, derived from the UI's font scale so that changing the scale
    /// resizes them too. Each is its design size (at scale 1) times the scale, rounded to whole pixels; the design sizes
    /// are chosen so the 1.2 scale gives the 500×800 Mission window, 450×700 Settings window, 740 px strip and 640 px panel.
    /// </summary>
    public static class DefaultSizes
    {
        public const float MissionWidthDesign = 417, MissionHeightDesign = 667;
        public const float SettingsWidthDesign = 375, SettingsHeightDesign = 583;
        public const float StripWidthDesign = 617;
        public const float PanelHeightDesign = 533;
        // Minimums, chosen so the 1.2 scale gives the 430×460 Mission and 450×400 Settings minimums.
        public const float MissionMinWidthDesign = 358, MissionMinHeightDesign = 383;
        public const float SettingsMinWidthDesign = 375, SettingsMinHeightDesign = 333;

        public static float Scale(float designPx, float fontScale)
        {
            return (float)Math.Round(designPx * fontScale, MidpointRounding.AwayFromZero);
        }

        public static WindowBox Mission(float fontScale, float x, float y)
        {
            return new WindowBox(x, y, Scale(MissionWidthDesign, fontScale), Scale(MissionHeightDesign, fontScale));
        }

        public static WindowBox Settings(float fontScale, float x, float y)
        {
            return new WindowBox(x, y, Scale(SettingsWidthDesign, fontScale), Scale(SettingsHeightDesign, fontScale));
        }

        public static WindowBox MissionMin(float fontScale)
        {
            return new WindowBox(0, 0, Scale(MissionMinWidthDesign, fontScale), Scale(MissionMinHeightDesign, fontScale));
        }

        public static WindowBox SettingsMin(float fontScale)
        {
            return new WindowBox(0, 0, Scale(SettingsMinWidthDesign, fontScale), Scale(SettingsMinHeightDesign, fontScale));
        }

        public static float StripWidth(float fontScale) { return Scale(StripWidthDesign, fontScale); }

        public static float PanelHeight(float fontScale) { return Scale(PanelHeightDesign, fontScale); }
    }
}
