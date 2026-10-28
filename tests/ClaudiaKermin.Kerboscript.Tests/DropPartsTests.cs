// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>The drop_empty option's and drop_parts' pure helpers, run on kOS.Safe through the real bundle.</summary>
    public class DropPartsTests
    {
        private static string One(string expression)
        {
            return KosHarness.Run(CoreLibrary.Bundle("print " + expression + ".")).Single();
        }

        [Fact]
        public void Every_block_takes_drop_empty_and_it_defaults_to_auto()
        {
            Assert.Equal("", One("claudia_options(lexicon(\"drop_empty\", list(\"fuelTank\")), exec_node_defaults(), \"auto\"):error"));
            Assert.Equal("", One("claudia_options(lexicon(), exec_node_defaults(), \"auto\"):error"));
            Assert.Equal("auto", One("claudia_options(lexicon(), exec_node_defaults(), \"auto\"):opt:drop_empty"));
            Assert.Equal("auto", One("claudia_common_defaults(\"auto\"):drop_empty"));
            Assert.Equal("auto", One("claudia_common_opts(claudia_options(lexicon(), exec_node_defaults(), \"auto\"):opt):drop_empty"));
        }

        [Fact]
        public void A_list_still_works_and_an_empty_list_turns_dropping_off()
        {
            Assert.Equal("2", One("claudia_options(lexicon(\"drop_empty\", list(\"a\", \"b\")), exec_node_defaults(), \"auto\"):opt:drop_empty:length"));
            Assert.Equal("0", One("claudia_options(lexicon(\"drop_empty\", list()), exec_node_defaults(), \"auto\"):opt:drop_empty:length"));
            Assert.Equal("", One("claudia_options(lexicon(\"drop_empty\", list()), exec_node_defaults(), \"auto\"):error"));
        }

        [Theory]
        [InlineData("list(\"\", \"\", \"\")", "list(0)", "list(400)", "True")]        // empty tank, nose cones, fuel duct
        [InlineData("list(\"\", \"\")", "list(80)", "list(400)", "False")]             // the tank holds fuel
        [InlineData("list(\"\", \"rcs\")", "list(0)", "list(400)", "False")]           // empty tank + RCS block
        [InlineData("list(\"\", \"engine\")", "list(0)", "list(400)", "False")]        // empty tank + engine
        [InlineData("list(\"\", \"\")", "list()", "list()", "False")]                  // trim only, no tank
        [InlineData("list(\"\", \"parachute\")", "list(0)", "list(400)", "False")]     // empty tank + parachute
        [InlineData("list(\"\", \"shield\")", "list(0)", "list(400)", "False")]        // empty tank + heat shield
        [InlineData("list(\"\", \"\")", "list(0, 0)", "list(400, 400)", "True")]       // two empty tanks
        [InlineData("list(\"\", \"\")", "list(0, 9)", "list(400, 400)", "False")]      // one still holds fuel
        public void Auto_fires_only_for_empty_tanks_and_trim(string kinds, string amounts, string capacities, string expected)
        {
            Assert.Equal(expected, One("claudia_auto_drop_fires(" + kinds + ", " + amounts + ", " + capacities + ")"));
        }

        [Fact]
        public void A_bare_name_is_a_one_item_list_and_a_non_list_is_refused()
        {
            Assert.Equal("boosters", One("claudia_options(lexicon(\"drop_empty\", \"boosters\"), exec_node_defaults(), \"auto\"):opt:drop_empty[0]"));
            Assert.Equal("1", One("claudia_options(lexicon(\"drop_empty\", \"boosters\"), exec_node_defaults(), \"auto\"):opt:drop_empty:length"));
            Assert.Equal("drop_empty must be a list of part names or tags", One("claudia_options(lexicon(\"drop_empty\", 3), exec_node_defaults(), \"auto\"):error"));
        }

        [Theory]
        [InlineData("fuelTank", "", "list(\"fuelTank\")", "True")]
        [InlineData("fuelTank", "", "list(\"FUELTANK\")", "True")]     // any case
        [InlineData("fuelTank", "boosters", "list(\"x\", \"Boosters\")", "True")]
        [InlineData("fuelTank", "", "list(\"boosters\")", "False")]
        [InlineData("fuelTank", "", "list(\"\")", "False")]            // an empty name would match every untagged part: never
        [InlineData("fuelTank", "", "list()", "False")]
        public void A_part_matches_by_name_or_tag_ignoring_case(string name, string tag, string wanted, string expected)
        {
            Assert.Equal(expected, One("claudia_drop_matches(\"" + name + "\", \"" + tag + "\", " + wanted + ")"));
        }

        [Theory]
        [InlineData("list(0, 0.004)", "list(400, 400)", "True")]        // dregs under the 0.01 unit floor
        [InlineData("list(0, 5)", "list(400, 400)", "False")]           // one tank still holds fuel
        [InlineData("list(0.005)", "list(1)", "True")]                  // tiny tank: 0.01 units counts as empty
        [InlineData("list()", "list()", "False")]                       // no tank: not empty, so never dropped for it
        public void A_set_is_empty_only_when_every_tank_in_it_is(string amounts, string capacities, string expected)
        {
            Assert.Equal(expected, One("claudia_tanks_empty(" + amounts + ", " + capacities + ")"));
        }

        [Fact]
        public void Charge_air_and_ablator_are_not_fuel()
        {
            Assert.Equal("False", One("claudia_is_propellant(\"ElectricCharge\")"));
            Assert.Equal("False", One("claudia_is_propellant(\"IntakeAir\")"));
            Assert.Equal("False", One("claudia_is_propellant(\"Ablator\")"));
            Assert.Equal("True", One("claudia_is_propellant(\"LiquidFuel\")"));
            Assert.Equal("True", One("claudia_is_propellant(\"SolidFuel\")"));
        }

        [Fact]
        public void The_drop_parts_block_needs_a_list_of_names()
        {
            Assert.Equal("parts is required", One("drop_parts_names(\"required\")"));
            Assert.Equal("parts must be a list of part names or tags", One("drop_parts_names(list())"));
            Assert.Equal("parts must be a list of part names or tags", One("drop_parts_names(4)"));
            Assert.Equal("1", One("drop_parts_names(\"boosters\"):length"));
            Assert.Equal("2", One("drop_parts_names(list(\"a\", \"b\")):length"));
        }

        [Fact]
        public void Drop_parts_is_one_of_the_blocks_and_its_docs_name_its_options()
        {
            Assert.Contains(CoreLibrary.Blocks, b => b.Name == "drop_parts");
            var docs = CoreLibrary.FullDocs(new[] { "drop_parts" });
            Assert.Contains("parts [required]", docs);
            Assert.Contains("dropped", docs);
            Assert.Contains("drop_empty", CoreLibrary.Summaries());
        }
    }
}
