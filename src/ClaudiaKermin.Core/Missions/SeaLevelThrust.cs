// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Thrust at a body's sea-level pressure, worked out from the engines' own Isp curves instead of the stock delta-v
    /// app's thrustASL, which is for whichever body that app has selected.
    /// </summary>
    public static class SeaLevelThrust
    {
        /// <summary>An engine's thrust (kN) at <paramref name="atm"/> atmospheres; 0 atm is its vacuum thrust.</summary>
        public static double Of(SnapshotPart engine, double atm)
        {
            if (engine.ThrustVacKn <= 0 || engine.IspVac <= 0) return 0;
            if (atm <= 0) return engine.ThrustVacKn;
            return engine.ThrustVacKn * IspAt(engine, atm) / engine.IspVac;
        }

        public static double Of(IEnumerable<SnapshotPart> engines, double atm)
        {
            return engines.Sum(e => Of(e, atm));
        }

        /// <summary>
        /// A stage's thrust (kN) at <paramref name="atm"/>: the simulator's vacuum thrust (it knows which engines burn in the stage)
        /// scaled by the sea-level fraction of the engines in that stage and the ones before it.
        /// </summary>
        public static double ForStage(GameSnapshot snapshot, SnapshotStage stage, double atm)
        {
            if (atm <= 0) return stage.ThrustVacKn;
            var engines = snapshot.Craft.Parts.Where(p => p.ThrustVacKn > 0 && p.Stage >= stage.Number).ToList();
            var vacuum = engines.Sum(e => e.ThrustVacKn);
            return vacuum > 0 ? stage.ThrustVacKn * Of(engines, atm) / vacuum : stage.ThrustVacKn;
        }

        /// <summary>The Isp at the pressure: the curve's keys joined by straight lines, held flat beyond the last key.</summary>
        private static double IspAt(SnapshotPart engine, double atm)
        {
            var keys = engine.IspCurve;
            if (keys.Count == 0)
            {
                // No curve read: a straight line from vacuum to the engine's 1 atm figure.
                var asl = engine.ThrustAslKn > 0 ? engine.ThrustAslKn / engine.ThrustVacKn * engine.IspVac : engine.IspVac;
                return engine.IspVac + (asl - engine.IspVac) * atm;
            }
            var sorted = keys.OrderBy(k => k[0]).ToList();
            if (atm <= sorted[0][0]) return sorted[0][1];
            for (var i = 1; i < sorted.Count; i++)
            {
                if (atm > sorted[i][0]) continue;
                var span = sorted[i][0] - sorted[i - 1][0];
                return sorted[i - 1][1] + (sorted[i][1] - sorted[i - 1][1]) * (atm - sorted[i - 1][0]) / span;
            }
            return sorted[sorted.Count - 1][1];
        }
    }
}
