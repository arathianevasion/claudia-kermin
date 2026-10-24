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
        public void UsePhysicsWarp(string script) { AssertForbidden("use physics warp", script); }

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

        [Fact]
        public void ReadingKosConfig() { AssertAllowed("print config:ipu."); }

        [Fact]
        public void EnginesCanBeActivatedAndShutDown()
        {
            AssertAllowed("list engines in engs.\nfor e in engs { e:activate(). }\nengs[0]:shutdown().");
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

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ClaudiaKermin.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}
