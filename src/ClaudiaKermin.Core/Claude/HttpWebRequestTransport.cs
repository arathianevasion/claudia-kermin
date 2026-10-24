// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace ClaudiaKermin.Core.Claude
{
    /// <summary>
    /// The real transport. KSP on macOS ships no System.Net.Http, so this is HttpWebRequest.
    /// TLS settings are never touched: ServicePointManager.SecurityProtocol is left as the runtime has it.
    /// </summary>
    public sealed class HttpWebRequestTransport : IHttpTransport
    {
        /// <summary>Connect timeout: until the response headers arrive.</summary>
        private const int ConnectTimeoutMs = 30000;

        /// <summary>
        /// A backstop on each body read. The client's own stall watchdog (60 s with no bytes) aborts
        /// first; this only stops a read hanging for ever if that watchdog were missing.
        /// </summary>
        private const int ReadTimeoutMs = 90000;

        public HttpResponseData Send(HttpRequestSpec request)
        {
            var web = Create(request);
            web.ReadWriteTimeout = ConnectTimeoutMs;
            try
            {
                WriteBody(web, request);
                using (var response = (HttpWebResponse)web.GetResponse())
                    return Read(response);
            }
            catch (WebException e) when (e.Response is HttpWebResponse errorResponse)
            {
                using (errorResponse)
                    return Read(errorResponse);
            }
            catch (WebException e)
            {
                throw new NetworkUnavailableException(e.Status.ToString(), e);
            }
        }

        public IHttpStream Open(HttpRequestSpec request, CancellationToken cancel)
        {
            var web = Create(request);
            web.ReadWriteTimeout = ReadTimeoutMs;
            web.AllowReadStreamBuffering = false;
            using (cancel.Register(web.Abort))
            {
                try
                {
                    cancel.ThrowIfCancellationRequested();
                    WriteBody(web, request);
                    return new ResponseStream(web, (HttpWebResponse)web.GetResponse());
                }
                catch (WebException e) when (e.Response is HttpWebResponse errorResponse)
                {
                    return new ResponseStream(web, errorResponse);
                }
                catch (WebException e)
                {
                    cancel.ThrowIfCancellationRequested();
                    throw new NetworkUnavailableException(e.Status.ToString(), e);
                }
                catch (IOException e)
                {
                    cancel.ThrowIfCancellationRequested();
                    throw new NetworkUnavailableException("io", e);
                }
            }
        }

        private static HttpWebRequest Create(HttpRequestSpec request)
        {
            var web = (HttpWebRequest)WebRequest.Create(request.Url);
            web.Method = request.Method;
            web.Timeout = ConnectTimeoutMs;
            foreach (var header in request.Headers)
            {
                if (string.Equals(header.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                    web.UserAgent = header.Value;
                else if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    web.ContentType = header.Value;
                else
                    web.Headers[header.Key] = header.Value;
            }
            return web;
        }

        private static void WriteBody(HttpWebRequest web, HttpRequestSpec request)
        {
            if (request.Body == null) return;
            var bytes = new UTF8Encoding(false).GetBytes(request.Body);
            web.ContentLength = bytes.Length;
            using (var body = web.GetRequestStream())
                body.Write(bytes, 0, bytes.Length);
        }

        private static HttpResponseData Read(HttpWebResponse response)
        {
            using (var reader = new StreamReader(response.GetResponseStream()))
                return new HttpResponseData { StatusCode = (int)response.StatusCode, Body = reader.ReadToEnd() };
        }

        private sealed class ResponseStream : IHttpStream
        {
            private readonly HttpWebRequest _request;
            private readonly HttpWebResponse _response;
            private readonly Stream _body;

            public ResponseStream(HttpWebRequest request, HttpWebResponse response)
            {
                _request = request;
                _response = response;
                try
                {
                    _body = response.GetResponseStream();
                }
                catch
                {
                    response.Close();
                    throw;
                }
            }

            public int StatusCode
            {
                get { return (int)_response.StatusCode; }
            }

            public string Header(string name)
            {
                return _response.Headers[name];
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                try
                {
                    return _body.Read(buffer, offset, count);
                }
                catch (WebException e)
                {
                    throw new IOException(e.Status.ToString(), e);
                }
                catch (ObjectDisposedException e)
                {
                    throw new IOException("aborted", e);
                }
            }

            public void Abort()
            {
                _request.Abort();
            }

            public void Dispose()
            {
                _body.Dispose();
                _response.Close();
            }
        }
    }
}
