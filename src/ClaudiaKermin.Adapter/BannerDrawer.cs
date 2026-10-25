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
        private GUIStyle _art, _title, _tagline, _secondary, _text;
        private GUISkin _stylesFor;

        /// <summary>
        /// Draws the banner in a transcript <paramref name="width"/> wide. The pending setup stands in for the Mission's
        /// model and type until there is a Mission.
        /// </summary>
        public void Draw(MissionView view, float width, string setupModel, bool setupRemote, bool goalSet)
        {
            if (!WelcomeBanner.Shows(view.Stage)) return;
            Styles();
            var lines = WelcomeBanner.Lines(view, setupModel, setupRemote, Site(), goalSet, ModInfo.Version);
            // Measured, not counted: the art's width plus the widest text line, against the card's inner width.
            var gap = ClaudiaSkin.Gap * 2;
            var widest = 0f;
            foreach (var line in lines) widest = Mathf.Max(widest, StyleOf(line).CalcSize(new GUIContent(line.Text)).x);
            var artWidth = _art.CalcSize(new GUIContent(WelcomeBanner.Art)).x;
            var wide = WelcomeBanner.ChooseLayout(width - 2 * ClaudiaSkin.Padding, artWidth, gap, widest) == BannerLayout.Wide;

            // No expanding height anywhere: the card is as tall as its content and the log follows it.
            if (wide)
            {
                ClaudiaSkin.BeginCard(GUILayout.ExpandHeight(false));
                GUILayout.BeginHorizontal(GUILayout.ExpandHeight(false));
                DrawArt();
                GUILayout.Space(gap);
                GUILayout.BeginVertical(GUILayout.ExpandHeight(false));
                DrawLines(lines);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                GUILayout.Label(WelcomeBanner.ClosingLine, _secondary);
                ClaudiaSkin.EndCard();
            }
            else
            {
                DrawArt();
                GUILayout.Space(ClaudiaSkin.Gap);
                DrawLines(lines);
                GUILayout.Label(WelcomeBanner.ClosingLine, _secondary);
                GUILayout.Space(ClaudiaSkin.Gap);
            }
        }

        private GUIStyle StyleOf(BannerLine line)
        {
            return line.Tone == BannerTone.Title ? _title
                : line.Tone == BannerTone.Tagline ? _tagline
                : line.Tone == BannerTone.Secondary ? _secondary : _text;
        }

        // One label for every row, so the half blocks meet.
        private void DrawArt()
        {
            GUILayout.Label(WelcomeBanner.Art, _art, GUILayout.ExpandWidth(false));
        }

        private void DrawLines(System.Collections.Generic.IList<BannerLine> lines)
        {
            foreach (var line in lines)
                GUILayout.Label(line.Text, StyleOf(line));
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
            // Bold at the title's size (Body, scaled) in the normal text colour: never the secondary grey.
            _tagline = new GUIStyle(ClaudiaSkin.Body) { fontStyle = FontStyle.Bold };
            _tagline.normal.textColor = ClaudiaSkin.Text;
            _secondary = ClaudiaSkin.Secondary;
            _text = ClaudiaSkin.Body;
        }
    }
}
