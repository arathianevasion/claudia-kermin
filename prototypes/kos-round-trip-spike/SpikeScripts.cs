// PROTOTYPE: throwaway kOS round-trip spike. Loads the test scripts shipped next to the DLL.
using System.IO;

namespace ClaudiaSpike
{
    public static class SpikeScripts
    {
        public static readonly string[] Names = { "ok", "runtime_error", "syntax_error", "no_report", "long_run", "warp_soi", "rsvp_mun" };

        private static string Dir { get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/ClaudiaSpike/scripts"); } }

        public static string Get(string name) { return File.ReadAllText(Path.Combine(Dir, name + ".ks")); }

        public static string BootListener { get { return File.ReadAllText(Path.Combine(Dir, "claudia_boot.ks")); } }
    }
}
