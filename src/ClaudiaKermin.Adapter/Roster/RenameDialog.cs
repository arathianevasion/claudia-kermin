// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Roster;
using UnityEngine;

namespace ClaudiaKermin.Adapter.Roster
{
    /// <summary>The one-time dialog for a hired Kerbal whose name clashes with Claudia's.</summary>
    public static class RenameDialog
    {
        /// <param name="answered">Called once with true if the player declined (or gave a name that still clashes).</param>
        public static void Show(ProtoCrewMember kerbal, Action<bool> answered)
        {
            var newName = kerbal.name;
            var message = "Claudia Kermin can't join your crew while " + kerbal.name + " has \"" + ClaudiaIdentity.ClashFragment
                + "\" in their name. Rename them to let her in, or decline and she stays inactive in this save.";

            var dialog = new MultiOptionDialog(
                "ClaudiaKerminRename",
                message,
                "Claudia Kermin",
                HighLogic.UISkin,
                new DialogGUIBase[]
                {
                    new DialogGUITextInput(newName, false, 40, text => { newName = text; return text; }, 24f),
                    new DialogGUIHorizontalLayout(
                        new DialogGUIButton("Rename", () =>
                        {
                            var valid = !string.IsNullOrEmpty(newName.Trim()) && !RosterPlanner.ClashesWithClaudia(newName);
                            if (valid) kerbal.ChangeName(newName.Trim());
                            answered(!valid);
                        }, true),
                        new DialogGUIButton("Decline", () => answered(true), true)),
                });
            PopupDialog.SpawnPopupDialog(dialog, true, HighLogic.UISkin);
        }
    }
}
