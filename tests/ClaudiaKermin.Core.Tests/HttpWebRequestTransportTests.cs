// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The real transport builds a real HttpWebRequest from the exact headers ClaudeClient sends.
    /// The request is created, never sent: no test here touches the network.
    /// These tests run on modern .NET, which accepts restricted headers through Headers; KSP's Mono
    /// throws. So the test records what the transport adds through Headers and fails if any of it is
    /// a name Mono restricts (the list is from the HttpWebRequest documentation, not from the code).
    /// </summary>
    public class HttpWebRequestTransportTests
    {
        private const string Key = "sk-ant-api03-HEADERSECRET-wxyz";

        private static readonly string[] Restricted =
        {
            "Accept", "Connection", "Content-Length", "Content-Type", "Date", "Expect", "Host",
            "If-Modified-Since", "Keep-Alive", "Proxy-Connection", "Range", "Referer", "Transfer-Encoding", "User-Agent",
        };

        private static void AssertNoRestrictedHeadersAddedThroughHeaders(HttpRequestSpec spec)
        {
            var added = new List<string>();
            HttpWebRequestTransport.Create(spec, (name, value) => added.Add(name));

            var offending = added.Where(n => Restricted.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            Assert.Empty(offending);
            Assert.NotEmpty(added); // x-api-key and anthropic-version still arrive
        }

        [Fact]
        public void KeyCheckRequestAddsOnlyUnrestrictedHeadersThroughHeaders()
        {
            var transport = new FakeTransport();
            new ClaudeClient(transport, () => Key, line => { }).Get("/v1/models");

            AssertNoRestrictedHeadersAddedThroughHeaders(transport.Requests[0]);

            var web = HttpWebRequestTransport.Create(transport.Requests[0]);
            Assert.Equal("GET", web.Method);
            Assert.Equal(ClaudeClient.UserAgent, web.UserAgent);
            Assert.Equal(Key, web.Headers["x-api-key"]);
        }

        [Fact]
        public void StreamedMessagesRequestAddsOnlyUnrestrictedHeadersThroughHeaders()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("text.sse"));
            var request = new MessagesRequest { Model = "claude-sonnet-5-5", MaxTokens = 16000, Effort = "low" };
            request.Messages.Add(Message.UserText("How far is the Mun?"));
            new ClaudeClient(transport, () => Key, line => { }).Stream(request, null, CancellationToken.None);

            AssertNoRestrictedHeadersAddedThroughHeaders(transport.Requests[0]);

            var web = HttpWebRequestTransport.Create(transport.Requests[0]);
            Assert.Equal("POST", web.Method);
            Assert.Equal("text/event-stream", web.Accept);
            Assert.Equal("application/json", web.ContentType);
            Assert.Equal(ClaudeClient.UserAgent, web.UserAgent);
            Assert.Equal(ClaudeClient.BetaHeaders, web.Headers["anthropic-beta"]);
            Assert.Equal(Key, web.Headers["x-api-key"]);
        }
    }
}
