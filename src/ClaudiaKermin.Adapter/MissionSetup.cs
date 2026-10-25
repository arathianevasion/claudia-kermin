// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The Mission being set up, pending until Start Mission: its type, model, Spending Cap and Mission Settings.
    /// The Settings window edits it and the Mission window sends it with Check craft and Start Mission. Seeded from
    /// the start form's defaults (the player config); <see cref="MissionDriver"/> owns it and reseeds it once a
    /// Mission is under way, so every new Mission starts from the defaults.
    /// </summary>
    public sealed class MissionSetup
    {
        /// <summary>Remote: Claudia at Mission Control. False is crewed (she flies aboard).</summary>
        public bool Remote;
        public string Model = "";
        /// <summary>USD; always positive once seeded.</summary>
        public decimal SpendingCap;
        public MissionSettingsValues Settings = new MissionSettingsValues();

        private bool _seeded;

        /// <summary>Takes the start form's defaults, the first time and after <see cref="Reset"/>; a form not yet built (no models) is skipped.</summary>
        public void SeedFrom(StartFormView form)
        {
            if (_seeded || form.Models.Count == 0) return;
            Remote = form.DefaultRemote;
            Model = form.DefaultModel;
            SpendingCap = form.DefaultSpendingCap;
            Settings = form.DefaultSettings.Copy();
            _seeded = true;
        }

        /// <summary>The next <see cref="SeedFrom"/> takes the defaults again (a Mission is under way).</summary>
        public void Reset()
        {
            _seeded = false;
        }

        /// <summary>The model to send: the one chosen while it is still offered, otherwise the default.</summary>
        public string ModelFor(StartFormView form)
        {
            return form.Models.Contains(Model) ? Model : form.DefaultModel;
        }
    }
}
