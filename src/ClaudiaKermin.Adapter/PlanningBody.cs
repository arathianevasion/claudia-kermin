// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Tools;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The live game behind the Planning Tools' body interface (#265, #278): terrain height, biome and the sun, read with stock
    /// calls. Stock's terrain code is main-thread only (the PQS shares a scratch buffer), but tools run on a worker thread, so a
    /// call from a worker is queued, and <see cref="Pump"/>, called every frame from the main thread, serves the queue for at
    /// most <see cref="BudgetMs"/> of the frame; the worker waits meanwhile. A batch is served a sample at a time, so a big one is
    /// spread over frames instead of stalling one. Called on the main thread itself (the test-only kOS bridge) it runs at once.
    /// Reads only: it never changes the game.
    /// </summary>
    public sealed class PlanningBody : PlanningBodyBase
    {
        /// <summary>The most of one frame the queue may use, ms; one sample past it at most, so a job always moves on.</summary>
        public const double BudgetMs = 3;
        /// <summary>A worker stops waiting for a frame to serve it after this long (a paused game, or a scene change).</summary>
        private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

        private sealed class Job
        {
            /// <summary>Does one unit of the work; true when the job is done.</summary>
            public Func<bool> Step;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public volatile bool Abandoned;
        }

        private readonly Queue<Job> _queue = new Queue<Job>();
        private readonly int _mainThread = Thread.CurrentThread.ManagedThreadId;

        /// <summary>Serves queued work for this frame's budget. Main thread; call it from an Update.</summary>
        public void Pump()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                Job job;
                lock (_queue)
                {
                    if (_queue.Count == 0) return;
                    job = _queue.Peek();
                }
                var finished = job.Abandoned || Step(job);
                if (finished)
                {
                    lock (_queue) _queue.Dequeue();
                    job.Done.Set();
                }
                if (watch.Elapsed.TotalMilliseconds >= BudgetMs) return;
            }
        }

        private static bool Step(Job job)
        {
            try
            {
                return job.Step();
            }
            catch (Exception e)
            {
                // One bad read must cost the tool its answer, not the frame.
                Debug.LogException(e);
                return true;
            }
        }

        /// <summary>Runs <paramref name="step"/> until it says it is done: now on the main thread, or a slice a frame from the queue.</summary>
        private void Run(Func<bool> step)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThread)
            {
                var guard = new Job { Step = step };
                while (!Step(guard)) { }
                return;
            }
            var job = new Job { Step = step };
            lock (_queue) _queue.Enqueue(job);
            if (!job.Done.Wait(WaitLimit)) job.Abandoned = true;
        }

        private static CelestialBody Find(string name)
        {
            return FlightGlobals.Bodies == null ? null
                : FlightGlobals.Bodies.FirstOrDefault(b => string.Equals(b.bodyName, name, StringComparison.OrdinalIgnoreCase));
        }

        public override double TerrainAltitude(string body, double latitude, double longitude)
        {
            var sample = new SiteSample { Latitude = latitude, Longitude = longitude };
            Survey(body, new List<SiteSample> { sample }, SiteFacts.Altitude);
            return sample.Altitude;
        }

        public override string Biome(string body, double latitude, double longitude)
        {
            var sample = new SiteSample { Latitude = latitude, Longitude = longitude };
            Survey(body, new List<SiteSample> { sample }, SiteFacts.Biome);
            return sample.Biome;
        }

        public override double SunElevation(string body, double latitude, double longitude, double ut)
        {
            var sample = new SiteSample { Latitude = latitude, Longitude = longitude, Ut = ut };
            Survey(body, new List<SiteSample> { sample }, SiteFacts.Sun);
            return sample.SunElevation;
        }

        public override void Survey(string body, IList<SiteSample> samples, SiteFacts facts)
        {
            var next = 0;
            Run(() =>
            {
                if (next >= samples.Count) return true;
                var celestial = Find(body);
                var sample = samples[next++];
                if (celestial != null) Read(celestial, sample, facts);
                return next >= samples.Count;
            });
        }

        public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
        {
            double lat = double.NaN, lon = double.NaN;
            Run(() =>
            {
                var celestial = Find(body);
                if (celestial != null)
                {
                    // Orbit elements give inertial vectors (the reference plane, z north). Stock takes them into the game through
                    // Planetarium.Zup, as Orbit does, which its rotating reference frame turns with the body near it while the
                    // body's own frame stands still (#326: skipping it put a landing off by the turn since that frame began).
                    // The game's vectors are then the same with y and z swapped.
                    var game = Planetarium.Zup.WorldToLocal(new Vector3d(x, y, z));
                    var at = celestial.GetLatitudeAndLongitude(game.xzy, true);
                    lat = at.x;
                    lon = at.y;
                }
                return true;
            });
            latitude = lat;
            longitude = lon;
            return !double.IsNaN(lat) && !double.IsNaN(lon);
        }

        /// <summary>Main thread only.</summary>
        private static void Read(CelestialBody body, SiteSample sample, SiteFacts facts)
        {
            if ((facts & SiteFacts.Altitude) != 0 && body.pqsController != null)
                sample.Altitude = body.TerrainAltitude(sample.Latitude, sample.Longitude, true);
            if ((facts & SiteFacts.Biome) != 0) sample.Biome = BiomeAt(body, sample.Latitude, sample.Longitude);
            if ((facts & SiteFacts.Sun) != 0) sample.SunElevation = SunElevationAt(body, sample.Latitude, sample.Longitude, sample.Ut);
        }

        /// <summary>The stock biome name, as science uses it; "" for a body with no biome map (stock throws for those).</summary>
        private static string BiomeAt(CelestialBody body, double latitude, double longitude)
        {
            if (body.BiomeMap == null || body.BiomeMap.Attributes == null || body.BiomeMap.Attributes.Length == 0) return "";
            return ScienceUtil.GetExperimentBiome(body, latitude, longitude) ?? "";
        }

        /// <summary>
        /// The sun's elevation above the horizon at a fixed point of the body at <paramref name="ut"/>, degrees. The sun's direction
        /// comes from both bodies' positions at the time. The point's up at that time is the surface normal of the longitude it
        /// has under the body's present rotation, moved by the turn since: the body turns east, so a point fixed to it sits under
        /// a longitude further east by the turn.
        /// </summary>
        private static double SunElevationAt(CelestialBody body, double latitude, double longitude, double ut)
        {
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun == null || body.rotationPeriod <= 0) return double.NaN;
            var toSun = sun.getTruePositionAtUT(ut) - body.getTruePositionAtUT(ut);
            var up = body.GetSurfaceNVector(latitude, longitude + 360 * (ut - Planetarium.GetUniversalTime()) / body.rotationPeriod);
            if (toSun.magnitude <= 0 || up.magnitude <= 0) return double.NaN;
            return 90 - Vector3d.Angle(up, toSun);
        }
    }
}
