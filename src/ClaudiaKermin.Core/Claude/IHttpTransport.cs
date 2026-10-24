// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;

namespace ClaudiaKermin.Core.Claude
{
    public sealed class HttpRequestSpec
    {
        public string Method;
        public string Url;
        public List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();
    }

    public sealed class HttpResponseData
    {
        public int StatusCode;
        public string Body;
    }

    /// <summary>Raised by a transport when no HTTP response arrived at all (DNS, connect, timeout).</summary>
    public sealed class NetworkUnavailableException : Exception
    {
        public NetworkUnavailableException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// The seam under the Claude client. Blocking: callers run it on a worker thread. A non-2xx status
    /// is a normal response; only a missing response throws <see cref="NetworkUnavailableException"/>.
    /// </summary>
    public interface IHttpTransport
    {
        HttpResponseData Send(HttpRequestSpec request);
    }
}
