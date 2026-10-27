// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The Core Library as the core ships it: the bundle and the docs. Running it is in the kerboscript tests.</summary>
    public class CoreLibraryTests
    {
        private const string Script = "@lazyglobal off.\r\nlocal res is exec_node().\r\naddons:claudia:report(res).\r\n";

        // kOS refuses a declaration that hides a built-in ("clobber BUILTIN_FUNCTION r"); see KosBuiltinNames.
        [Fact]
        public void NoDocExampleNamesAVariableAfterAKosBuiltin()
        {
            var docs = CoreLibrary.Summaries() + "\n" + CoreLibrary.FullDocs();
            var bad = KosBuiltinNames.ClashesInExamples(docs);
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        [Fact]
        public void NoBundledFileBlockTestOrToolTestScriptDeclaresANameThatHidesAKosBuiltin()
        {
            var bad = KosBuiltinNames.Clashes("bundle", CoreLibrary.Bundle(Script));
            var root = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "ClaudiaKermin.sln"))) root = root.Parent;
            Assert.NotNull(root);
            foreach (var dir in new[] { "blocks", "tools" })
            {
                var scripts = System.IO.Directory.GetFiles(System.IO.Path.Combine(root.FullName, "tests", dir), "*.ks", System.IO.SearchOption.AllDirectories);
                Assert.NotEmpty(scripts);
                foreach (var file in scripts)
                    bad.AddRange(KosBuiltinNames.Clashes(file.Substring(root.FullName.Length + 1), System.IO.File.ReadAllText(file)));
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        // #325: land's `local deorbit is node(...)` hid the deorbit Building Block, so `add deorbit.` cast a function delegate.
        [Fact]
        public void NoLocalParameterOrSetTargetInTheBundleOrATestScriptSharesANameWithACoreLibraryFunction()
        {
            // The functions every file can see: the first-party library and kslib.
            var global = CoreLibrary.Files.Concat(CoreLibrary.Vendored).ToList();
            var functions = new System.Collections.Generic.HashSet<string>(global.SelectMany(f => KosBuiltinNames.FunctionNames(f.Source)));
            Assert.Contains("deorbit", functions);
            var bad = new System.Collections.Generic.List<string>();
            foreach (var file in global)
                bad.AddRange(KosBuiltinNames.ClashesWithFunctions(file.Name, file.Source, functions));
            var root = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "ClaudiaKermin.sln"))) root = root.Parent;
            Assert.NotNull(root);
            foreach (var dir in new[] { "blocks", "tools" })
                foreach (var file in System.IO.Directory.GetFiles(System.IO.Path.Combine(root.FullName, "tests", dir), "*.ks", System.IO.SearchOption.AllDirectories))
                    bad.AddRange(KosBuiltinNames.ClashesWithFunctions(file.Substring(root.FullName.Length + 1), System.IO.File.ReadAllText(file), functions));
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        [Theory]
        [InlineData("local deorbit is node(1, 0, 0, 2).")]
        [InlineData("parameter ctx, deorbit.")]
        [InlineData("for deorbit in list(1) { }")]
        [InlineData("set deorbit to 1.")]
        public void TheFunctionNameCheckCatchesEachKindOfDeclaration(string code)
        {
            Assert.NotEmpty(KosBuiltinNames.ClashesWithFunctions("t", code, new System.Collections.Generic.HashSet<string> { "deorbit" }));
        }

        [Fact]
        public void TheFunctionNameCheckIgnoresTheFunctionsOwnDeclarationCommentsAndStrings()
        {
            var names = new System.Collections.Generic.HashSet<string> { "deorbit" };
            Assert.Empty(KosBuiltinNames.ClashesWithFunctions("t", "function deorbit {\n}\n// local deorbit is 1.\nlocal v is \"set deorbit to 1.\".\nlocal descent_node is 1.", names));
        }

        [Theory]
        [InlineData("local north is 1.")]
        [InlineData("global encounter is 2.")]
        [InlineData("declare local r is 3.")]
        [InlineData("parameter a, north.")]
        [InlineData("parameter a, b is max(1, 2), heading.")]
        [InlineData("function ship { }")]
        [InlineData("for time in list(1) { }")]
        [InlineData("local function node { }")]
        public void TheBuiltinCheckCatchesEachKindOfDeclaration(string code)
        {
            Assert.NotEmpty(KosBuiltinNames.Clashes("t", code));
        }

        [Fact]
        public void TheBuiltinCheckIgnoresCommentsStringsAndNonBuiltins()
        {
            Assert.Empty(KosBuiltinNames.Clashes("t", "// local north is 1.\nlocal v_north is \"local north\". // global r\nparameter a, b is max(1, 2).\nlocal direction is 1."));
        }

        [Fact]
        public void TheBundleKeepsTheMissionScriptFirstSoItsLineNumbersStayRight()
        {
            var bundle = CoreLibrary.Bundle(Script);
            Assert.StartsWith("@lazyglobal off.\nlocal res is exec_node().\naddons:claudia:report(res).\n", bundle);
        }

        [Fact]
        public void TheBundleHoldsEveryLibraryFileWithOnlyTheScriptsLazyglobalDirective()
        {
            var bundle = CoreLibrary.Bundle(Script);
            Assert.Contains("function claudia_finish {", bundle);
            Assert.Contains("function exec_node {", bundle);
            Assert.Single(Regex.Matches(bundle, "(?m)^\\s*@lazyglobal"));
        }

        [Fact]
        public void TheBundleCarriesNoWaitCapAndItsWaitsWarpOnRails()
        {
            // ADR 0004: a wait longer than an orbit stays inside the sequence; nothing in the bundle refuses it.
            var bundle = CoreLibrary.Bundle(Script);
            Assert.DoesNotContain("claudia_config", bundle);
            Assert.DoesNotContain("wait_cap", bundle);
            Assert.DoesNotContain("claudia_check_wait", bundle);
            Assert.DoesNotContain("plan a Due Time", bundle);
            Assert.Contains("function claudia_wait_until {", bundle);
            Assert.Contains("set kuniverse:timewarp:mode to \"RAILS\".", bundle);
        }

        // #335: the warp trace counts real time at 1x only inside waits, so every wait loop says when it starts and ends.
        [Theory]
        [InlineData("claudia_warp_to")]
        public void EveryWaitLoopReportsItsWaitToTheWarpTraceAndEndsItOnEveryReturn(string function)
        {
            var bundle = CoreLibrary.Bundle(Script).Replace("\r\n", "\n");
            var body = Regex.Match(bundle, "(?ms)^function " + function + " \\{\n.*?^\\}\n").Value;
            Assert.NotEqual("", body);
            var lines = body.Split('\n').Select(l => l.Trim()).ToList();
            var start = lines.IndexOf("addons:claudia:waitstart(warp_end).");
            Assert.True(start > 0 && start < lines.FindIndex(l => l.StartsWith("until ")), function + " starts its wait before the loop");
            var returns = lines.Select((l, i) => new { l, i }).Where(x => x.l.StartsWith("return ")).ToList();
            Assert.NotEmpty(returns);
            foreach (var r in returns)
                Assert.True(lines[r.i - 1] == "addons:claudia:waitend().", function + ": '" + r.l + "' without waitend() before it");
        }

        [Fact]
        public void TheSharedDocsTellClaudiaWaitsWarpInsideTheScriptWithNoCap()
        {
            var shared = CoreLibrary.Summaries();
            Assert.Contains("on-rails warp", shared);
            Assert.Contains("1x at least 30 s before", shared);
            Assert.DoesNotContain("plan a Due Time instead", shared);
            Assert.Contains("\n- coast(options): ", shared);
            foreach (var text in new[] { "until [required]", "lead [30]" })
                Assert.Contains(text, CoreLibrary.FullDocs(new[] { "coast" }));
        }

        // #349: Claudia's warp guidance in the shared docs.
        [Fact]
        public void TheSharedDocsTellClaudiaToWaitThroughCoastsWithTheWarpHelpersAndNeverSetWarpHerself()
        {
            var shared = CoreLibrary.Summaries();
            Assert.Contains("Before a burn they plan the node, turn to it, and then warp, one SOI at a time.", shared);
            Assert.Contains("10 s before parachutes or a heat-shield drop", shared);
            Assert.Contains("use claudia_warp_to(ctx, ut, lead[, stop, cap]); fly every burn through exec_node.", shared);
            Assert.Contains("Never set WARP or WARPMODE yourself.", shared);
        }

        [Fact]
        public void TheSharedHelpersComeFirstAndEveryBlockHasDocsLedByItsSummary()
        {
            Assert.Equal("claudia_lib", CoreLibrary.Files.First().Name);
            Assert.NotEmpty(CoreLibrary.Blocks);
            foreach (var block in CoreLibrary.Blocks)
                Assert.StartsWith(block.Name + "(options): ", block.Docs[0]);
        }

        [Fact]
        public void SummariesListEachBlockOnceAndFullDocsCanBeNarrowedToSomeBlocks()
        {
            Assert.Contains("\n- exec_node(options): flies a maneuver node", CoreLibrary.Summaries());
            Assert.DoesNotContain("max_residual [0.5]", CoreLibrary.Summaries());
            Assert.Contains("max_residual [0.5]", CoreLibrary.FullDocs(new[] { "exec_node" }));
            Assert.Equal("", CoreLibrary.FullDocs(new[] { "no_such_block" }));
        }

        [Fact]
        public void LandAndAscendShipWithDocsOfTheirSpecOptionsAndResults()
        {
            Assert.Contains("\n- land(options): lands on an airless body", CoreLibrary.Summaries());
            Assert.Contains("\n- ascend(options): flies from the surface of an airless body", CoreLibrary.Summaries());
            var land = CoreLibrary.FullDocs(new[] { "land" });
            foreach (var text in new[] { "target [\"any\"]", "daylight [true]", "max_slope [8]", "max_divert [300]", "touchdown_speed [2]",
                "gear_alt [1000]", "burn_margin [1.10]", "slope", "touchdown_speed_actual", "distance_to_target", "best_effort", "geo" })
                Assert.Contains(text, land);
            var ascend = CoreLibrary.FullDocs(new[] { "ascend" });
            foreach (var text in new[] { "target_alt [\"auto\"]", "5 km above the body's highest terrain", "target_inc [0]", "ap_error" })
                Assert.Contains(text, ascend);
        }

        [Fact]
        public void LaunchAndCircularizeAreSummarisedAndDocumentTheirContractOptionsAndResults()
        {
            var summaries = CoreLibrary.Summaries();
            Assert.Contains("\n- launch(options): flies from the ground of a body with an atmosphere to a coast out of it; then circularize.", summaries);
            Assert.Contains("\n- circularize(options): makes the orbit circular at apoapsis, periapsis or an altitude in one burn.", summaries);
            var launch = CoreLibrary.FullDocs(new[] { "launch" });
            foreach (var option in new[] { "launch_at [\"now\"]", "target_ap [\"auto\"]", "target_inc [0]", "heading [\"lib_lazcalc\"]", "turn_start_speed [60]", "turn_shape [0.45]", "max_aoa [5]", "throttle_max_q [\"none\"]", "g, atm_height, surface_pressure [\"body\"]", "ap_error", "max_q", "coast_ap" })
                Assert.Contains(option, launch);
            var circularize = CoreLibrary.FullDocs(new[] { "circularize" });
            foreach (var option in new[] { "at [\"ap\"]", "tolerance [0.01]", "ecc" })
                Assert.Contains(option, circularize);
        }

        // #214: positionat(ship, t) is the craft at t around its body where the body is NOW, while
        // positionat(body, t) is where the body will be at t, so their difference is off by how far the
        // body moves before t (hundreds of km at the Mun). land's deorbit burn was ~230 m/s, not ~17, and
        // the lander hit the ground. The craft's position from its body at t is positionat(ship, t) - body:position.
        [Fact]
        public void NoLibraryFileTakesTheCraftsFuturePositionFromTheBodysFuturePosition()
        {
            var bad = Regex.Matches(CoreLibrary.Bundle(Script), @"positionat\(\s*ship\s*,[^)]*\)\s*-\s*positionat\(", RegexOptions.IgnoreCase);
            Assert.True(bad.Count == 0, string.Join("\n", bad.Cast<Match>().Select(m => m.Value)));
        }

        [Fact]
        public void EveryShippedBlockTestSpecParses()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "ClaudiaKermin.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var specs = System.IO.Directory.GetFiles(System.IO.Path.Combine(dir.FullName, "tests", "blocks"), "test.cfg", System.IO.SearchOption.AllDirectories);
            Assert.NotEmpty(specs);
            foreach (var path in specs)
            {
                var folder = System.IO.Path.GetDirectoryName(path);
                var spec = BlockTestSpec.Parse(System.IO.Path.GetFileName(folder), System.IO.File.ReadAllText(path));
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(folder, spec.Craft)), path + ": no " + spec.Craft);
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(folder, "scenario.sfs")), path + ": no scenario.sfs");
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(folder, "test.ks")), path + ": no test.ks");
            }
        }
    }
}
