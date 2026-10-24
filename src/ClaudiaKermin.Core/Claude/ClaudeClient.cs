// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace ClaudiaKermin.Core.Claude
{
    /// <summary>
    /// The first slice of the Claude client: a non-streaming GET with the pinned headers and one log
    /// line per request. Blocking; run it through <see cref="Background"/>.
    /// </summary>
    public sealed class ClaudeClient
    {
        public const string BaseUrl = "https://api.anthropic.com";

        /// <summary>Pinned in code; changed only with a mod release.</summary>
        public const string ApiVersion = "2026-10-21";

        public static string UserAgent
        {
            get { return "ClaudiaKermin/" + ModInfo.Version; }
        }

        private static int _nextRequestId;

        private readonly IHttpTransport _transport;
        private readonly Func<string> _apiKey;
        private readonly Action<string> _log;

        /// <param name="apiKey">Read per request, so a changed key takes effect immediately.</param>
        /// <param name="log">Receives finished KSP.log lines.</param>
        public ClaudeClient(IHttpTransport transport, Func<string> apiKey, Action<string> log)
        {
            _transport = transport;
            _apiKey = apiKey;
            _log = log;
        }

        /// <summary>Throws <see cref="NetworkUnavailableException"/> when there is no response.</summary>
        public HttpResponseData Get(string path)
        {
            var request = new HttpRequestSpec { Method = "GET", Url = BaseUrl + path };
            request.Headers.Add(new KeyValuePair<string, string>("x-api-key", _apiKey() ?? ""));
            request.Headers.Add(new KeyValuePair<string, string>("anthropic-version", ApiVersion));
            request.Headers.Add(new KeyValuePair<string, string>("User-Agent", UserAgent));

            var id = Interlocked.Increment(ref _nextRequestId);
            var timer = Stopwatch.StartNew();
            try
            {
                var response = _transport.Send(request);
                Log(id, request.Method, path, response.StatusCode.ToString(CultureInfo.InvariantCulture), timer);
                return response;
            }
            catch (NetworkUnavailableException)
            {
                Log(id, request.Method, path, "no-network", timer);
                throw;
            }
        }

        // Only ids, the path, status and timing: never headers, the key, or any request or response text.
        private void Log(int id, string method, string path, string status, Stopwatch timer)
        {
            _log(ModInfo.LogTag + " request id=" + id.ToString(CultureInfo.InvariantCulture) + " " + method + " " + path
                + " status=" + status + " ms=" + timer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
        }
    }
}
