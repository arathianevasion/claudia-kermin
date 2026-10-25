// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// Draws the welcome banner, the first item of a fresh transcript: the Claudia art in icon green beside the text in
    /// a card (wide), or the art with the text below it (narrow). Drawing only: the banner is never a log entry, so it
    /// stays out of the Mission log, the transcript file and Claude's context. What it says and which layout fits are in
    /// <see cref="WelcomeBanner"/>.
    /// </summary>
    public sealed class BannerDrawer
    {
        private GUIStyle _art, _title, _secondary, _text;
        private GUISkin _stylesFor;
        private float _charWidth;

        /// <summary>
        /// Draws the banner in a transcript <paramref name="width"/> wide. The pending setup stands in for the Mission's
        /// model, type and Spending Cap until there is a Mission.
        /// </summary>
        public void Draw(MissionView view, float width, string setupModel, bool setupRemote, decimal setupCap, bool goalSet)
        {
            if (!WelcomeBanner.Shows(view.Stage)) return;
            Styles();
            var lines = WelcomeBanner.Lines(view, setupModel, setupRemote, setupCap, Site(), goalSet, ModInfo.Version);
            // The card's padding and the art's gap to the text come off the width before counting columns.
            var columns = WelcomeBanner.Columns(width, _charWidth, 2 * ClaudiaSkin.Padding + ClaudiaSkin.Gap);
            var wide = WelcomeBanner.ChooseLayout(columns) == BannerLayout.Wide;

            if (wide)
            {
                ClaudiaSkin.BeginCard();
                GUILayout.BeginHorizontal();
                DrawArt();
                GUILayout.Space(ClaudiaSkin.Gap * 2);
                GUILayout.BeginVertical();
                GUILayout.FlexibleSpace();
                DrawLines(lines);
                GUILayout.FlexibleSpace();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                ClaudiaSkin.EndCard();
            }
            else
            {
                DrawArt();
                GUILayout.Space(ClaudiaSkin.Gap);
                DrawLines(lines);
                GUILayout.Space(ClaudiaSkin.Gap);
            }
        }

        // One label for every row, so the half blocks meet.
        private void DrawArt()
        {
            GUILayout.Label(WelcomeBanner.Art, _art, GUILayout.ExpandWidth(false));
        }

        private void DrawLines(System.Collections.Generic.IList<BannerLine> lines)
        {
            foreach (var line in lines)
                GUILayout.Label(line.Text, line.Tone == BannerTone.Title ? _title : line.Tone == BannerTone.Secondary ? _secondary : _text);
        }

        private static BannerSite Site()
        {
            if (HighLogic.LoadedSceneIsEditor) return EditorDriver.editorFacility == EditorFacility.SPH ? BannerSite.Sph : BannerSite.Vab;
            return HighLogic.LoadedSceneIsFlight ? BannerSite.Pad : BannerSite.None;
        }

        private void Styles()
        {
            if (_art != null && ReferenceEquals(_stylesFor, HighLogic.Skin)) return;
            _stylesFor = HighLogic.Skin;
            // The skin's monospace font at 12 px (times FontScale), with no padding or margin so the rows touch.
            _art = new GUIStyle(ClaudiaSkin.Mono)
            {
                fontSize = ClaudiaSkin.Scaled(12),
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                wordWrap = false,
                richText = false,
            };
            _art.normal.textColor = _art.hover.textColor = _art.active.textColor = ClaudiaSkin.IconGreen;
            _title = new GUIStyle(ClaudiaSkin.Body) { fontStyle = FontStyle.Bold };
            _title.normal.textColor = ClaudiaSkin.Accent;
            _secondary = ClaudiaSkin.Secondary;
            _text = ClaudiaSkin.Body;
            // One block cell is as wide as any monospace character.
            _charWidth = _art.CalcSize(new GUIContent("█")).x;
        }
    }
}
