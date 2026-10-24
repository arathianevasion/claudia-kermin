// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>A Script Check that passes every script, for tests about other parts of the Mission loop.</summary>
    internal sealed class PassingScriptCheck : IScriptCheck
    {
        public ScriptCheckResult Check(string script)
        {
            return ScriptCheckResult.Pass();
        }
    }
}
