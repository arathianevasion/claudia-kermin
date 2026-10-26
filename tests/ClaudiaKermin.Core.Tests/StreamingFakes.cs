// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>What a scripted response does once its bytes run out.</summary>
    internal enum StreamEnd
    {
        /// <summary>Returns 0: the server closed the connection.</summary>
        Close,
        /// <summary>Blocks until aborted: no more bytes ever arrive.</summary>
        Stall,
        /// <summary>Throws IOException: the connection broke.</summary>
        Break,
    }

    /// <summary>One scripted reply: a status, headers, body bytes delivered in fixed chunks, and an ending.</summary>
    internal sealed class ScriptedResponse
    {
        public int Status = 200;
        public byte[] Body = new byte[0];
        public int ChunkSize = 4096;
        public StreamEnd End = StreamEnd.Close;
        public bool NoNetwork;
        /// <summary>When set, delivery pauses at this byte offset until <see cref="Gate"/> opens.</summary>
        public int PauseAt = -1;
        public readonly ManualResetEventSlim Gate = new ManualResetEventSlim();
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static ScriptedResponse Fixture(string name, int chunkSize = 4096, StreamEnd end = StreamEnd.Close)
        {
            return new ScriptedResponse { Body = Fixtures.Bytes(name), ChunkSize = chunkSize, End = end };
        }

        public static ScriptedResponse Error(int status, string errorType, string message, string retryAfter = null)
        {
            var response = new ScriptedResponse
            {
                Status = status,
                Body = Encoding.UTF8.GetBytes("{\"type\":\"error\",\"error\":{\"type\":\"" + errorType + "\",\"message\":\"" + message + "\"}}"),
            };
            if (retryAfter != null) response.Headers["retry-after"] = retryAfter;
            return response;
        }
    }

    internal static class Fixtures
    {
        public static byte[] Bytes(string name)
        {
            return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "streams", name));
        }

        public static string Text(string name)
        {
            return new UTF8Encoding(false).GetString(Bytes(name));
        }
    }

    /// <summary>Replays scripted responses in order through the real client; never touches the network.</summary>
    internal sealed class StreamingFakeTransport : IHttpTransport
    {
        private readonly Queue<ScriptedResponse> _responses = new Queue<ScriptedResponse>();
        public readonly List<HttpRequestSpec> Requests = new List<HttpRequestSpec>();

        public StreamingFakeTransport(params ScriptedResponse[] responses)
        {
            foreach (var response in responses) _responses.Enqueue(response);
        }

        public void Add(ScriptedResponse response)
        {
            lock (_responses) _responses.Enqueue(response);
        }

        public HttpResponseData Send(HttpRequestSpec request)
        {
            throw new InvalidOperationException("streaming fake only");
        }

        public IHttpStream Open(HttpRequestSpec request, CancellationToken cancel)
        {
            ScriptedResponse response;
            lock (_responses)
            {
                Requests.Add(request);
                if (_responses.Count == 0) throw new InvalidOperationException("no scripted response left");
                response = _responses.Dequeue();
            }
            cancel.ThrowIfCancellationRequested();
            if (response.NoNetwork) throw new NetworkUnavailableException("ConnectFailure", null);
            return new ScriptedStream(response);
        }
    }

    internal sealed class ScriptedStream : IHttpStream
    {
        private readonly ScriptedResponse _response;
        private readonly ManualResetEventSlim _aborted = new ManualResetEventSlim();
        private int _position;

        public ScriptedStream(ScriptedResponse response)
        {
            _response = response;
        }

        public int StatusCode
        {
            get { return _response.Status; }
        }

        public string Header(string name)
        {
            string value;
            return _response.Headers.TryGetValue(name, out value) ? value : null;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_aborted.IsSet) throw new IOException("aborted");
            if (_position == _response.PauseAt && !_response.Gate.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("test gate was never opened");
            var left = _response.Body.Length - _position;
            if (left > 0)
            {
                var n = Math.Min(Math.Min(count, left), _response.ChunkSize);
                if (_position < _response.PauseAt) n = Math.Min(n, _response.PauseAt - _position);
                Array.Copy(_response.Body, _position, buffer, offset, n);
                _position += n;
                return n;
            }
            switch (_response.End)
            {
                case StreamEnd.Stall:
                    if (!_aborted.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("test stall was never aborted");
                    throw new IOException("aborted");
                case StreamEnd.Break:
                    throw new IOException("connection reset");
                default:
                    return 0;
            }
        }

        public void Abort()
        {
            _aborted.Set();
        }

        public void Dispose()
        {
        }
    }

    internal sealed class RecordingDump : IRawStreamDump
    {
        public readonly StringBuilder Text = new StringBuilder();
        public readonly List<int> Attempts = new List<int>();

        public readonly List<string> Bodies = new List<string>();

        public void WriteRequest(int requestId, string body)
        {
            lock (Text) Bodies.Add(body);
        }

        public void Write(int requestId, int attempt, string rawEvent)
        {
            lock (Text)
            {
                Text.Append(rawEvent);
                Attempts.Add(attempt);
            }
        }
    }
}
