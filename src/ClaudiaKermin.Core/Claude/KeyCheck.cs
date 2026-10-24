// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Claude
{
    public enum KeyCheckResult
    {
        Valid,
        Invalid,
        NoNetwork,
        /// <summary>The API answered, but not with a verdict on the key (e.g. 5xx or 429), or the check itself failed.</summary>
        Unconfirmed,
    }

    public static class KeyCheck
    {
        /// <summary>Checks the client's key with GET /v1/models. Blocking; never throws (it runs on a worker thread).</summary>
        public static KeyCheckResult Run(ClaudeClient client)
        {
            try
            {
                var status = client.Get("/v1/models").StatusCode;
                if (status >= 200 && status < 300) return KeyCheckResult.Valid;
                if (status == 401 || status == 403) return KeyCheckResult.Invalid;
                return KeyCheckResult.Unconfirmed;
            }
            catch (NetworkUnavailableException)
            {
                return KeyCheckResult.NoNetwork;
            }
            catch (Exception)
            {
                return KeyCheckResult.Unconfirmed;
            }
        }

        public static string Describe(KeyCheckResult result)
        {
            switch (result)
            {
                case KeyCheckResult.Valid: return "Key is valid.";
                case KeyCheckResult.Invalid: return "Key was rejected by Anthropic.";
                case KeyCheckResult.NoNetwork: return "No network: could not check the key.";
                default: return "Anthropic did not confirm the key; try again later.";
            }
        }
    }
}
