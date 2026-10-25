// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Sends Craft Check requests to a transport of their own that always answers Pass, so the tests of the
    /// Mission loop see only the Mission's requests, in the order they always did.
    /// </summary>
    internal sealed class CraftCheckRouter : IHttpTransport
    {
        private readonly IHttpTransport _mission;
        private readonly StreamingFakeTransport _checks = new StreamingFakeTransport();

        public CraftCheckRouter(IHttpTransport mission)
        {
            _mission = mission;
            for (var i = 0; i < 20; i++) _checks.Add(ScriptedResponse.Fixture("craft-check-pass-free.sse", 64));
        }

        public HttpResponseData Send(HttpRequestSpec request)
        {
            return _mission.Send(request);
        }

        public IHttpStream Open(HttpRequestSpec request, CancellationToken cancel)
        {
            return request.Body.Contains("\"required\":[\"summary\",\"findings\"") ? _checks.Open(request, cancel) : _mission.Open(request, cancel);
        }

        /// <summary>
        /// Start Mission as the player presses it, with a snapshot to check: the Craft Check runs first (it passes),
        /// and the Mission starts when it comes back. Returns as soon as it has, before anything else is drained.
        /// </summary>
        public static void StartChecked(MissionControl mission, Background background, GameSnapshot snapshot, StartMission start)
        {
            mission.Send(snapshot);
            mission.Send(start);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (mission.View.CraftCheck.State == CraftCheckState.Running && DateTime.UtcNow < deadline)
            {
                background.Drain();
                Thread.Sleep(2);
            }
            Assert.NotEqual(CraftCheckState.Running, mission.View.CraftCheck.State);
        }
    }
}
