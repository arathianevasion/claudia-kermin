// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Threading;

namespace ClaudiaKermin.Core.Claude
{
    public sealed class HttpRequestSpec
    {
        public string Method;
        public string Url;
        public List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();

        /// <summary>The request body as UTF-8 JSON text, or null for none.</summary>
        public string Body;
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
    /// An open response whose body is read as it arrives. <see cref="Read"/> blocks until bytes come,
    /// returns 0 at the end of the body and throws <see cref="System.IO.IOException"/> when the
    /// connection breaks or <see cref="Abort"/> is called.
    /// </summary>
    public interface IHttpStream : IDisposable
    {
        int StatusCode { get; }

        /// <summary>A response header's value, or null.</summary>
        string Header(string name);

        int Read(byte[] buffer, int offset, int count);

        /// <summary>Safe to call from any thread, any number of times; unblocks a pending <see cref="Read"/>.</summary>
        void Abort();
    }

    /// <summary>
    /// The seam under the Claude client. Blocking: callers run it on a worker thread. A non-2xx status
    /// is a normal response; only a missing response throws <see cref="NetworkUnavailableException"/>.
    /// </summary>
    public interface IHttpTransport
    {
        HttpResponseData Send(HttpRequestSpec request);

        /// <summary>
        /// Sends the request and returns once the response headers arrive. Cancelling
        /// <paramref name="cancel"/> aborts the request; the transport then throws
        /// <see cref="OperationCanceledException"/> or <see cref="NetworkUnavailableException"/>.
        /// </summary>
        IHttpStream Open(HttpRequestSpec request, CancellationToken cancel);
    }
}
