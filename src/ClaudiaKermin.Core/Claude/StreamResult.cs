// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKermin.Core.Claude
{
    public enum StreamStatus
    {
        Completed,
        /// <summary>The caller cancelled: nothing from this request is appended anywhere.</summary>
        Cancelled,
        Failed,
    }

    public enum StreamFailure
    {
        None,
        /// <summary>No response at all (DNS, connect, connect timeout).</summary>
        NoNetwork,
        /// <summary>60 s passed with no bytes.</summary>
        Stalled,
        /// <summary>The connection closed before <c>message_stop</c>.</summary>
        EndedEarly,
        /// <summary>The stream sent an <c>error</c> event.</summary>
        StreamError,
        /// <summary>A known event could not be read.</summary>
        BadStream,
        /// <summary>401 or 403: the key was rejected.</summary>
        Unauthorized,
        /// <summary>The account's credit balance is too low.</summary>
        LowCredit,
        /// <summary>Any other non-2xx status.</summary>
        HttpStatus,
    }

    public sealed class StreamResult
    {
        public StreamStatus Status;
        public StreamFailure Failure;
        /// <summary>The last HTTP status seen, or 0 when none arrived.</summary>
        public int HttpStatus;
        /// <summary>The API's error type (e.g. overloaded_error), when it gave one.</summary>
        public string ErrorType;
        /// <summary>The API's error message, for showing the player; never logged.</summary>
        public string ErrorMessage;
        public int Attempts;
        /// <summary>The reply, when <see cref="Status"/> is <see cref="StreamStatus.Completed"/>.</summary>
        public AssistantMessage Message;
    }

    /// <summary>Receives every raw stream event as text, for debugging (behind <c>debug_tools</c>).</summary>
    public interface IRawStreamDump
    {
        void Write(int requestId, int attempt, string rawEvent);
    }
}
