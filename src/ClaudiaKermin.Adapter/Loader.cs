// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>Created once when KSP starts; announces that Claudia Kermin loaded.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class Loader : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(this);
            Debug.Log(ModInfo.LoadedLine);
        }
    }
}
