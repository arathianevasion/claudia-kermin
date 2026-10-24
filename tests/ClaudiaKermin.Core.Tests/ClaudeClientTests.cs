// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Claude;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>Hand-written transport: no test here touches the network.</summary>
    internal sealed class FakeTransport : IHttpTransport
    {
        public int Status = 200;
        public bool NoNetwork;
        public bool Throw;
        public readonly List<HttpRequestSpec> Requests = new List<HttpRequestSpec>();

        public HttpResponseData Send(HttpRequestSpec request)
        {
            Requests.Add(request);
            if (NoNetwork) throw new NetworkUnavailableException("NameResolutionFailure", null);
            if (Throw) throw new InvalidOperationException("boom");
            return new HttpResponseData { StatusCode = Status, Body = "{\"data\":[]}" };
        }
    }

    public class ClaudeClientTests
    {
        private const string Key = "sk-ant-api03-SECRETSECRETSECRET-abcd";

        private static ClaudeClient ClientFor(FakeTransport transport, List<string> log)
        {
            return new ClaudeClient(transport, () => Key, log.Add);
        }

        [Fact]
        public void SendsPinnedHeadersAndTheKey()
        {
            var transport = new FakeTransport();
            ClientFor(transport, new List<string>()).Get("/v1/models");

            var request = Assert.Single(transport.Requests);
            Assert.Equal("GET", request.Method);
            Assert.Equal("https://api.anthropic.com/v1/models", request.Url);
            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value);
            Assert.Equal("2026-10-21", headers["anthropic-version"]);
            Assert.Equal("ClaudiaKermin/" + ModInfo.Version, headers["User-Agent"]);
            Assert.Equal(Key, headers["x-api-key"]);
        }

        [Fact]
        public void LogsExactlyOneLineWithIdStatusAndTiming()
        {
            var log = new List<string>();
            ClientFor(new FakeTransport { Status = 401 }, log).Get("/v1/models");

            var line = Assert.Single(log);
            Assert.Matches(@"^\[ClaudiaKermin\] request id=\d+ GET /v1/models status=401 ms=\d+$", line);
        }

        [Fact]
        public void LogsOneLineWhenThereIsNoNetwork()
        {
            var log = new List<string>();
            var client = ClientFor(new FakeTransport { NoNetwork = true }, log);

            Assert.Throws<NetworkUnavailableException>(() => client.Get("/v1/models"));
            Assert.Contains("status=no-network", Assert.Single(log));
        }

        [Theory]
        [InlineData(200)]
        [InlineData(401)]
        [InlineData(500)]
        public void KeyAndHeadersNeverReachTheLog(int status)
        {
            var log = new List<string>();
            var transport = new FakeTransport { Status = status };
            ClientFor(transport, log).Get("/v1/models");

            var everything = string.Join("\n", log);
            Assert.DoesNotContain(Key, everything);
            Assert.DoesNotContain("SECRETSECRET", everything);
            foreach (var header in transport.Requests[0].Headers)
            {
                Assert.DoesNotContain(header.Key, everything, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(header.Value, everything);
            }
        }

        [Fact]
        public void KeyAndHeadersNeverReachTheLogWithoutNetwork()
        {
            var log = new List<string>();
            var client = ClientFor(new FakeTransport { NoNetwork = true }, log);
            Assert.Throws<NetworkUnavailableException>(() => client.Get("/v1/models"));

            var everything = string.Join("\n", log);
            Assert.DoesNotContain("SECRETSECRET", everything);
            Assert.DoesNotContain("x-api-key", everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("anthropic-version", everything, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(200, KeyCheckResult.Valid)]
        [InlineData(401, KeyCheckResult.Invalid)]
        [InlineData(403, KeyCheckResult.Invalid)]
        [InlineData(429, KeyCheckResult.Unconfirmed)]
        [InlineData(500, KeyCheckResult.Unconfirmed)]
        public void KeyCheckMapsStatusToResult(int status, KeyCheckResult expected)
        {
            var transport = new FakeTransport { Status = status };
            Assert.Equal(expected, KeyCheck.Run(ClientFor(transport, new List<string>())));
            Assert.Equal("/v1/models", new Uri(transport.Requests[0].Url).AbsolutePath);
        }

        [Fact]
        public void KeyCheckReportsNoNetwork()
        {
            var transport = new FakeTransport { NoNetwork = true };
            Assert.Equal(KeyCheckResult.NoNetwork, KeyCheck.Run(ClientFor(transport, new List<string>())));
        }

        [Fact]
        public void KeyCheckNeverThrowsOnAWorkerThread()
        {
            var transport = new FakeTransport { Throw = true };
            Assert.Equal(KeyCheckResult.Unconfirmed, KeyCheck.Run(ClientFor(transport, new List<string>())));
        }

        [Fact]
        public void BackgroundRunsWorkOffThreadAndDeliversOnDrain()
        {
            var background = new Background();
            var callerThread = Environment.CurrentManagedThreadId;
            int workThread = -1, doneThread = -1;
            var delivered = new System.Threading.ManualResetEventSlim();

            background.Run(() => { workThread = Environment.CurrentManagedThreadId; return 7; },
                value => { doneThread = Environment.CurrentManagedThreadId; Assert.Equal(7, value); delivered.Set(); });

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!delivered.IsSet && DateTime.UtcNow < deadline)
            {
                background.Drain();
                System.Threading.Thread.Sleep(5);
            }

            Assert.True(delivered.IsSet);
            Assert.NotEqual(callerThread, workThread);
            Assert.Equal(callerThread, doneThread);
        }

        [Fact]
        public void TransportSourceNeverTouchesTlsSettings()
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "ClaudiaKermin.Core", "Claude", "HttpWebRequestTransport.cs");
            if (!System.IO.File.Exists(path)) return;
            var code = string.Join("\n", System.IO.File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("///")));
            Assert.DoesNotContain("SecurityProtocol", code);
        }
    }
}
