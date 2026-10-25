// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The KSP control locks while Claudia has the craft: staging, action groups, SAS, RCS, gear, brakes and the throttle keys, and the
    /// time-warp keys while a sequence is Running. The core says what is locked (<see cref="MissionView.Locks"/>); this
    /// applies it as one lock owner, the same way the windows apply theirs (<see cref="WindowFrame"/>), and
    /// <see cref="Release"/> lets everything go when the Mission, the scene or the mod goes away. Attitude and translation
    /// stay free, and the watcher reads the raw key state, so Shift or Ctrl held over a second still takes Manual Control.
    /// </summary>
    public sealed class CraftLocks
    {
        private const string LockId = "ClaudiaKerminCraft";

        private ControlLocks _applied;

        /// <summary>Makes the game's locks match <paramref name="locks"/>; cheap when nothing changed.</summary>
        public void Apply(ControlLocks locks)
        {
            if (locks == _applied) return;
            InputLockManager.RemoveControlLock(LockId);
            if (locks != ControlLocks.None) InputLockManager.SetControlLock(Types(locks), LockId);
            _applied = locks;
        }

        /// <summary>Releases the lock; safe to call when nothing is locked.</summary>
        public void Release()
        {
            Apply(ControlLocks.None);
        }

        private static ControlTypes Types(ControlLocks locks)
        {
            ControlTypes types = 0;
            // Abort and Lights stay free: abort is the player's own safety, and lights can't ruin a sequence.
            if ((locks & ControlLocks.Staging) != 0) types |= ControlTypes.STAGING | ControlTypes.GROUP_STAGE;
            if ((locks & ControlLocks.ActionGroups) != 0) types |= ControlTypes.CUSTOM_ACTION_GROUPS;
            if ((locks & ControlLocks.Sas) != 0) types |= ControlTypes.SAS;
            if ((locks & ControlLocks.Rcs) != 0) types |= ControlTypes.RCS;
            if ((locks & ControlLocks.Gear) != 0) types |= ControlTypes.GROUP_GEARS;
            if ((locks & ControlLocks.Brakes) != 0) types |= ControlTypes.GROUP_BRAKES;
            // THROTTLE_CUT_MAX is the X cut-off and Z full keys. kOS drives the throttle through fly-by-wire, which these don't touch.
            if ((locks & ControlLocks.Throttle) != 0) types |= ControlTypes.THROTTLE | ControlTypes.THROTTLE_CUT_MAX;
            if ((locks & ControlLocks.Warp) != 0) types |= ControlTypes.TIMEWARP;
            if ((locks & ControlLocks.Attitude) != 0)
                types |= ControlTypes.PITCH | ControlTypes.YAW | ControlTypes.ROLL | ControlTypes.THROTTLE | ControlTypes.LINEAR
                    | ControlTypes.WHEEL_STEER | ControlTypes.WHEEL_THROTTLE;
            return types;
        }
    }
}
