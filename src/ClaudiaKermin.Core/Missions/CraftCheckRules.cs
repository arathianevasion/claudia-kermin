// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>One entry of the fixed Blocker or Warning list.</summary>
    internal sealed class CraftCheckRule
    {
        public readonly string Code;
        public readonly CraftFindingSeverity Severity;
        public readonly string Title;
        /// <summary>When it applies, as Claude is told.</summary>
        public readonly string Condition;

        public CraftCheckRule(string code, CraftFindingSeverity severity, string title, string condition)
        {
            Code = code;
            Severity = severity;
            Title = title;
            Condition = condition;
        }
    }

    /// <summary>
    /// The Craft Check's fixed lists (spec stories 48 and 49). Claude may only report these, by code; the mod
    /// decides each finding's severity from the list, never from Claude. Nothing is added to the lists here
    /// without a spec change.
    /// </summary>
    internal static class CraftCheckRules
    {
        private const CraftFindingSeverity Blocker = CraftFindingSeverity.Blocker;
        private const CraftFindingSeverity Warning = CraftFindingSeverity.Warning;

        public static readonly IList<CraftCheckRule> All = new List<CraftCheckRule>
        {
            new CraftCheckRule("no_kos_processor", Blocker, "No kOS processor",
                "the craft has no kOS processor (no part has the kos_processor tag)."),
            new CraftCheckRule("no_seat_for_claudia", Blocker, "No seat for Claudia",
                "the Mission is crewed (she flies aboard) and the craft has no free crew seat for her."),
            new CraftCheckRule("no_probe_core_or_antenna", Blocker, "No probe core or antenna reaching home",
                "the Mission is remote (Claudia stays at Mission Control) and the craft has no probe core, or no antenna able to reach home."),
            new CraftCheckRule("delta_v_short", Blocker, "Not enough delta-v",
                "the craft's delta-v is below what the Mission Goal's route needs from the Delta-v Map plus the margin."),
            new CraftCheckRule("twr_below_one", Blocker, "Thrust-to-weight under 1.0",
                "the thrust over weight is under 1.0 at launch, or for a landing, on the body concerned."),
            new CraftCheckRule("no_parachutes_for_return", Blocker, "No parachutes for the return",
                "a crewed Mission returns to Kerbin and the craft has no parachutes."),
            new CraftCheckRule("required_experiments_missing", Blocker, "Required experiments missing",
                "the Mission Goal needs experiments the craft has no parts for."),
            new CraftCheckRule("child_vessel_problem", Blocker, "Child Vessel problem",
                "a Child Vessel the Mission Goal plans (a part that separates and carries a kOS processor) lacks, at its separation stage, a processor, "
                + "an antenna able to reach Claudia, delta-v, thrust-to-weight or electric power."),

            new CraftCheckRule("launch_twr_low", Warning, "Launch thrust-to-weight under 1.2",
                "the launch thrust over weight is under 1.2 (but not under 1.0, which is a Blocker)."),
            new CraftCheckRule("landing_twr_low", Warning, "Landing thrust-to-weight under 1.5",
                "the landing thrust over weight is under 1.5 (but not under 1.0, which is a Blocker)."),
            new CraftCheckRule("margin_under_25", Warning, "Delta-v margin under 25%",
                "the delta-v margin over the need is under 25% (but is not short, which is a Blocker)."),
            new CraftCheckRule("no_landing_legs", Warning, "No landing legs",
                "the Mission Goal includes a landing and the craft has no landing legs."),
            new CraftCheckRule("no_electric_generation_or_low_battery", Warning, "No electric charge generation or a low battery",
                "the craft generates no electric charge, or its batteries are small for the Mission Goal."),
            new CraftCheckRule("weak_control_authority", Warning, "Weak control authority",
                "the craft has little to steer with for its mass (few or no reaction wheels, RCS or gimbals)."),
            new CraftCheckRule("no_heat_shield", Warning, "No heat shield on a high-speed return",
                "the Mission Goal returns to Kerbin at high speed (from the Mun or farther) and the craft has no heat shield."),
            new CraftCheckRule("eva_science_required", Warning, "EVA science required",
                "the Mission Goal needs science only a Kerbal on EVA can do (Claudia doesn't go on EVA; the player would have to)."),
            new CraftCheckRule("boot_file_set", Warning, "Boot file set on Claudia's processor",
                "the processor Claudia will use (chosen_processor) has a boot file set."),
            new CraftCheckRule("manual_stretches_without_crew", Warning, "Manual stretches planned with no other crew aboard",
                "the Mission Goal plans stretches the player flies by hand and no crew other than Claudia is aboard."),
        };

        public static CraftCheckRule Find(string code)
        {
            return All.FirstOrDefault(r => r.Code == code);
        }

        /// <summary>The rules as the system prompt lists them.</summary>
        public static string Describe()
        {
            var text = new StringBuilder();
            text.Append("Blockers (the Mission cannot start; the player cannot override them):\n");
            foreach (var rule in All.Where(r => r.Severity == Blocker))
                text.Append("- ").Append(rule.Code).Append(": ").Append(rule.Title).Append(". Applies when ").Append(rule.Condition).Append('\n');
            text.Append("\nWarnings (the player must acknowledge each before the Mission starts):\n");
            foreach (var rule in All.Where(r => r.Severity == Warning))
                text.Append("- ").Append(rule.Code).Append(": ").Append(rule.Title).Append(". Applies when ").Append(rule.Condition).Append('\n');
            return text.ToString();
        }

        /// <summary>The structured-output schema: a summary and findings whose code can only be one of the lists'.</summary>
        public static string SchemaJson()
        {
            var codes = new JsonWriter().BeginArray();
            foreach (var rule in All) codes.String(rule.Code);
            codes.EndArray();
            return "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"summary\",\"findings\"],\"properties\":{"
                + "\"summary\":{\"type\":\"string\",\"description\":\"Your line to the player: concise mission-control voice, key numbers first, one or two sentences.\"},"
                + "\"findings\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,"
                + "\"required\":[\"code\",\"explanation\",\"fix\"],\"properties\":{"
                + "\"code\":{\"type\":\"string\",\"enum\":" + codes + "},"
                + "\"explanation\":{\"type\":\"string\",\"description\":\"What is wrong, with the numbers.\"},"
                + "\"fix\":{\"type\":\"string\",\"description\":\"A concrete change the player could make to the craft; \\\"\\\" if none is needed.\"}}}}}}";
        }
    }
}
