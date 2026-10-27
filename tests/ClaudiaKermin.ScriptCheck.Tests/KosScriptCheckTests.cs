// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.IO;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.ScriptCheck.Tests
{
    /// <summary>
    /// The real Script Check, compiling with kOS.Safe: a script using each forbidden item fails naming that
    /// item (the list is the spec's, user story 139), and each allowed item passes.
    /// </summary>
    public sealed class KosScriptCheckTests
    {
        private readonly IScriptCheck _check = new KosScriptCheck();

        private void AssertForbidden(string item, string script)
        {
            var result = _check.Check(script);
            Assert.True(result.CompileError == null, result.CompileError);
            Assert.False(result.Passed, "passed: " + script);
            Assert.Contains(result.Forbidden, f => f.EndsWith("(" + item + ")"));
        }

        private void AssertAllowed(string script)
        {
            var result = _check.Check(script);
            Assert.True(result.Passed, string.Join("; ", result.Problems.ToArray()) + "\n" + script);
        }

        // ---- Lints: warn, never refuse ----

        [Theory]
        [InlineData("circularize().\nfrom {local i is 0.} until i > 20 step {set i to i + 1.} do {\n  local p is latlng(i, 10).\n  print p:terrainheight.\n}\n", "landing-site search")]
        [InlineData("until ship:orbit:nextpatch:periapsis > 30000 {\n  set nextnode:prograde to nextnode:prograde + 1.\n}\n", "next patch")]
        [InlineData("until false {\n  if stage:liquidfuel < 0.1 { stage. }\n  wait 0.1.\n}\n", "staging loop")]
        public void AReinventionLintWarnsAndTheScriptStillPasses(string script, string warning)
        {
            var result = _check.Check(script);
            Assert.True(result.Passed, string.Join("; ", result.Problems.ToArray()));
            Assert.Contains(result.Warnings, w => w.Contains(warning));
            Assert.DoesNotContain(result.Problems, p => p.Contains(warning));
        }

        [Fact]
        public void AnOrdinaryScriptHasNoWarnings()
        {
            var result = _check.Check("local res is lexicon(\"success\", true).\nstage.\nuntil res:success { print 1. }\nlocal p is latlng(1, 2).\n");
            Assert.True(result.Passed, string.Join("; ", result.Problems.ToArray()));
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void TheBaseLayersWorkedExamplePassesWithNoWarnings()
        {
            var result = _check.Check(ClaudiaKermin.Core.Missions.MissionPrompts.WorkedExampleScript);
            Assert.True(result.Passed, string.Join("; ", result.Problems.ToArray()));
            Assert.Empty(result.Warnings);
        }

        // ---- Forbidden: one theory per item of the spec's list ----

        [Theory]
        [InlineData("kuniverse:reverttolaunch().")]
        [InlineData("kuniverse:reverttoeditor().")]
        [InlineData("kuniverse:revertto(\"VAB\").")]
        public void Revert(string script) { AssertForbidden("revert", script); }

        [Theory]
        [InlineData("kuniverse:quickload().")]
        [InlineData("kuniverse:quickloadfrom(\"before burn\").")]
        public void Quickload(string script) { AssertForbidden("quickload", script); }

        [Theory]
        [InlineData("kuniverse:quicksave().")]
        [InlineData("kuniverse:quicksaveto(\"before burn\").")]
        public void Quicksave(string script) { AssertForbidden("quicksave", script); }

        [Theory]
        [InlineData("set kuniverse:activevessel to vessel(\"Probe\").")]
        [InlineData("kuniverse:forcesetactivevessel(vessel(\"Probe\")).")]
        [InlineData("vessel(\"Probe\"):forceactive().")]
        public void SwitchVessels(string script) { AssertForbidden("switch vessels", script); }

        [Theory]
        [InlineData("kuniverse:launchcraft(kuniverse:getcraft(\"Lander\", \"VAB\")).")]
        [InlineData("kuniverse:launchcraftfrom(kuniverse:getcraft(\"Lander\", \"VAB\"), \"LaunchPad\").")]
        public void LaunchCraft(string script) { AssertForbidden("launch craft", script); }

        [Theory]
        [InlineData("kuniverse:pause().")]
        public void Pause(string script) { AssertForbidden("pause", script); }

        [Theory]
        [InlineData("set warpmode to \"PHYSICS\".")]
        [InlineData("set warpmode to claudia_mode.")]
        [InlineData("set kuniverse:timewarp:mode to \"PHYSICS\".")]
        [InlineData("set kuniverse:timewarp:mode to claudia_mode.")]
        [InlineData("local tw is kuniverse:timewarp.\nset tw:mode to \"PHYSICS\".")]
        [InlineData("set warpmode to choose \"PHYSICS\" if ship:altitude < 70000 else \"RAILS\".")]
        public void PhysicsWarpIsNoLongerRefusedByTheScriptCheck(string script)
        {
            // Whether the craft is on a Coast is known only at run time (ADR 0008); the runtime gate decides.
            AssertAllowed(script);
        }

        [Theory]
        [InlineData("copypath(\"0:/a.ks\", \"1:/a.ks\").")]
        [InlineData("movepath(\"1:/a.ks\", \"1:/b.ks\").")]
        [InlineData("deletepath(\"1:/a.ks\").")]
        [InlineData("log \"x\" to \"0:/out.txt\".")]
        [InlineData("run other.")]
        [InlineData("runpath(\"0:/other.ks\").")]
        [InlineData("runoncepath(\"0:/other.ks\").")]
        [InlineData("writejson(list(1), \"1:/x.json\").")]
        [InlineData("print readjson(\"1:/x.json\").")]
        [InlineData("local f is open(\"1:/x.txt\").")]
        [InlineData("create(\"1:/x.txt\").")]
        [InlineData("createdir(\"1:/d\").")]
        [InlineData("print exists(\"1:/x.txt\").")]
        [InlineData("list files.")]
        [InlineData("list volumes in vols.")]
        [InlineData("switch to 0.")]
        [InlineData("cd(\"1:/d\").")]
        [InlineData("compile \"0:/a.ks\" to \"1:/a.ksm\".")]
        [InlineData("edit \"x.ks\".")]
        [InlineData("print archive:files.")]
        [InlineData("print core:volume:freespace.")]
        [InlineData("print volume(0):name.")]
        [InlineData("local c is copypath@.\nc:call(\"0:/a\", \"1:/a\").")]
        public void TouchFiles(string script) { AssertForbidden("touch files", script); }

        [Theory]
        [InlineData("set config:ipu to 2000.")]
        [InlineData("local c is config.\nset c:ipu to 2000.")]
        public void ChangeKosConfig(string script) { AssertForbidden("change kOS config", script); }

        [Theory]
        [InlineData("reboot.")]
        [InlineData("shutdown.")]
        [InlineData("processor(\"second\"):deactivate().")]
        public void RebootOrShutDownProcessors(string script) { AssertForbidden("reboot or shut down processors", script); }

        [Theory]
        [InlineData("set core:bootfilename to \"boot.ks\".")]
        [InlineData("set processor(\"second\"):bootfilename to \"boot.ks\".")]
        public void SetBootFiles(string script) { AssertForbidden("set boot files", script); }

        [Theory]
        [InlineData("print addons:rt:available.")]
        [InlineData("addons:tr:settarget(body(\"Mun\")).")]
        [InlineData("local a is addons.\nprint a:claudia.")]
        public void UseAddonsOtherThanClaudias(string script) { AssertForbidden("use addons other than Claudia's", script); }

        [Theory]
        [InlineData("vessel(\"Probe\"):connection:sendmessage(\"hi\").")]
        [InlineData("target:connection:sendmessage(\"hi\").")]
        [InlineData("vessel(\"Probe\"):partstagged(\"dish\")[0]:getmodule(\"ModuleRTAntenna\"):doevent(\"activate\").")]
        [InlineData("local p is vessel(\"Probe\").\np:parts[0]:getmodule(\"ModuleLight\"):doaction(\"toggle light\", true).")]
        [InlineData("set vessel(\"Probe\"):shipname to \"Renamed\".")]
        [InlineData("set vessel(\"Probe\"):control:pilotmainthrottle to 1.")]
        [InlineData("list targets in t.\nt[0]:parts[0]:getmodule(\"ModuleLight\"):doevent(\"lights on\").")]
        public void ChangeOrMessageOtherVessels(string script) { AssertForbidden("change or message other vessels", script); }

        [Theory]
        [InlineData("local g is gui(200).\ng:show().")]
        public void OpenGuiWindows(string script) { AssertForbidden("open GUI windows", script); }

        [Theory]
        [InlineData("debugfreezegame(1).")]
        public void FreezeTheGame(string script) { AssertForbidden("freeze the game", script); }

        [Theory]
        [InlineData("local d is makebuiltindelegate(ship:name).")]
        public void MakeBuiltinDelegateWithAComputedName(string script) { AssertForbidden("makebuiltindelegate without a constant argument", script); }

        [Fact]
        public void MakeBuiltinDelegateOfAForbiddenFunctionIsThatFunction()
        {
            AssertForbidden("touch files", "local d is makebuiltindelegate(\"deletepath\").");
        }

        // ---- Allowed ----

        [Fact]
        public void Staging() { AssertAllowed("stage.\nwait until stage:ready.\nprint stage:number."); }

        [Fact]
        public void ActionGroups() { AssertAllowed("toggle ag1.\nag2 on.\ngear on.\nset brakes to true.\nlights off.\nrcs on.\nsas off."); }

        [Fact]
        public void DoEventOnOwnParts()
        {
            AssertAllowed("ship:partstagged(\"chute\")[0]:getmodule(\"ModuleParachute\"):doevent(\"deploy chute\").\n"
                + "local p is ship:partstagged(\"light\")[0].\np:getmodule(\"ModuleLight\"):doevent(\"lights on\").");
        }

        [Fact]
        public void DoActionOnOwnParts()
        {
            AssertAllowed("core:part:getmodule(\"ModuleLight\"):doaction(\"toggle light\", true).\n"
                + "for p in ship:partstagged(\"dish\") { p:getmodule(\"ModuleDeployableAntenna\"):doaction(\"extend antenna\", true). }");
        }

        [Fact]
        public void RailsWarp()
        {
            AssertAllowed("set warpmode to \"RAILS\".\nset warp to 3.\nset kuniverse:timewarp:mode to \"RAILS\".\n"
                + "kuniverse:timewarp:warpto(time:seconds + 600).\nwarpto(time:seconds + 600).\nkuniverse:timewarp:cancelwarp().\nset warp to 0.");
        }

        [Fact]
        public void ManeuverNodes()
        {
            AssertAllowed("local n is node(time:seconds + 600, 0, 0, 120).\nadd n.\nset n:prograde to 130.\n"
                + "print nextnode:deltav:mag.\nlock steering to nextnode:burnvector.\nremove nextnode.");
        }

        [Fact]
        public void HudText() { AssertAllowed("hudtext(\"Claudia: burn in 10 s\", 5, 2, 15, white, false)."); }

        [Fact]
        public void VecDraw()
        {
            AssertAllowed("local arrow is vecdraw(v(0,0,0), ship:velocity:orbit, red, \"velocity\", 1, true).\n"
                + "set arrow:show to false.\nclearvecdraws().");
        }

        [Fact]
        public void ReadingOtherVessels()
        {
            AssertAllowed("local probe is vessel(\"Probe\").\nprint probe:altitude.\nprint probe:orbit:apoapsis.\n"
                + "print vessel(\"Probe\"):partstagged(\"dish\"):length.\nprint probe:parts[0]:getmodule(\"ModuleLight\"):hasevent(\"lights on\").\n"
                + "list targets in t.\nprint t[0]:name.\nprint (probe:position - ship:position):mag.");
        }

        [Fact]
        public void TargetingBodies() { AssertAllowed("set target to body(\"Mun\").\nprint target:name.\nprint target:orbit:inclination."); }

        [Fact]
        public void MakeBuiltinDelegateWithAConstantArgument()
        {
            AssertAllowed("local d is makebuiltindelegate(\"round\").\nprint d:call(1.25).\nlocal e is print@.\ne:call(\"x\").");
        }

        [Fact]
        public void ReportingTheScriptResultThroughClaudiasAddon()
        {
            AssertAllowed("if addons:available(\"claudia\") { addons:claudia:report(lexicon(\"success\", true, \"reason\", \"done\")). }");
        }

        [Theory]
        [InlineData("local p is addons:claudia:planningtool(\"predict_burn\", lexicon(\"burns\", list())).")]
        [InlineData("local c is addons:claudia.\nprint c:planningtool(\"predict_burn\", lexicon()).")]
        [InlineData("print addons:claudia:PlanningTool(\"stage_performance\", lexicon(\"situation\", \"vacuum\")):stages.")]
        public void CallPlanningToolsFromAMissionScript(string script) { AssertForbidden("call Planning Tools", script); }

        [Fact]
        public void APlanningToolCallPassesOnlyTheBlockTestsCheck()
        {
            var script = "local p is addons:claudia:planningtool(\"predict_burn\", lexicon(\"burns\", list())).\nprint p.";
            Assert.True(new KosScriptCheck(forToolTests: true).Check(script).Passed);
            Assert.False(new KosScriptCheck().Check(script).Passed);
            // The block tests' check still refuses everything else on the list.
            var quicksave = new KosScriptCheck(forToolTests: true).Check("kuniverse:quicksave().");
            Assert.Contains(quicksave.Forbidden, f => f.EndsWith("(quicksave)"));
        }

        [Fact]
        public void EveryToolScenarioScriptBundledWithTheLibraryPassesTheBlockTestsCheckAndNoMissionCheck()
        {
            var tools = Directory.GetDirectories(Path.Combine(RepoRoot(), "tests", "tools"));
            Assert.NotEmpty(tools);
            foreach (var dir in tools)
            {
                var script = CoreLibrary.Bundle(File.ReadAllText(Path.Combine(dir, "test.ks")));
                var asTest = new KosScriptCheck(forToolTests: true).Check(script);
                Assert.True(asTest.Passed, dir + ": " + string.Join("; ", asTest.Problems.ToArray()));
                Assert.Contains(_check.Check(script).Forbidden, f => f.EndsWith("(call Planning Tools)"));
            }
        }

        // ---- Block-internal calls (ADR 0006, ADR 0007): the Core Library's, never a Mission Script's ----

        [Theory]
        [InlineData("local s is addons:claudia:landingsite(lexicon(\"biome\", \"Midlands\")).")]
        [InlineData("local c is addons:claudia.\nprint c:landingsite(lexicon()).")]
        [InlineData("local f is addons:claudia:LandingSite@.\nprint f(lexicon()).")]
        [InlineData("local b is addons:claudia:deorbitburn(lexicon(\"lat\", 0, \"lng\", -74)).")]
        [InlineData("print addons:claudia:reentryimpact(lexicon()):lat.")]
        [InlineData("local c is addons:claudia.\nprint c:ReentryImpact(lexicon()).")]
        [InlineData("print addons:claudia:turntime(lexicon()):turn_time.")]
        [InlineData("local t is addons:claudia:TurnTime@.\nprint t(lexicon(\"max_stopping_time\", 2)).")]
        [InlineData("local e is addons:claudia:ejectionburn(lexicon(\"target\", \"Mun\", \"target_pe\", 30000)).\nadd node(e:ut, e:radial, e:normal, e:prograde).")]
        [InlineData("local c is addons:claudia.\nprint c:EjectionBurn(lexicon(\"target\", \"Minmus\", \"target_pe\", 20000, \"window\", \"next\")):dv.")]
        public void ABlockInternalCallInAMissionScriptFails(string script)
        {
            AssertForbidden("call block-internal calculations", script);
            AssertForbidden("call block-internal calculations", CoreLibrary.Bundle(script));
        }

        // #335: the warp trace's wait marks are common calls, like BLOCKSTART: a report to the mod's trace, not a calculation
        // (ADR 0006), so a Custom Script's own claudia_warp_to, or the script itself, may make them.
        [Fact]
        public void AMissionScriptMayMarkAWaitForTheWarpTrace()
        {
            AssertAllowed("addons:claudia:waitstart(time:seconds + 600).\naddons:claudia:waitend().");
            AssertAllowed(CoreLibrary.Bundle("addons:claudia:waitstart(time:seconds + 600).\naddons:claudia:waitend()."));
        }

        // #365: a block's trace line is a report to the mod's log too, so land, or a Custom Script, may write one.
        [Fact]
        public void AMissionScriptMayTraceABlocksStateForTheLog()
        {
            AssertAllowed("addons:claudia:blocktrace(\"land\", lexicon(\"phase\", \"fall\", \"h\", 3000)).");
            Assert.Contains("addons:claudia:blocktrace(", CoreLibrary.Bundle("").ToLowerInvariant());
        }

        // #398: an RCS switch is a report to the mod's log, like a trace, so the helper calls and a Custom Script may write one.
        [Fact]
        public void AMissionScriptMayReportAnRcsSwitch()
        {
            AssertAllowed("addons:claudia:rcsswitch(lexicon(\"on\", true, \"reason\", \"translating\")).");
        }

        // #402 (RCS Use): SAS stays off while a Mission Script runs. The library's own abort (claudia_make_safe) turns it on.
        [Theory]
        [InlineData("sas on.")]
        [InlineData("set sas to true.")]
        [InlineData("local x is true.\nset sas to x.")]
        [InlineData("set sas to not sas.")]
        [InlineData("if ship:altitude > 10 { sas on. }")]
        public void SwitchingSasOnFails(string script)
        {
            AssertForbidden("SAS must stay off while a Mission Script runs: it fights kOS steering and fires RCS through holds", script);
            AssertForbidden("SAS must stay off while a Mission Script runs: it fights kOS steering and fires RCS through holds", CoreLibrary.Bundle(script));
        }

        [Theory]
        [InlineData("sas off.")]
        [InlineData("set sas to false.")]
        [InlineData("rcs on.")]
        [InlineData("rcs off.")]
        [InlineData("set rcs to true.\nprint sas.")]
        public void SwitchingSasOffAndRcsOnOrOffPass(string script)
        {
            AssertAllowed(script);
            AssertAllowed(CoreLibrary.Bundle(script));
        }

        [Fact]
        public void TheCoreLibrarysOwnSasOnPasses()
        {
            Assert.Contains("sas on.", CoreLibrary.Bundle("").ToLowerInvariant());
            AssertAllowed(CoreLibrary.Bundle("claudia_make_safe(lexicon())."));
        }

        [Fact]
        public void TheCoreLibrarysOwnBlockInternalCallPasses()
        {
            // land calls it for a biome target; the bundle's library lines are the Core Library's.
            Assert.Contains("addons:claudia:landingsite(", CoreLibrary.Bundle("").ToLowerInvariant());
            AssertAllowed(CoreLibrary.Bundle("local res is land(lexicon(\"target\", lexicon(\"biome\", \"Midlands\"))).\naddons:claudia:report(res)."));
        }

        [Fact]
        public void DeorbitsOwnReentryPredictorCallsPass()
        {
            var bundle = CoreLibrary.Bundle("").ToLowerInvariant();
            Assert.Contains("addons:claudia:deorbitburn(", bundle);
            Assert.Contains("addons:claudia:reentryimpact(", bundle);
            AssertAllowed(CoreLibrary.Bundle("local res is deorbit(lexicon(\"target\", \"ksc\")).\nif res:burn_ut > 0 { set res to reentry(). }\naddons:claudia:report(res)."));
        }

        [Fact]
        public void ExecNodesOwnTurnTimeCallPasses()
        {
            Assert.Contains("addons:claudia:turntime(", CoreLibrary.Bundle("").ToLowerInvariant());
            AssertAllowed(CoreLibrary.Bundle("add node(time:seconds + 600, 0, 0, 120).\nlocal res is exec_node().\naddons:claudia:report(res)."));
        }

        [Fact]
        public void ABlockInternalCallFailsOnTheMissionScriptsOwnLineInABundle()
        {
            var result = _check.Check(CoreLibrary.Bundle("print 1.\nprint addons:claudia:landingsite(lexicon())."));
            Assert.Contains("line 2: :LANDINGSITE (call block-internal calculations)", result.Forbidden);
        }

        [Fact]
        public void AMissionScriptCopyingTheLibrarysBannerIsStillAMissionScript()
        {
            // Only the bundle's real tail is the Core Library; a copy of its banner line in the script changes nothing.
            var script = "// ---- The Core Library, bundled by Claudia Kermin ----\nprint addons:claudia:landingsite(lexicon()).";
            AssertForbidden("call block-internal calculations", CoreLibrary.Bundle(script));
        }

        [Theory]
        [InlineData(0x0D)]
        [InlineData(0x2028)]
        [InlineData(0x2029)]
        [InlineData(0x85)]
        [InlineData(0x0B)]
        [InlineData(0x0C)]
        public void OtherLineBreaksCannotMoveAMissionScriptCallOntoTheLibrarysLines(int lineBreak)
        {
            // Enough to pass the library's first line, were any of them counted as a line by kOS and not by the check.
            var padding = new string((char)lineBreak, 50);
            var result = _check.Check(CoreLibrary.Bundle(padding + "print addons:claudia:landingsite(lexicon())."));
            Assert.False(result.Passed);
        }

        [Fact]
        public void AMissionScriptCannotSwapItsOwnCodeIntoABlockWithALock()
        {
            // A lock is stored when its line runs, over the library's function of that name: a lock named land_plan would get land's
            // site from the block-internal search and could keep it without the block flying it.
            var result = _check.Check(CoreLibrary.Bundle("print 1.\nlock land_plan to lexicon(\"error\", \"x\").\nprint land(lexicon(\"target\", lexicon(\"biome\", \"Midlands\")))."));
            Assert.Contains("line 2: LAND_PLAN* (redefine a Core Library function)", result.Forbidden);
        }

        [Fact]
        public void AMissionScriptCannotDefineAFunctionTheLibraryDefines()
        {
            // The Mission Script's function wins over the library's (LandAscendTests shows it on kOS), so land_plan could keep the site.
            var result = _check.Check(CoreLibrary.Bundle("print 1.\n\nfunction land_plan {\n  parameter a, b, c.\n  return lexicon(\"error\", \"x\").\n}"));
            Assert.Contains(result.Forbidden, f => f.EndsWith("FUNCTION LAND_PLAN (redefine a Core Library function)"));
            Assert.All(result.Forbidden, f => Assert.True(int.Parse(f.Split(' ', ':')[1]) <= 6, f));
            AssertForbidden("redefine a Core Library function", CoreLibrary.Bundle("global function claudia_finish { parameter a, b, c, d. return a. }"));
        }

        [Fact]
        public void ALockInsideAMissionScriptFunctionIsCaughtToo()
        {
            AssertForbidden("redefine a Core Library function", CoreLibrary.Bundle("function f {\n  lock land_site_query to lexicon().\n}\nf()."));
        }

        [Fact]
        public void AMissionScriptMayLockSteeringAndThrottleAndNameItsOwnFunctions()
        {
            AssertAllowed(CoreLibrary.Bundle("function my_helper { return 1. }\nlock steering to prograde.\nlock throttle to 0.\nprint my_helper().\nunlock steering.\nunlock throttle."));
        }

        [Fact]
        public void LandsFileAlonePassesOnlyAsCoreLibrary()
        {
            // CI checks each kerboscript/library file alone: as the library's, not as a Mission Script.
            var land = File.ReadAllText(Path.Combine(RepoRoot(), "kerboscript", "library", "land.ks"));
            Assert.True(new KosScriptCheck(forCoreLibrary: true).Check(land).Passed);
            Assert.Contains(_check.Check(land).Forbidden, f => f.EndsWith("(call block-internal calculations)"));
            // The library's check refuses everything else on the list.
            Assert.Contains(new KosScriptCheck(forCoreLibrary: true).Check("kuniverse:quicksave().").Forbidden, f => f.EndsWith("(quicksave)"));
        }

        [Theory]
        [InlineData("land_biome_mun")]
        [InlineData("land_biome_minmus")]
        public void TheLandBiomeBlockTestsBundledWithTheLibraryPass(string test)
        {
            var script = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "blocks", test, "test.ks"));
            AssertAllowed(script);
            AssertAllowed(CoreLibrary.Bundle(script));
        }

        [Fact]
        public void TheBlockTestsCheckStillRefusesABlockInternalCallInTheScript()
        {
            var result = new KosScriptCheck(forToolTests: true).Check("print addons:claudia:landingsite(lexicon()).");
            Assert.Contains(result.Forbidden, f => f.EndsWith("(call block-internal calculations)"));
        }

        [Fact]
        public void ReadingKosConfig() { AssertAllowed("print config:ipu."); }

        [Fact]
        public void EnginesCanBeActivatedAndShutDown()
        {
            AssertAllowed("list engines in engs.\nfor e in engs { e:activate(). }\nengs[0]:shutdown().");
        }

        // ---- A bare return(...) statement ----

        [Theory]
        [InlineData("return(lexicon(\"stage_limit\", 2)).")]
        [InlineData("return (lexicon(\"stage_limit\", 2)).")]
        [InlineData("if ship:altitude > 1 { return(lexicon()). }")]
        public void ABareReturnStatementFailsTellingTheScriptToUseTheValue(string script)
        {
            var result = _check.Check(script);
            Assert.False(result.Passed, "passed: " + script);
            Assert.NotNull(result.CompileError);
            Assert.Contains("line:1", result.CompileError);
            Assert.Contains("local res is return(...)", result.CompileError);
        }

        [Fact]
        public void TheReturnBuildingBlockCalledAsAValuePasses()
        {
            AssertAllowed("function return { parameter o. return lexicon(\"success\", true). }\n"
                + "local res is return(lexicon(\"stage_limit\", 2)).\nprint res:success.\n"
                + "print return(lexicon()):success.\naddons:claudia:report(return(lexicon())).");
        }

        [Fact]
        public void ReturnStatementsInsideFunctionsPass()
        {
            AssertAllowed("function f { parameter x. if x > 1 { return (x - 1). } return x. }\nprint f(3).");
        }

        // ---- Compiling ----

        [Fact]
        public void AScriptThatDoesNotCompileFailsWithTheCompilerMessage()
        {
            var result = _check.Check("function broken {\n  local x is 1\n  print x.\n}");
            Assert.False(result.Passed);
            Assert.NotNull(result.CompileError);
        }

        [Fact]
        public void FindingsNameTheLineAndWhatWasCalled()
        {
            var result = _check.Check("print \"ok\".\ncopypath(\"0:/a\", \"1:/a\").");
            Assert.Equal(new[] { "line 2: COPYPATH (touch files)" }, result.Forbidden);
        }

        [Fact]
        public void ForbiddenCallsInsideFunctionsAndTriggersAreFound()
        {
            var result = _check.Check("function f { kuniverse:quicksave(). }\nwhen altitude > 1000 then { reboot. }\nf().");
            Assert.Contains(result.Forbidden, f => f.EndsWith("(quicksave)"));
            Assert.Contains(result.Forbidden, f => f.EndsWith("(reboot or shut down processors)"));
        }

        [Theory]
        [InlineData("change kOS config", "set config:ipu to choose 2000 if ship:altitude > 1 else 500.")]
        public void ChoosingBetweenValuesStillChecksEachOne(string item, string script) { AssertForbidden(item, script); }

        [Fact]
        public void ChoosingBetweenAllowedValuesPasses()
        {
            AssertAllowed("set warpmode to choose \"RAILS\" if ship:altitude < 70000 else \"RAILS\".\n"
                + "set warp to choose 3 if ship:altitude > 100000 else 1.");
        }

        [Fact]
        public void AnotherVesselPassedToAFunctionIsStillAnotherVessel()
        {
            AssertForbidden("change or message other vessels",
                "function poke { parameter other. other:parts[0]:getmodule(\"ModuleDecoupler\"):doevent(\"decouple\"). }\npoke(vessel(\"Other\")).");
        }

        [Fact]
        public void OwnPartsPassedToAFunctionStayAllowed()
        {
            AssertAllowed("function deploy { parameter p. p:getmodule(\"ModuleParachute\"):doevent(\"deploy chute\"). }\n"
                + "deploy(ship:partstagged(\"chute\")[0]).\nprint vessel(\"Other\"):altitude.");
        }

        [Fact]
        public void TheShippedDebugScriptsPass()
        {
            var dir = Path.Combine(RepoRoot(), "kerboscript", "debug");
            foreach (var file in Directory.GetFiles(dir, "*.ks"))
            {
                var result = _check.Check(File.ReadAllText(file));
                Assert.True(result.Passed, file + ": " + string.Join("; ", result.Problems.ToArray()));
            }
        }

        // ---- The Core Library (ADR 0003: it must pass the same check), as the mod bundles it ----

        [Fact]
        public void TheCoreLibraryBundledWithAMissionScriptPasses()
        {
            // The block test's Mission Script: it stages, warps on rails and flies a maneuver node through exec_node.
            var script = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "blocks", "exec_node", "test.ks"));
            AssertAllowed(CoreLibrary.Bundle(script));
        }

        [Theory]
        [InlineData("transfer_to")]
        [InlineData("return")]
        public void EachBlockTestScriptBundledWithTheLibraryPasses(string test)
        {
            // transfer_to calls the ejection solver inline; return calls the block kOS lets be named return.
            var script = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "blocks", test, "test.ks"));
            AssertAllowed(CoreLibrary.Bundle(script));
        }

        [Fact]
        public void AForbiddenCallInABundledScriptFailsOnTheScriptsOwnLine()
        {
            var result = _check.Check(CoreLibrary.Bundle("print \"hello\".\nkuniverse:quicksave()."));
            Assert.False(result.Passed);
            Assert.Contains("line 2: :QUICKSAVE (quicksave)", result.Forbidden);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ClaudiaKermin.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}
