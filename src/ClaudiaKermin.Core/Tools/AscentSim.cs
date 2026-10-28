// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// The air for the ascent sim: the body table's sampled air in the tool, the kOS sim's exponential air in the cross-check
    /// against <c>launch_ascent_sim.ks</c>.
    /// </summary>
    internal interface IAscentAir
    {
        /// <summary>Pressure (atmospheres), density (kg/m³) and speed of sound (m/s) at <paramref name="altitude"/> m above sea level; 0 pressure and density above the air.</summary>
        void Air(double altitude, out double atm, out double density, out double speedOfSound);
    }

    /// <summary>
    /// A body's air from its rows in the Game Snapshot (altitude, atmospheres, kg/m³, m/s), pressure and density joined
    /// exponentially between rows; or, for a Snapshot recorded without them, an estimate from the atmosphere's height and
    /// sea-level pressure: Kerbin's shape (pressure falling e-fold every twelfth-and-a-half of the depth, 1.225 kg/m³ an
    /// atmosphere, sound at 340 m/s).
    /// </summary>
    internal sealed class BodyAir : IAscentAir
    {
        private readonly SnapshotBody _body;
        private readonly List<double[]> _rows;

        public BodyAir(SnapshotBody body)
        {
            _body = body;
            _rows = body.Air.Where(r => r.Length >= 4).OrderBy(r => r[0]).ToList();
        }

        /// <summary>True when the air is estimated, not read from the body's rows.</summary>
        public bool Estimated
        {
            get { return _rows.Count < 2; }
        }

        public void Air(double altitude, out double atm, out double density, out double speedOfSound)
        {
            atm = 0;
            density = 0;
            speedOfSound = 340;
            if (altitude >= _body.AtmosphereHeight || _body.SeaLevelAtm <= 0) return;
            altitude = Math.Max(0, altitude);
            if (Estimated)
            {
                var ratio = Math.Exp(-altitude * 12.5 / _body.AtmosphereHeight);
                atm = _body.SeaLevelAtm * ratio;
                density = 1.225 * atm;
                return;
            }
            var i = 1;
            while (i < _rows.Count - 1 && _rows[i][0] < altitude) i++;
            var a = _rows[i - 1];
            var b = _rows[i];
            var f = b[0] > a[0] ? Math.Max(0, Math.Min(1, (altitude - a[0]) / (b[0] - a[0]))) : 0;
            atm = Between(a[1], b[1], f);
            density = Between(a[2], b[2], f);
            speedOfSound = a[3] + (b[3] - a[3]) * f;
        }

        /// <summary>Exponential between two positive values, straight to or from 0.</summary>
        private static double Between(double a, double b, double f)
        {
            if (a > 0 && b > 0) return a * Math.Pow(b / a, f);
            return a + (b - a) * f;
        }
    }

    /// <summary>One stage the ascent may burn, from <see cref="StagePerformance"/>'s rows: SI units.</summary>
    internal sealed class AscentStage
    {
        public int Number;
        /// <summary>Thrust in vacuum and at the body's sea-level pressure, N; between them it goes with the pressure.</summary>
        public double ThrustVac;
        public double ThrustSeaLevel;
        /// <summary>Propellant flow at full throttle, kg/s (fixed: thrust and Isp fall together with pressure).</summary>
        public double Flow;
        public double StartMass;
        public double EndMass;
        /// <summary>Vacuum Isp, s.</summary>
        public double IspVac;
        /// <summary>Vacuum delta-v of the whole stage, m/s.</summary>
        public double DeltaVVac;
        /// <summary>Full-throttle burn time of the whole stage at sea level, s (as <c>stage_performance</c> gives it).</summary>
        public double BurnTime;

        public bool Burns
        {
            get { return ThrustVac > 0 && Flow > 0 && StartMass > EndMass && EndMass > 0; }
        }

        /// <summary>Thrust (N) at <paramref name="atm"/> atmospheres on a body whose sea level is <paramref name="seaLevelAtm"/>.</summary>
        public double Thrust(double atm, double seaLevelAtm)
        {
            if (seaLevelAtm <= 0 || atm <= 0) return ThrustVac;
            return ThrustVac + (ThrustSeaLevel - ThrustVac) * atm / seaLevelAtm;
        }

        /// <summary>The stages of the craft in the Snapshot, in firing order (highest number first), at <paramref name="body"/>'s sea level.</summary>
        public static List<AscentStage> From(GameSnapshot snapshot, SnapshotBody body)
        {
            var vacuum = StagePerformance.Rows(snapshot, 0, body.SurfaceGravity);
            var sea = StagePerformance.Rows(snapshot, body.SeaLevelAtm, body.SurfaceGravity);
            var stages = new List<AscentStage>();
            for (var i = 0; i < vacuum.Count; i++)
            {
                var v = vacuum[i];
                stages.Add(new AscentStage
                {
                    Number = v.Stage,
                    ThrustVac = v.Thrust,
                    ThrustSeaLevel = sea[i].Thrust,
                    Flow = v.Isp > 0 ? v.Thrust / (v.Isp * AscentSim.G0) : 0,
                    StartMass = v.StartMass,
                    EndMass = v.EndMass,
                    IspVac = v.Isp,
                    DeltaVVac = v.DeltaV,
                    BurnTime = sea[i].BurnTime,
                });
            }
            return stages.OrderByDescending(s => s.Number).ToList();
        }
    }

    /// <summary>The body and the launch's options for one ascent.</summary>
    internal sealed class AscentOptions
    {
        public double Gm;
        public double Radius;
        public double AtmosphereHeight;
        public double SeaLevelAtm;
        /// <summary>The surface's speed along the climb from the body's turning, m/s (its equatorial speed times cos inclination).</summary>
        public double Spin;
        /// <summary>The altitude it lifts off from, m.</summary>
        public double StartAltitude;

        public double TargetAp;
        public double TurnStartSpeed = 60;
        public double TurnShape = 0.45;
        public double MaxAoa = 5;
        /// <summary>throttle_max_q, Pa; -1 for none.</summary>
        public double MaxQ = -1;
        public int StageLimit;
        /// <summary>The integration step, s.</summary>
        public double Step = 0.1;
        /// <summary>Records the pitch each 1000 m of climb (the cross-check's track).</summary>
        public bool Track;
    }

    /// <summary>The craft when a stage lit.</summary>
    internal sealed class StageLighting
    {
        public int Stage;
        /// <summary>Its periapsis was under the atmosphere top: a Boost stage (GLOSSARY.md); otherwise an Orbital stage.</summary>
        public bool Suborbital;
        public double Altitude;
        /// <summary>Surface speed, m/s.</summary>
        public double Speed;
        public double Apoapsis;
        public double Periapsis;
        /// <summary>s; NaN when the path does not come back down (escape).</summary>
        public double TimeToAp;
        /// <summary>Thrust-to-weight at the local pressure and gravity.</summary>
        public double Twr;
    }

    /// <summary>One ascent flown by <see cref="AscentSim"/>.</summary>
    internal sealed class AscentFlight
    {
        public bool ReachesOrbit;
        /// <summary>Why not; "" when it does.</summary>
        public string Failure = "";
        /// <summary>The stage burning (or last burnt) where it failed; -1 when it reached orbit.</summary>
        public int FailedStage = -1;
        public double FailedAltitude;
        public List<StageLighting> Lit = new List<StageLighting>();
        /// <summary>Apoapsis at the first engine cutoff (launch's coast_ap), m; 0 when it never cut off.</summary>
        public double CoastAp;
        /// <summary>The highest dynamic pressure, Pa.</summary>
        public double MaxQ;
        /// <summary>The highest apoapsis reached, m: ranks the shapes that fail.</summary>
        public double BestAp;
        /// <summary>Delta-v spent from the pad to a circular orbit, m/s.</summary>
        public double DeltaVToOrbit;
        /// <summary>Vacuum delta-v left at orbit in the stages the launch may burn (stage_limit and above), m/s.</summary>
        public double DeltaVLeft;
        /// <summary>Vacuum delta-v of the stages stage_limit keeps (below it), m/s.</summary>
        public double DeltaVKept;
        /// <summary>The stage burning when launch's block ends (out of the air, coasting to apoapsis).</summary>
        public int LaunchEndStage = -1;
        /// <summary>The lowest vertical speed while a Boost stage burns below the atmosphere top after the turn starts, m/s.</summary>
        public double MinVerticalSpeed = double.PositiveInfinity;
        /// <summary>
        /// (altitude, pitch and prograde from vertical in degrees, dynamic pressure Pa, throttle) at each 1000 m of the climb
        /// while burning, when asked for.
        /// </summary>
        public List<double[]> Track = new List<double[]>();
    }

    /// <summary>
    /// The ascent sim behind <c>plan_ascent</c> (#367): a point mass in the plane of the climb, round a turning body, flown by
    /// <c>launch.ks</c>'s own rules in the order its loop uses them (<c>launch_pitch</c>, the turn hold, the throttle and its
    /// thick-air floor, <c>max_aoa</c>, the thin-air prograde rule), staging as <c>claudia_autostage</c> does (on flameout,
    /// never below stage_limit), out of the air to the coast, then the circularization burn at apoapsis (as <c>circularize</c>
    /// and <c>exec_node</c> fly it: half before apoapsis, along the horizon there). Each tick mirrors a kOS tick. The nose
    /// is where the steering asks; one drag table serves the whole flight (stages dropping don't change it).
    /// </summary>
    internal sealed class AscentSim
    {
        public const double G0 = 9.80665;
        /// <summary>launch's max_duration on the pad: a launch still climbing after this has failed.</summary>
        private const double MaxLaunchSeconds = 900;
        /// <summary>The coast and circularization after launch ends.</summary>
        private const double MaxOrbitSeconds = 3600;

        private readonly AscentOptions _o;
        private readonly IAscentAir _air;
        private readonly Func<double, double> _dragArea;
        private readonly List<AscentStage> _stages;
        private readonly AscentFlight _flight = new AscentFlight();

        // State: position and inertial velocity in the plane, the body's centre at the origin; the climb runs anticlockwise.
        private double _px, _py, _vx, _vy;
        private double _mass;
        private double _t;
        private int _stage = -1;

        private AscentSim(AscentOptions options, IAscentAir air, Func<double, double> dragArea, List<AscentStage> stages)
        {
            _o = options;
            _air = air;
            _dragArea = dragArea;
            _stages = stages;
        }

        /// <summary>Flies one ascent. <paramref name="dragArea"/> is m² nose first at a Mach number; <paramref name="stages"/> in firing order.</summary>
        public static AscentFlight Fly(AscentOptions options, IAscentAir air, Func<double, double> dragArea, List<AscentStage> stages)
        {
            return new AscentSim(options, air, dragArea, stages).Run();
        }

        /// <summary>The drag area at a Mach number from (Mach, m²) keys, joined by straight lines and held flat past the ends.</summary>
        public static Func<double, double> Table(List<double[]> keys)
        {
            var sorted = keys.OrderBy(k => k[0]).ToList();
            return mach =>
            {
                if (sorted.Count == 0) return 0;
                if (mach <= sorted[0][0]) return sorted[0][1];
                for (var i = 1; i < sorted.Count; i++)
                {
                    if (mach > sorted[i][0]) continue;
                    var span = sorted[i][0] - sorted[i - 1][0];
                    return span > 0 ? sorted[i - 1][1] + (sorted[i][1] - sorted[i - 1][1]) * (mach - sorted[i - 1][0]) / span : sorted[i][1];
                }
                return sorted[sorted.Count - 1][1];
            };
        }

        // ---- launch.ks's rules, word for word ----

        private static double LaunchPitch(double h, double startAlt, double endAlt, double shape)
        {
            if (h <= startAlt || endAlt <= startAlt) return 90;
            var fraction = Math.Min(1, (h - startAlt) / (endAlt - startAlt));
            return 90 * (1 - Math.Pow(fraction, shape));
        }

        private static bool ThickAir(double ratio)
        {
            return ratio > 0.1;
        }

        private static bool TurnHeld(double q, double maxQ, double ratio)
        {
            return maxQ > 0 && q > 0.9 * maxQ && ThickAir(ratio);
        }

        private static double TopupTol(double targetAp)
        {
            return Math.Max(100, 0.005 * targetAp);
        }

        private static double Throttle(double ap, double targetAp, double ramp, double q, double maxQ, double floor)
        {
            if (ap >= targetAp - TopupTol(targetAp) / 2) return 0;
            var t = Math.Min(1, (targetAp - ap) / ramp);
            if (maxQ > 0) t = Math.Min(t, Math.Max(0.05, 1 - (q - 0.9 * maxQ) / (0.2 * maxQ)));
            return Math.Min(1, Math.Max(floor, Math.Max(0, t)));
        }

        /// <summary>launch_throttle_floor, with mass in kg and thrust in N.</summary>
        private static double ThrottleFloor(double ratio, double mass, double g, double thrust)
        {
            if (!ThickAir(ratio) || thrust <= 0) return 0;
            return Math.Min(1, mass * g / thrust);
        }

        private const double AeroQ = 500;
        private const double AoaQRef = 8000;
        private const double ThinLead = 3;

        private static double AllowedAoa(double maxAoa, double q)
        {
            if (q <= AoaQRef) return maxAoa;
            return Math.Max(maxAoa * 0.1, maxAoa * AoaQRef / q);
        }

        // ---- the flight ----

        private double R
        {
            get { return Math.Sqrt(_px * _px + _py * _py); }
        }

        private double Altitude
        {
            get { return R - _o.Radius; }
        }

        /// <summary>Up and the horizontal along the climb, unit vectors.</summary>
        private void Frame(out double ux, out double uy, out double ex, out double ey)
        {
            var r = R;
            ux = _px / r;
            uy = _py / r;
            ex = -uy;
            ey = ux;
        }

        /// <summary>The velocity against the turning surface, horizontal and vertical, m/s.</summary>
        private void Surface(out double horizontal, out double vertical)
        {
            double ux, uy, ex, ey;
            Frame(out ux, out uy, out ex, out ey);
            var spin = _o.Spin * R / _o.Radius;
            horizontal = _vx * ex + _vy * ey - spin;
            vertical = _vx * ux + _vy * uy;
        }

        /// <summary>Apoapsis and periapsis above sea level, m (apoapsis infinite on an escape path), and seconds to apoapsis (NaN on an escape path).</summary>
        private void Orbit(out double ap, out double pe, out double toAp)
        {
            var r = R;
            var v2 = _vx * _vx + _vy * _vy;
            var mu = _o.Gm;
            var energy = v2 / 2 - mu / r;
            var h = _px * _vy - _py * _vx;
            var e = Math.Sqrt(Math.Max(0, 1 + 2 * energy * h * h / (mu * mu)));
            if (energy >= 0)
            {
                ap = double.PositiveInfinity;
                pe = h * h / (mu * (1 + e)) - _o.Radius;
                toAp = double.NaN;
                return;
            }
            var a = -mu / (2 * energy);
            ap = a * (1 + e) - _o.Radius;
            pe = a * (1 - e) - _o.Radius;
            if (e < 1e-9)
            {
                toAp = 0;
                return;
            }
            var radial = (_px * _vx + _py * _vy) / r;
            var cosNu = Math.Max(-1, Math.Min(1, (a * (1 - e * e) / r - 1) / e));
            var nu = Math.Acos(cosNu);
            if (radial < 0) nu = 2 * Math.PI - nu;
            var ecc = 2 * Math.Atan(Math.Sqrt((1 - e) / (1 + e)) * Math.Tan(nu / 2));
            var mean = ecc - e * Math.Sin(ecc);
            if (mean < 0) mean += 2 * Math.PI;
            var n = Math.Sqrt(mu / (a * a * a));
            var left = Math.PI - mean;
            if (left < 0) left += 2 * Math.PI;
            toAp = left / n;
        }

        private AscentStage Current
        {
            get { return _stage >= 0 && _stage < _stages.Count ? _stages[_stage] : null; }
        }

        /// <summary>
        /// Stages into the next stage that burns, as claudia_autostage does: never below stage_limit, through stages with no
        /// engines. False when there is none (out of fuel).
        /// </summary>
        private bool StageOn()
        {
            for (var i = _stage + 1; i < _stages.Count; i++)
            {
                var stage = _stages[i];
                if (stage.Number < _o.StageLimit) return false;
                if (!stage.Burns) continue;
                _stage = i;
                _mass = stage.StartMass;
                Light(stage);
                return true;
            }
            return false;
        }

        private void Light(AscentStage stage)
        {
            double ap, pe, toAp, horizontal, vertical, atm, density, sound;
            Orbit(out ap, out pe, out toAp);
            Surface(out horizontal, out vertical);
            _air.Air(Altitude, out atm, out density, out sound);
            var g = _o.Gm / (R * R);
            var top = _o.AtmosphereHeight > 0 ? _o.AtmosphereHeight : 0;
            _flight.Lit.Add(new StageLighting
            {
                Stage = stage.Number,
                Suborbital = pe < top,
                Altitude = Altitude,
                Speed = Math.Sqrt(horizontal * horizontal + vertical * vertical),
                Apoapsis = ap,
                Periapsis = pe,
                TimeToAp = toAp,
                Twr = stage.Thrust(atm, _o.SeaLevelAtm) / (_mass * g),
            });
        }

        /// <summary>One step: thrust at <paramref name="pitch"/> degrees above the surface horizontal and the throttle, drag, gravity.</summary>
        private void Step(double throttle, double pitch, double thrust, double density, double sound, double dt)
        {
            double ux, uy, ex, ey, horizontal, vertical;
            Frame(out ux, out uy, out ex, out ey);
            Surface(out horizontal, out vertical);
            var r = R;
            var ax = -_o.Gm * _px / (r * r * r);
            var ay = -_o.Gm * _py / (r * r * r);
            var force = thrust * throttle;
            if (force > 0)
            {
                var c = Math.Cos(pitch * Math.PI / 180);
                var s = Math.Sin(pitch * Math.PI / 180);
                ax += force / _mass * (c * ex + s * ux);
                ay += force / _mass * (c * ey + s * uy);
                _flight.DeltaVToOrbit += force / _mass * dt;
            }
            var spd = Math.Sqrt(horizontal * horizontal + vertical * vertical);
            if (density > 0 && spd > 0)
            {
                var drag = 0.5 * density * spd * spd * _dragArea(sound > 0 ? spd / sound : 0) / _mass;
                ax -= drag * (horizontal * ex + vertical * ux) / spd;
                ay -= drag * (horizontal * ey + vertical * uy) / spd;
            }
            _vx += ax * dt;
            _vy += ay * dt;
            _px += _vx * dt;
            _py += _vy * dt;
            var stage = Current;
            if (stage != null && throttle > 0) _mass -= stage.Flow * throttle * dt;
            _t += dt;
        }

        private AscentFlight Fail(string why)
        {
            _flight.ReachesOrbit = false;
            _flight.Failure = why;
            _flight.FailedStage = Current != null ? Current.Number : -1;
            _flight.FailedAltitude = Altitude;
            return _flight;
        }

        private AscentFlight Run()
        {
            _px = _o.Radius + _o.StartAltitude;
            _py = 0;
            _vx = 0;
            _vy = _o.Spin * R / _o.Radius;
            _flight.DeltaVKept = _stages.Where(s => s.Number < _o.StageLimit && s.Burns).Sum(s => s.DeltaVVac);
            if (!StageOn()) return Fail("no thrust");

            var top = _o.AtmosphereHeight;
            var target = _o.TargetAp;
            var turnEnd = Math.Min(top, target);
            var ramp = Math.Max(500, 0.02 * target);
            var topup = TopupTol(target);
            var g = _o.Gm / (_o.Radius * _o.Radius);
            var turnStartAlt = -1.0;
            var coasting = false;
            var nextTrack = 1000.0;
            var dt = _o.Step;

            while (true)
            {
                if (_t > MaxLaunchSeconds) return Fail("still climbing after " + MaxLaunchSeconds + " s");
                double atm, density, sound, ap, pe, toAp, horizontal, vertical;
                var h = Altitude;
                _air.Air(h, out atm, out density, out sound);
                Surface(out horizontal, out vertical);
                Orbit(out ap, out pe, out toAp);
                var spd = Math.Sqrt(horizontal * horizontal + vertical * vertical);
                var q = 0.5 * density * spd * spd;
                _flight.MaxQ = Math.Max(_flight.MaxQ, q);
                _flight.BestAp = Math.Max(_flight.BestAp, ap);
                var ratio = _o.SeaLevelAtm > 0 ? atm / _o.SeaLevelAtm : 0;
                var stage = Current;
                var thrust = stage != null ? stage.Thrust(atm, _o.SeaLevelAtm) : 0;

                if (coasting)
                {
                    if (h > top)
                    {
                        _flight.LaunchEndStage = stage != null ? stage.Number : -1;
                        return Circularize();
                    }
                    if (vertical < 0) return Fail("fell back into the atmosphere: apoapsis " + Math.Round(ap) + " m, under the atmosphere top");
                    if (ap < target - topup && !ThickAir(ratio))
                    {
                        coasting = false;
                        Step(0, 0, 0, density, sound, dt);
                        continue;
                    }
                }
                double throttle = 0;
                if (!coasting)
                {
                    throttle = Throttle(ap, target, ramp, q, _o.MaxQ, ThrottleFloor(ratio, _mass, g, thrust));
                    if (throttle == 0)
                    {
                        coasting = true;
                        if (_flight.CoastAp == 0) _flight.CoastAp = ap;
                    }
                }

                var prograde = spd > 1 ? Math.Atan2(vertical, horizontal) * 180 / Math.PI : 90;
                if (turnStartAlt < 0 && spd >= _o.TurnStartSpeed) turnStartAlt = h;
                if (turnStartAlt >= 0 && TurnHeld(q, _o.MaxQ, ratio)) turnStartAlt = h;
                var pitch = 90.0;
                if (!coasting)
                {
                    if (turnStartAlt >= 0) pitch = LaunchPitch(h, turnStartAlt, turnEnd, _o.TurnShape);
                    if (spd > 30 && q <= AeroQ) pitch = Math.Max(pitch, prograde - ThinLead);
                }
                else if (spd > 30)
                {
                    pitch = prograde;
                }
                if (q > AeroQ && spd > 30)
                {
                    var allowed = AllowedAoa(_o.MaxAoa, q);
                    pitch = Math.Max(prograde - allowed, Math.Min(prograde + allowed, pitch));
                }

                if (!coasting)
                {
                    if (vertical < 0 && h < top) return Fail("vertical speed fell below 0 while burning, under the atmosphere top");
                    if (turnStartAlt >= 0 && h < top) _flight.MinVerticalSpeed = Math.Min(_flight.MinVerticalSpeed, vertical);
                    if (_o.Track && h >= nextTrack)
                    {
                        _flight.Track.Add(new[] { h, 90 - pitch, 90 - prograde, q, throttle });
                        nextTrack += 1000;
                    }
                }

                Step(throttle, pitch, thrust, density, sound, dt);
                // claudia_tick's autostage: on flameout, the next stage, never below stage_limit.
                if (!coasting && Current.EndMass >= _mass && !StageOn())
                {
                    _mass = Current.EndMass;
                    return Fail("out of fuel" + (_o.StageLimit > 0 ? " at stage limit " + _o.StageLimit : ""));
                }
            }
        }

        /// <summary>Vacuum delta-v left in the stages the launch may burn, from the one burning now.</summary>
        private double DeltaVLeft()
        {
            var left = 0.0;
            var stage = Current;
            if (stage != null && _mass > stage.EndMass) left += stage.IspVac * G0 * Math.Log(_mass / stage.EndMass);
            for (var i = _stage + 1; i < _stages.Count; i++)
                if (_stages[i].Number >= _o.StageLimit && _stages[i].Burns) left += _stages[i].DeltaVVac;
            return left;
        }

        /// <summary>Seconds to burn <paramref name="dv"/> in vacuum from the stage burning now on; infinite when the stages can't give it.</summary>
        private double BurnTime(double dv)
        {
            var seconds = 0.0;
            var mass = _mass;
            for (var i = _stage; i < _stages.Count && dv > 0; i++)
            {
                var stage = _stages[i];
                if (stage.Number < _o.StageLimit) break;
                if (!stage.Burns) continue;
                if (i > _stage) mass = stage.StartMass;
                var exhaust = stage.IspVac * G0;
                var has = mass > stage.EndMass ? exhaust * Math.Log(mass / stage.EndMass) : 0;
                var use = Math.Min(dv, has);
                var end = mass / Math.Exp(use / exhaust);
                seconds += (mass - end) / stage.Flow;
                dv -= use;
            }
            return dv > 1e-6 ? double.PositiveInfinity : seconds;
        }

        /// <summary>
        /// Out of the air: coast to half the burn before apoapsis, then burn along the horizon at apoapsis (a node's fixed
        /// direction) until the orbit is circular, staging as before. Fails if the stages run dry or the craft sinks into the air.
        /// </summary>
        private AscentFlight Circularize()
        {
            double ap, pe, toAp;
            Orbit(out ap, out pe, out toAp);
            if (double.IsInfinity(ap)) return Fail("the coast escapes the body");
            var rAp = ap + _o.Radius;
            var a = (rAp + pe + _o.Radius) / 2;
            var need = Math.Sqrt(_o.Gm / rAp) - Math.Sqrt(_o.Gm * (2 / rAp - 1 / a));
            var burn = BurnTime(need);
            if (double.IsInfinity(burn))
                return Fail("can't circularize: it needs " + Math.Round(need) + " m/s at apoapsis and the stages have " + Math.Round(DeltaVLeft()) + " m/s");

            // The burn's direction: the horizon at apoapsis, fixed in space.
            var dt = 0.1;
            var end = _t + MaxOrbitSeconds;
            while (toAp > burn / 2 && _t < end)
            {
                Step(0, 0, 0, 0, 0, Math.Min(1, Math.Max(dt, (toAp - burn / 2) / 2)));
                Orbit(out ap, out pe, out toAp);
                if (Altitude < _o.AtmosphereHeight) return Fail("fell back into the atmosphere before circularizing");
            }
            double ux, uy, ex, ey;
            var angle = toAp * Math.Sqrt(_o.Gm / (rAp * rAp * rAp));
            Frame(out ux, out uy, out ex, out ey);
            // The horizon at apoapsis is today's horizon turned on by the angle still to go: below it now, above it after.
            var dirX = ex * Math.Cos(angle) - ux * Math.Sin(angle);
            var dirY = ey * Math.Cos(angle) - uy * Math.Sin(angle);
            var given = 0.0;
            while (given < need)
            {
                if (_t > end) return Fail("still circularizing after " + MaxOrbitSeconds + " s");
                var stage = Current;
                var accel = stage.ThrustVac / _mass;
                var step = Math.Min(dt, (need - given) / accel);
                var r = R;
                _vx += (-_o.Gm * _px / (r * r * r) + accel * dirX) * step;
                _vy += (-_o.Gm * _py / (r * r * r) + accel * dirY) * step;
                _px += _vx * step;
                _py += _vy * step;
                _mass -= stage.Flow * step;
                _t += step;
                given += accel * step;
                _flight.DeltaVToOrbit += accel * step;
                if (Altitude < _o.AtmosphereHeight) return Fail("fell back into the atmosphere while circularizing");
                if (stage.EndMass >= _mass && given < need && !StageOn())
                {
                    _mass = stage.EndMass;
                    return Fail("out of fuel while circularizing" + (_o.StageLimit > 0 ? " at stage limit " + _o.StageLimit : ""));
                }
            }
            Orbit(out ap, out pe, out toAp);
            if (pe < _o.AtmosphereHeight) return Fail("the circularization leaves the periapsis at " + Math.Round(pe) + " m, in the atmosphere");
            _flight.ReachesOrbit = true;
            _flight.DeltaVLeft = DeltaVLeft();
            return _flight;
        }
    }

    /// <summary>
    /// When <c>plan_ascent</c> calls a craft's margin thin (#367): the constants in one place, to be tuned against in-game runs.
    /// </summary>
    internal static class AscentMargin
    {
        /// <summary>Delta-v left at orbit, as a fraction of the delta-v to orbit, under which the margin is thin.</summary>
        public const double ThinDeltaVFraction = 0.05;
        /// <summary>The lowest vertical speed (m/s) while a Boost stage burns under the atmosphere top, under which the margin is thin.</summary>
        public const double ThinVerticalSpeed = 20;

        public static bool Marginal(double deltaVFraction, double minVerticalSpeed)
        {
            return deltaVFraction < ThinDeltaVFraction || minVerticalSpeed < ThinVerticalSpeed;
        }
    }
}
