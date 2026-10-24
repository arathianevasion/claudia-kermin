// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using System.IO;
using System.Text;

namespace ClaudiaKermin.Core.Claude
{
    /// <summary>
    /// Writes raw stream events to <c>request-&lt;session start&gt;-&lt;id&gt;.sse</c> in a folder, as received, with a comment line
    /// at the start of each attempt. A debug tool: only used when <c>debug_tools</c> is on.
    /// </summary>
    public sealed class FileStreamDump : IRawStreamDump
    {
        private readonly string _directory;
        // Request ids restart with every KSP session, so each session's files carry when it started.
        private readonly string _session = System.DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        private readonly object _gate = new object();
        private int _lastRequest = -1;
        private int _lastAttempt = -1;

        public FileStreamDump(string directory)
        {
            _directory = directory;
        }

        private string PathFor(int requestId)
        {
            return Path.Combine(_directory, "request-" + _session + "-" + requestId.ToString(CultureInfo.InvariantCulture) + ".sse");
        }

        public void Write(int requestId, int attempt, string rawEvent)
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var text = rawEvent;
                if (requestId != _lastRequest || attempt != _lastAttempt)
                {
                    text = ": attempt " + attempt.ToString(CultureInfo.InvariantCulture) + "\n" + text;
                    _lastRequest = requestId;
                    _lastAttempt = attempt;
                }
                File.AppendAllText(PathFor(requestId), text, new UTF8Encoding(false));
            }
        }
    }
}
