// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.ScriptCheck
{
    /// <summary>
    /// The forbidden list, in the spec's words (user story 139), and the kOS 1.6 names behind each item.
    /// Allowed, and so absent here: staging, action groups, DOEVENT and DOACTION on own parts, rails warp,
    /// maneuver nodes, HUDTEXT, VECDRAW, reading other vessels and targeting bodies.
    /// </summary>
    internal static class ForbiddenList
    {
        public const string Revert = "revert";
        public const string Quickload = "quickload";
        public const string Quicksave = "quicksave";
        public const string SwitchVessels = "switch vessels";
        public const string LaunchCraft = "launch craft";
        public const string Pause = "pause";
        public const string TouchFiles = "touch files";
        public const string ChangeConfig = "change kOS config";
        public const string RebootOrShutDown = "reboot or shut down processors";
        public const string SetBootFiles = "set boot files";
        public const string OtherAddons = "use addons other than Claudia's";
        public const string OtherVessels = "change or message other vessels";
        public const string GuiWindows = "open GUI windows";
        public const string FreezeGame = "freeze the game";
        /// <summary>
        /// Planning Tools are for Claudia while she plans, and reach kOS only through the bridge a block test opens (ADR 0006); a
        /// Mission Script calls Building Blocks instead.
        /// </summary>
        public const string PlanningTools = "call Planning Tools";
        /// <summary>
        /// The mod's calculations that Building Blocks call while they fly (ADR 0006): only the Core Library may call them, so a
        /// Mission Script passes a block its goal instead (ADR 0007).
        /// </summary>
        public const string BlockInternal = "call block-internal calculations";
        /// <summary>A Mission Script defining a function (or lock) the Core Library defines, which would change a block's code (ADR 0007).</summary>
        public const string RedefineLibrary = "redefine a Core Library function";
        /// <summary>
        /// Switching SAS on (RCS Use): SAS fights kOS steering and fires RCS through holds, so it stays off while a Mission Script runs.
        /// Only Claudia's code is held to it: the Core Library's claudia_make_safe turns SAS on at an abort.
        /// </summary>
        public const string SasOn = "SAS must stay off while a Mission Script runs: it fights kOS steering and fires RCS through holds";
        /// <summary>From the allowed list: <c>makebuiltindelegate</c> is allowed only with a constant argument.</summary>
        public const string ComputedDelegate = "makebuiltindelegate without a constant argument";

        /// <summary>Built-in functions (OpcodeCall targets, and function pointers to them).</summary>
        public static readonly Dictionary<string, string> Functions = Table(
            TouchFiles, "copypath movepath deletepath create createdir open exists writejson readjson volume run runpath runoncepath "
                + "load logfile switch edit cd chdir compile copy_deprecated delete_deprecated rename_file_deprecated rename_volume_deprecated",
            RebootOrShutDown, "reboot shutdown",
            GuiWindows, "gui",
            FreezeGame, "debugfreezegame");

        /// <summary>Suffixes forbidden on anything (they exist only on KUNIVERSE, processors, volumes or connections).</summary>
        public static readonly Dictionary<string, string> Suffixes = Table(
            Revert, "revertto reverttolaunch reverttoeditor",
            Quickload, "quickload quickloadfrom",
            Quicksave, "quicksave quicksaveto",
            SwitchVessels, "forcesetactivevessel forceactive",
            LaunchCraft, "launchcraft launchcraftfrom launchcraftwithcrewfrom",
            Pause, "pause",
            TouchFiles, "volume",
            RebootOrShutDown, "deactivate",
            OtherVessels, "sendmessage",
            PlanningTools, "planningtool",
            BlockInternal, "landingsite deorbitburn reentryimpact turntime ejectionburn");

        /// <summary>Suffixes forbidden on another vessel or its parts: they change it. Reading stays allowed.</summary>
        public static readonly HashSet<string> OtherVesselMutators = Set(
            "doevent doaction setfield undock controlfrom activate shutdown control starttracking");

        /// <summary>The ADDONS suffixes a script may use: Claudia's own addon, and asking whether an addon is there.</summary>
        public static readonly HashSet<string> AllowedAddonSuffixes = Set("claudia available hasaddon");

        /// <summary>The names of the functions the Core Library (with the vendored scripts) defines, as the mod bundles it.</summary>
        public static readonly Lazy<HashSet<string>> LibraryFunctions = new Lazy<HashSet<string>>(() =>
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(CoreLibrary.Bundle(""), @"^\s*(?:(?:global|local)\s+)?function\s+(\w+)", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                names.Add(match.Groups[1].Value);
            return names;
        });

        /// <summary>LIST kinds that read volumes or files (<c>list files</c>, <c>list volumes in v</c>).</summary>
        public static readonly HashSet<string> FileListKinds = Set("files volumes");

        /// <summary>
        /// Every kOS 1.6.0.1 built-in function name, so the check compiles exactly as kOS does (built-ins as
        /// built-in calls, <c>name@</c> as <c>makebuiltindelegate</c>). Taken from the [Function] attributes
        /// in kOS.Safe.dll and kOS.dll.
        /// </summary>
        public static readonly HashSet<string> BuiltIns = Set(
            "droppriority debugdump debugfreezegame abs anglediff arccos arcsin arctan arctan2 cd chdir ceiling char constant "
            + "copy_deprecated copypath cos create createdir delete_deprecated deletepath exists floor lex lexicon list ln load "
            + "log10 logfile max min mod movepath open path print printat queue random randomseed range readjson reboot "
            + "rename_file_deprecated rename_volume_deprecated round run scriptpath selectautopilotmode uniqueset shutdown sin "
            + "sqrt stack switch tan toggleflybywire unchar volume writejson makebuiltindelegate pidloop profileresult "
            + "addalarm add allwaypoints angleaxis body bodyatmosphere bodyexists bounds buildlist career clearguis clearscreen "
            + "createorbit deletealarm edit getvoice heading highlight hsv hsva hudtext latlng listalarms lookdirup node note "
            + "orbitat positionat printlist processor q remove rgb rgba rotatefromto r slidenote stage stopallvoices timespan "
            + "timestamp time transfer transferall clearvecdraws vecdraw vecdrawargs v vang vectorangle vcrs vectorcrossproduct "
            + "vdot vectordotproduct vxcl vectorexclude velocityat vessel waypoint gui warpto");

        private static Dictionary<string, string> Table(params string[] itemsAndNames)
        {
            var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < itemsAndNames.Length; i += 2)
                foreach (var name in Set(itemsAndNames[i + 1])) table.Add(name, itemsAndNames[i]);
            return table;
        }

        private static HashSet<string> Set(string names)
        {
            return new HashSet<string>(names.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        }
    }
}
