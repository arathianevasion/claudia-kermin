// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// The air of a body for the reentry predictor: the adapter reads KSP's own pressure and temperature curves; tests give a
    /// table. It is asked hundreds of times per prediction, so it must be cheap.
    /// </summary>
    public interface IReentryAir
    {
        /// <summary>
        /// Density (kg/m³) and speed of sound (m/s) at <paramref name="altitude"/> m above sea level over <paramref name="latitude"/>
        /// degrees; density 0 above the atmosphere.
        /// </summary>
        void Air(double altitude, double latitude, out double density, out double speedOfSound);
    }

    /// <summary>
    /// The craft for the reentry predictor, held surface retrograde (as <c>reentry</c> flies it): the adapter reads KSP's drag
    /// cubes for that attitude; tests give the research's capsule.
    /// </summary>
    public interface IReentryCraft
    {
        /// <summary>The craft's mass now, kg.</summary>
        double MassKg { get; }

        /// <summary>
        /// The drag area, m², held retrograde at <paramref name="mach"/>, so that drag (N) = ½ρv² × this; <paramref name="densitySpeed"/>
        /// (ρv, kg/m²/s) is for KSP's pseudo-Reynolds curve.
        /// </summary>
        double DragArea(double mach, double densitySpeed);
    }

    /// <summary>
    /// How the frame orbit elements use sits on a turning body: the longitude under its x axis at <see cref="Ut"/>, and whether
    /// longitude grows with the frame's angle (1) or against it (-1), as <see cref="LandingSiteSearch.Frame"/> reads it.
    /// </summary>
    internal sealed class BodyFrame
    {
        public double LonOffset;
        public double Handed = 1;
        public double Ut;
        public double RotationPeriod;

        /// <summary>The frame of <paramref name="body"/> as it is at <paramref name="now"/>, or null with <paramref name="error"/> saying why it can't be read.</summary>
        public static BodyFrame Read(SnapshotBody body, IPlanningBody api, double now, out string error)
        {
            double lonOffset, handed;
            error = LandingSiteSearch.Frame(body.Name, api, out lonOffset, out handed);
            if (error != null) return null;
            return new BodyFrame { LonOffset = lonOffset, Handed = handed, Ut = now, RotationPeriod = body.RotationPeriod };
        }

        /// <summary>The body's angular velocity in the frame, rad/s: the air turns with it.</summary>
        public Vec3 Spin
        {
            get { return new Vec3(0, 0, RotationPeriod > 0 ? Handed * 2 * Math.PI / RotationPeriod : 0); }
        }

        /// <summary>The latitude and longitude, degrees, of the ground under <paramref name="r"/> at <paramref name="ut"/>.</summary>
        public void Ground(Vec3 r, double ut, out double latitude, out double longitude)
        {
            var length = r.Length;
            latitude = Math.Asin(Math.Max(-1, Math.Min(1, r.Z / length))) * 180 / Math.PI;
            var turn = RotationPeriod > 0 ? 360 / RotationPeriod : 0;
            longitude = LandingSiteSearch.Wrap180(LonOffset + Handed * Math.Atan2(r.Y, r.X) * 180 / Math.PI - turn * (ut - Ut));
        }
    }

    /// <summary>Where a craft comes down, or why the predictor can't say.</summary>
    internal sealed class ReentryPrediction
    {
        /// <summary>Null when it landed; otherwise why there is no landing point.</summary>
        public string Error;
        public double Latitude;
        public double Longitude;
        /// <summary>When it first reaches the top of the atmosphere, and when it reaches the ground.</summary>
        public double EntryUt;
        public double LandingUt;
        /// <summary>Where it reaches the ground, in the body's inertial frame, m.</summary>
        public Vec3 Position;
        /// <summary>The most drag deceleration on the way down, in g.</summary>
        public double PeakG;
    }

    /// <summary>
    /// The reentry predictor (#282, from the reentry research of #247): where a craft held surface retrograde lands. The vacuum
    /// leg follows the Kepler orbit (<see cref="Conic"/>, as predict_burn) to the top of the atmosphere; from there gravity and
    /// drag are integrated with fixed-step RK4 against the turning atmosphere, as the Trajectories mod does. The air and the
    /// craft's drag come through <see cref="IReentryAir"/> and <see cref="IReentryCraft"/>. Parachutes and the heat shield's
    /// drop are not modelled: they come after Mach 1, a few km of the downrange at most. Shared by the deorbit burn search and
    /// the block-internal calls; it only reads.
    /// </summary>
    internal sealed class ReentryPredictor
    {
        private const double G0 = 9.80665;
        /// <summary>The RK4 step, s: within about 10 m of a 0.1 s step through Kerbin's atmosphere (the research's step table).</summary>
        public const double Step = 2;
        /// <summary>A craft still flying after this long in the atmosphere is given up on.</summary>
        public const double MaxAtmosphereSeconds = 3600;
        /// <summary>Times it may skip back out of the atmosphere and fall in again.</summary>
        private const int MaxSkips = 3;

        private readonly SnapshotBody _body;
        private readonly IReentryAir _air;
        private readonly IReentryCraft _craft;
        private readonly Vec3 _spin;
        private double _mass;
        private double _peak;

        private ReentryPredictor(SnapshotBody body, IReentryAir air, IReentryCraft craft, BodyFrame frame)
        {
            _body = body;
            _air = air;
            _craft = craft;
            _spin = frame.Spin;
        }

        /// <summary>
        /// Where the craft on <paramref name="orbit"/> round <paramref name="body"/> lands, falling from <paramref name="fromUt"/>,
        /// on ground <paramref name="groundAltitude"/> m above sea level, with mass <paramref name="massKg"/>.
        /// </summary>
        public static ReentryPrediction Predict(SnapshotBody body, Conic orbit, double fromUt, double groundAltitude, double massKg,
            IReentryAir air, IReentryCraft craft, BodyFrame frame, double step = Step)
        {
            if (body.AtmosphereHeight <= 0) return Fail(body.Name + " has no atmosphere");
            var predictor = new ReentryPredictor(body, air, craft, frame) { _mass = massKg };
            return predictor.Fly(orbit, fromUt, groundAltitude, frame, step);
        }

        private ReentryPrediction Fly(Conic orbit, double t, double ground, BodyFrame frame, double step)
        {
            var radius = _body.Radius;
            var top = radius + _body.AtmosphereHeight;
            var prediction = new ReentryPrediction();
            if (orbit.Radius(t) <= radius + ground) return Fail("the craft is already on the ground");
            for (var skip = 0; skip <= MaxSkips; skip++)
            {
                if (orbit.Radius(t) > top)
                {
                    var entry = orbit.TimeAtRadius(top, t, false);
                    if (!entry.HasValue)
                        return Fail(skip == 0
                            ? "the orbit never reaches " + _body.Name + "'s atmosphere (periapsis " + Metres(orbit.PeR - radius) + " m)"
                            : "the craft skips out of " + _body.Name + "'s atmosphere and does not fall back in");
                    t = entry.Value;
                }
                if (skip == 0) prediction.EntryUt = t;
                Vec3 r, v;
                orbit.State(t, out r, out v);
                var end = t + MaxAtmosphereSeconds;
                var skipped = false;
                while (t < end)
                {
                    var r0 = r;
                    var h0 = r.Length - radius;
                    Rk4(ref r, ref v, step);
                    t += step;
                    var h = r.Length - radius;
                    if (h <= ground)
                    {
                        var f = (h0 - ground) / (h0 - h);
                        var at = r0 + (r - r0) * f;
                        var ut = t - step + f * step;
                        double lat, lng;
                        frame.Ground(at, ut, out lat, out lng);
                        prediction.Latitude = lat;
                        prediction.Longitude = lng;
                        prediction.LandingUt = ut;
                        prediction.Position = at;
                        prediction.PeakG = _peak;
                        return prediction;
                    }
                    if (h > _body.AtmosphereHeight && Vec3.Dot(r, v) > 0)
                    {
                        orbit = Conic.FromState(_body.Gm, r, v, t);
                        skipped = true;
                        break;
                    }
                }
                if (!skipped) return Fail("the craft is still flying after " + Metres(MaxAtmosphereSeconds) + " s in " + _body.Name + "'s atmosphere");
            }
            return Fail("the craft skips out of " + _body.Name + "'s atmosphere more than " + MaxSkips + " times");
        }

        private void Rk4(ref Vec3 r, ref Vec3 v, double dt)
        {
            var a1 = Accel(r, v);
            var v2 = v + a1 * (dt / 2);
            var a2 = Accel(r + v * (dt / 2), v2);
            var v3 = v + a2 * (dt / 2);
            var a3 = Accel(r + v2 * (dt / 2), v3);
            var v4 = v + a3 * dt;
            var a4 = Accel(r + v3 * dt, v4);
            r = r + (v + 2 * v2 + 2 * v3 + v4) * (dt / 6);
            v = v + (a1 + 2 * a2 + 2 * a3 + a4) * (dt / 6);
        }

        private Vec3 Accel(Vec3 r, Vec3 v)
        {
            var length = r.Length;
            var a = r * (-_body.Gm / (length * length * length));
            var h = length - _body.Radius;
            if (h >= _body.AtmosphereHeight) return a;
            double density, sound;
            _air.Air(h, Math.Asin(Math.Max(-1, Math.Min(1, r.Z / length))) * 180 / Math.PI, out density, out sound);
            if (density <= 0) return a;
            var air = v - Vec3.Cross(_spin, r);
            var speed = air.Length;
            if (speed <= 0) return a;
            var area = _craft.DragArea(sound > 0 ? speed / sound : 0, density * speed);
            var k = 0.5 * density * speed * area / _mass;
            _peak = Math.Max(_peak, k * speed / G0);
            return a - air * k;
        }

        private static ReentryPrediction Fail(string reason)
        {
            return new ReentryPrediction { Error = reason };
        }

        private static string Metres(double value)
        {
            return Math.Round(value).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
