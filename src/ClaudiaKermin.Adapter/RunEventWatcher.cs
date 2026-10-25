// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// Watches the game for the notable events of a run (a stage fired, a part destroyed or broken off, a part close to its
    /// heat limit, science collected) and reports each to Mission Control as a <see cref="RunEventSeen"/>, for the Debrief.
    /// Reports only while a sequence is Running and only for the vessel the player is flying; the core keeps the log.
    /// </summary>
    public sealed class RunEventWatcher
    {
        private const float HeatSeconds = 0.5f;

        private readonly Action<MissionInput> _send;
        private readonly Func<bool> _running;
        private readonly HashSet<uint> _hot = new HashSet<uint>();
        private float _nextHeat;

        public RunEventWatcher(Action<MissionInput> send, Func<bool> running)
        {
            _send = send;
            _running = running;
            GameEvents.onStageActivate.Add(OnStage);
            GameEvents.onPartDie.Add(OnPartDie);
            GameEvents.onPartJointBreak.Add(OnJointBreak);
            GameEvents.OnExperimentDeployed.Add(OnExperiment);
            GameEvents.OnScienceRecieved.Add(OnScience);
        }

        /// <summary>Call each frame: parts near their heat limit are reported once per run.</summary>
        public void Update()
        {
            if (!_running())
            {
                _hot.Clear();
                return;
            }
            if (Time.realtimeSinceStartup < _nextHeat) return;
            _nextHeat = Time.realtimeSinceStartup + HeatSeconds;
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return;
            foreach (var part in vessel.Parts)
            {
                var percent = PartHeat.Percent(part.temperature, part.maxTemp, part.skinTemperature, part.skinMaxTemp);
                if (percent < RunEventLog.OverheatPercent || !_hot.Add(part.persistentId)) continue;
                Report(RunEventKind.Overheating, part.partInfo.title + " at " + percent.ToString("0") + "% of its heat limit");
            }
        }

        private bool Flying(Part part)
        {
            return _running() && part != null && part.vessel != null && part.vessel == FlightGlobals.ActiveVessel;
        }

        private void Report(RunEventKind kind, string text)
        {
            if (_running()) _send(new RunEventSeen(kind, text, Planetarium.GetUniversalTime()));
        }

        private void OnStage(int stage)
        {
            Report(RunEventKind.Staging, "stage " + stage + " fired");
        }

        private void OnPartDie(Part part)
        {
            if (Flying(part)) Report(RunEventKind.PartLost, part.partInfo.title + " was destroyed");
        }

        private void OnJointBreak(PartJoint joint, float breakForce)
        {
            if (joint != null && Flying(joint.Child)) Report(RunEventKind.PartLost, joint.Child.partInfo.title + " broke off");
        }

        private void OnExperiment(ScienceData data)
        {
            if (data != null) Report(RunEventKind.Science, data.title + " collected (" + data.dataAmount.ToString("0.#") + " Mits)");
        }

        private void OnScience(float amount, ScienceSubject subject, ProtoVessel source, bool reverted)
        {
            if (!reverted && subject != null) Report(RunEventKind.Science, subject.title + ": " + amount.ToString("0.#") + " science received");
        }
    }
}
