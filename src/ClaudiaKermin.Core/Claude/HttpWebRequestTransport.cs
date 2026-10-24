// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Net;

namespace ClaudiaKermin.Core.Claude
{
    /// <summary>
    /// The real transport. KSP on macOS ships no System.Net.Http, so this is HttpWebRequest.
    /// TLS settings are never touched: ServicePointManager.SecurityProtocol is left as the runtime has it.
    /// </summary>
    public sealed class HttpWebRequestTransport : IHttpTransport
    {
        private const int TimeoutMs = 30000;

        public HttpResponseData Send(HttpRequestSpec request)
        {
            var web = (HttpWebRequest)WebRequest.Create(request.Url);
            web.Method = request.Method;
            web.Timeout = TimeoutMs;
            web.ReadWriteTimeout = TimeoutMs;
            foreach (var header in request.Headers)
            {
                if (string.Equals(header.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                    web.UserAgent = header.Value;
                else
                    web.Headers[header.Key] = header.Value;
            }

            try
            {
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

        private static HttpResponseData Read(HttpWebResponse response)
        {
            using (var reader = new StreamReader(response.GetResponseStream()))
                return new HttpResponseData { StatusCode = (int)response.StatusCode, Body = reader.ReadToEnd() };
        }
    }
}
