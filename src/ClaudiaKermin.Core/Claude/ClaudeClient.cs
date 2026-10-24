// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ClaudiaKermin.Core.Claude
{
    /// <summary>The client's stall, retry and backoff rules. Tests shorten the times; the mod uses the defaults.</summary>
    public sealed class StreamTiming
    {
        /// <summary>No bytes for this long mid-stream counts as a stall.</summary>
        public TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

        /// <summary>The first backoff before content; each later one doubles.</summary>
        public TimeSpan BackoffBase = TimeSpan.FromSeconds(1);

        public int RetriesBeforeContent = 3;
        public int RetriesAfterContent = 1;

        /// <summary>Waits between attempts; returns early when cancelled.</summary>
        public Action<TimeSpan, CancellationToken> Wait = (delay, cancel) => cancel.WaitHandle.WaitOne(delay);
    }

    /// <summary>
    /// The Claude client: pinned headers, one redacted log line per request, and streaming Messages
    /// requests with the spec's stall, retry and cancel rules. Blocking; run it through <see cref="Background"/>.
    /// </summary>
    public sealed class ClaudeClient
    {
        public const string BaseUrl = "https://api.anthropic.com";

        /// <summary>Pinned in code; changed only with a mod release.</summary>
        public const string ApiVersion = "2026-10-21";

        /// <summary>Pinned in code; changed only with a mod release.</summary>
        public const string BetaHeaders = "server-side-fallback-2026-07-01,compact-2026-01-12,mid-conversation-output-config-2026-07-01";

        public static string UserAgent
        {
            get { return "ClaudiaKermin/" + ModInfo.Version; }
        }

        private const string MessagesPath = "/v1/messages";
        private const int MaxErrorBodyBytes = 64 * 1024;

        private static int _nextRequestId;

        private readonly IHttpTransport _transport;
        private readonly Func<string> _apiKey;
        private readonly Action<string> _log;

        public StreamTiming Timing = new StreamTiming();

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
            AddCommonHeaders(request);

            var id = Interlocked.Increment(ref _nextRequestId);
            var timer = Stopwatch.StartNew();
            try
            {
                var response = _transport.Send(request);
                Log(id, request.Method, path, "status=" + response.StatusCode.ToString(CultureInfo.InvariantCulture), timer);
                return response;
            }
            catch (NetworkUnavailableException)
            {
                Log(id, request.Method, path, "status=no-network", timer);
                throw;
            }
        }

        /// <summary>
        /// Sends a streaming Messages request and blocks until it completes, fails or is cancelled.
        /// Never throws for network or API trouble; those come back as a failed <see cref="StreamResult"/>.
        /// </summary>
        /// <param name="listener">Told about each block and delta as it arrives, on this (worker) thread.</param>
        /// <param name="dump">Receives raw events when set (debug_tools only); null for none.</param>
        public StreamResult Stream(MessagesRequest request, Action<StreamUpdate> listener, CancellationToken cancel, IRawStreamDump dump = null)
        {
            listener = listener ?? (u => { });
            var id = Interlocked.Increment(ref _nextRequestId);
            var timer = Stopwatch.StartNew();
            var body = request.ToJson();
            var result = new StreamResult();
            var retriesBefore = 0;
            var retriesAfter = 0;

            while (true)
            {
                result.Attempts++;
                var attempt = RunAttempt(id, result.Attempts, body, listener, cancel, dump);
                result.HttpStatus = attempt.HttpStatus;
                result.ErrorType = attempt.ErrorType;
                result.ErrorMessage = attempt.ErrorMessage;
                result.Failure = attempt.Failure;

                if (attempt.Cancelled || cancel.IsCancellationRequested)
                {
                    result.Status = StreamStatus.Cancelled;
                    result.Failure = StreamFailure.None;
                    break;
                }
                if (attempt.Message != null)
                {
                    result.Status = StreamStatus.Completed;
                    result.Message = attempt.Message;
                    break;
                }

                result.Status = StreamStatus.Failed;
                TimeSpan delay;
                if (!attempt.Retryable) break;
                if (attempt.ContentStarted)
                {
                    if (retriesAfter >= Timing.RetriesAfterContent) break;
                    retriesAfter++;
                    delay = attempt.RetryAfter ?? Timing.BackoffBase;
                    listener(new StreamUpdate { Kind = StreamUpdateKind.Restarted });
                }
                else
                {
                    if (retriesBefore >= Timing.RetriesBeforeContent) break;
                    delay = attempt.RetryAfter ?? TimeSpan.FromTicks(Timing.BackoffBase.Ticks << retriesBefore);
                    retriesBefore++;
                }

                Timing.Wait(delay, cancel);
                if (cancel.IsCancellationRequested)
                {
                    result.Status = StreamStatus.Cancelled;
                    result.Failure = StreamFailure.None;
                    break;
                }
            }

            LogStream(id, request.Model, result, timer);
            return result;
        }

        private sealed class Attempt
        {
            public AssistantMessage Message;
            public bool Cancelled;
            public bool Retryable;
            public bool ContentStarted;
            public StreamFailure Failure;
            public int HttpStatus;
            public string ErrorType;
            public string ErrorMessage;
            public TimeSpan? RetryAfter;
        }

        private Attempt RunAttempt(int id, int number, string body, Action<StreamUpdate> listener, CancellationToken cancel, IRawStreamDump dump)
        {
            var attempt = new Attempt();
            var request = new HttpRequestSpec { Method = "POST", Url = BaseUrl + MessagesPath, Body = body };
            AddCommonHeaders(request);
            request.Headers.Add(new KeyValuePair<string, string>("anthropic-beta", BetaHeaders));
            request.Headers.Add(new KeyValuePair<string, string>("Content-Type", "application/json"));
            request.Headers.Add(new KeyValuePair<string, string>("Accept", "text/event-stream"));

            IHttpStream stream;
            try
            {
                stream = _transport.Open(request, cancel);
            }
            catch (OperationCanceledException)
            {
                attempt.Cancelled = true;
                return attempt;
            }
            catch (NetworkUnavailableException)
            {
                attempt.Cancelled = cancel.IsCancellationRequested;
                attempt.Failure = StreamFailure.NoNetwork;
                attempt.Retryable = true;
                return attempt;
            }

            // Both the error body and the event stream are read under the stall watchdog and cancel.
            using (stream)
            using (var watchdog = new StallWatchdog(stream, Timing.StallTimeout))
            using (cancel.Register(stream.Abort))
            {
                attempt.HttpStatus = stream.StatusCode;
                if (stream.StatusCode < 200 || stream.StatusCode >= 300)
                {
                    ReadError(stream, watchdog, attempt);
                    attempt.Cancelled = cancel.IsCancellationRequested;
                    return attempt;
                }
                ReadEvents(id, number, stream, watchdog, attempt, listener, cancel, dump);
            }
            return attempt;
        }

        private void ReadEvents(int id, int number, IHttpStream stream, StallWatchdog watchdog, Attempt attempt,
            Action<StreamUpdate> listener, CancellationToken cancel, IRawStreamDump dump)
        {
            var parser = new SseParser();
            var assembler = new StreamAssembler(listener, what => _log(RequestTag(id) + " ignored " + what));
            var buffer = new byte[8192];

            try
            {
                while (!assembler.Stopped)
                {
                    if (cancel.IsCancellationRequested) break;
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    watchdog.BytesArrived();
                    foreach (var sse in parser.Feed(buffer, read))
                    {
                        if (dump != null) Dump(dump, id, number, sse.Raw);
                        assembler.Apply(sse);
                        if (assembler.Stopped) break;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (StreamErrorEventException e)
            {
                attempt.Failure = e.ErrorType == "billing_error" ? StreamFailure.LowCredit : StreamFailure.StreamError;
                attempt.ErrorType = e.ErrorType;
                attempt.ErrorMessage = e.Message;
                attempt.Retryable = RetryableErrorType(e.ErrorType);
                attempt.ContentStarted = assembler.ContentStarted;
                return;
            }
            catch (BadStreamException)
            {
                attempt.Failure = StreamFailure.BadStream;
                attempt.Retryable = true;
                attempt.ContentStarted = assembler.ContentStarted;
                return;
            }

            attempt.ContentStarted = assembler.ContentStarted;
            if (cancel.IsCancellationRequested)
            {
                attempt.Cancelled = true;
                return;
            }
            if (assembler.Stopped)
            {
                attempt.Message = assembler.Message;
                return;
            }
            attempt.Failure = watchdog.Fired ? StreamFailure.Stalled : StreamFailure.EndedEarly;
            attempt.Retryable = true;
        }

        private static void ReadError(IHttpStream stream, StallWatchdog watchdog, Attempt attempt)
        {
            var status = stream.StatusCode;
            attempt.RetryAfter = ParseRetryAfter(stream.Header("retry-after"));
            string text = "";
            try
            {
                var bytes = new MemoryStream();
                var buffer = new byte[4096];
                int read;
                while (bytes.Length < MaxErrorBodyBytes && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    bytes.Write(buffer, 0, read);
                    watchdog.BytesArrived();
                }
                text = Encoding.UTF8.GetString(bytes.ToArray());
            }
            catch (IOException)
            {
            }

            try
            {
                var error = JsonReader.Parse(text)["error"];
                attempt.ErrorType = JsonNode.Str(error != null ? error["type"] : null);
                attempt.ErrorMessage = JsonNode.Str(error != null ? error["message"] : null);
            }
            catch (JsonException)
            {
            }

            if (status == 401 || status == 403)
                attempt.Failure = StreamFailure.Unauthorized;
            else if (attempt.ErrorMessage != null && attempt.ErrorMessage.IndexOf("credit balance", StringComparison.OrdinalIgnoreCase) >= 0)
                attempt.Failure = StreamFailure.LowCredit;
            else
            {
                attempt.Failure = StreamFailure.HttpStatus;
                attempt.Retryable = status == 429 || status >= 500;
            }
        }

        private static bool RetryableErrorType(string type)
        {
            switch (type)
            {
                case "invalid_request_error":
                case "authentication_error":
                case "permission_error":
                case "not_found_error":
                case "request_too_large":
                case "billing_error":
                    return false;
                default:
                    return true;
            }
        }

        private static TimeSpan? ParseRetryAfter(string header)
        {
            double seconds;
            if (header == null) return null;
            if (double.TryParse(header.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(Math.Min(seconds, 3600));
            DateTime when;
            if (DateTime.TryParse(header, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when))
            {
                var wait = when - DateTime.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
            return null;
        }

        private void Dump(IRawStreamDump dump, int id, int attempt, string raw)
        {
            try
            {
                dump.Write(id, attempt, raw);
            }
            catch (Exception)
            {
                // A debug dump must never break the request.
                _log(RequestTag(id) + " raw dump failed");
            }
        }

        private static string RequestTag(int id)
        {
            return ModInfo.LogTag + " request id=" + id.ToString(CultureInfo.InvariantCulture);
        }

        private void AddCommonHeaders(HttpRequestSpec request)
        {
            request.Headers.Add(new KeyValuePair<string, string>("x-api-key", _apiKey() ?? ""));
            request.Headers.Add(new KeyValuePair<string, string>("anthropic-version", ApiVersion));
            request.Headers.Add(new KeyValuePair<string, string>("User-Agent", UserAgent));
        }

        // Only ids, the path, model, status, timing and token counts: never headers, the key, or any request or response text.
        private void Log(int id, string method, string path, string status, Stopwatch timer)
        {
            _log(RequestTag(id) + " " + method + " " + path
                + " " + status + " ms=" + timer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
        }

        private void LogStream(int id, string model, StreamResult result, Stopwatch timer)
        {
            var line = new StringBuilder();
            line.Append("model=").Append(StreamAssembler.Safe(model));
            line.Append(" status=").Append(result.Status == StreamStatus.Completed ? "completed"
                : result.Status == StreamStatus.Cancelled ? "cancelled" : "failed");
            if (result.Status == StreamStatus.Failed) line.Append(" failure=").Append(result.Failure.ToString());
            if (result.HttpStatus != 0) line.Append(" http=").Append(result.HttpStatus.ToString(CultureInfo.InvariantCulture));
            if (result.ErrorType != null) line.Append(" error=").Append(StreamAssembler.Safe(result.ErrorType));
            line.Append(" attempts=").Append(result.Attempts.ToString(CultureInfo.InvariantCulture));
            if (result.Message != null)
            {
                var usage = result.Message.Usage;
                line.Append(" stop=").Append(StreamAssembler.Safe(result.Message.RawStopReason));
                if (result.Message.Model != model) line.Append(" served_by=").Append(StreamAssembler.Safe(result.Message.Model));
                line.Append(" in=").Append(usage.InputTokens.ToString(CultureInfo.InvariantCulture));
                line.Append(" out=").Append(usage.OutputTokens.ToString(CultureInfo.InvariantCulture));
                line.Append(" cache_read=").Append(usage.CacheReadInputTokens.ToString(CultureInfo.InvariantCulture));
                line.Append(" cache_write=").Append(usage.CacheCreationInputTokens.ToString(CultureInfo.InvariantCulture));
                line.Append(" iterations=").Append(usage.Iterations.Count.ToString(CultureInfo.InvariantCulture));
            }
            Log(id, "POST", MessagesPath, line.ToString(), timer);
        }

        /// <summary>Aborts the stream when no bytes arrive for the stall timeout.</summary>
        private sealed class StallWatchdog : IDisposable
        {
            private readonly IHttpStream _stream;
            private readonly long _limitTicks;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly Timer _timer;
            private readonly object _gate = new object();
            private long _lastBytes;
            private bool _disposed;
            private volatile bool _fired;

            public bool Fired
            {
                get { return _fired; }
            }

            public StallWatchdog(IHttpStream stream, TimeSpan limit)
            {
                _stream = stream;
                _limitTicks = limit.Ticks;
                var period = (int)Math.Max(10, Math.Min(1000, limit.TotalMilliseconds / 10));
                _timer = new Timer(Check, null, period, period);
            }

            public void BytesArrived()
            {
                Interlocked.Exchange(ref _lastBytes, _clock.Elapsed.Ticks);
            }

            private void Check(object state)
            {
                lock (_gate)
                {
                    if (_disposed || _fired) return;
                    if (_clock.Elapsed.Ticks - Interlocked.Read(ref _lastBytes) < _limitTicks) return;
                    _fired = true;
                    _stream.Abort();
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    _disposed = true;
                    _timer.Dispose();
                }
            }
        }
    }
}
