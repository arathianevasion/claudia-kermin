// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Chat
{
    public enum ChatSpeaker
    {
        Player,
        Claudia,
        /// <summary>A line from the mod itself (errors, cancellations, "answered by").</summary>
        Notice,
    }

    public sealed class ChatLine
    {
        public ChatSpeaker Speaker;
        public string Text = "";
        /// <summary>Claudia's reply is still arriving.</summary>
        public bool Streaming;
        /// <summary>Claudia is thinking and has written no text yet.</summary>
        public bool Thinking;
    }

    /// <summary>
    /// Free-text chat with Claudia outside any Mission: the state behind the chat window. Replies
    /// stream in on a worker thread and are applied here on the main thread when
    /// <see cref="Background.Drain"/> runs, so the chat window only draws <see cref="Lines"/> and
    /// forwards clicks. It lives as long as the game, so a reply survives a scene change.
    /// </summary>
    public sealed class ChatSession
    {
        public const string DefaultModel = "claude-sonnet-5-5";
        public const int MaxTokens = 16000;

        public const string SystemPrompt =
            "You are Claudia, a Kerbal and the mission controller in this game of Kerbal Space Program (KSP), "
            + "where kOS is the in-game scripting language. Your crew role is Super Intelligence: Pilot, Engineer and "
            + "Scientist in one. The player is chatting with you in the game. There is no Mission yet, so you cannot see "
            + "or fly their craft yet. Answer as Claudia, briefly and plainly: a few sentences unless they ask for more. "
            + "The home planet is Kerbin.";

        /// <summary>Claudia's answer when every model declines a chat question.</summary>
        public const string RefusalLine = "I'd better not help with that one. Ask me anything about flying, though.";

        private readonly ClaudeClient _client;
        private readonly Background _background;
        private readonly Func<bool> _hasKey;
        private readonly List<ChatLine> _lines = new List<ChatLine>();
        private readonly List<Message> _conversation = new List<Message>();

        private Pending _pending;
        private string _lastUnanswered;

        /// <param name="hasKey">True when an API key is set, checked before each send.</param>
        public ChatSession(ClaudeClient client, Background background, Func<bool> hasKey)
        {
            _client = client;
            _background = background;
            _hasKey = hasKey;
        }

        public IList<ChatLine> Lines
        {
            get { return _lines.AsReadOnly(); }
        }

        /// <summary>The conversation sent to Claude: answered turns only, appended and never edited.</summary>
        public IList<Message> Conversation
        {
            get { return _conversation.AsReadOnly(); }
        }

        public bool Busy
        {
            get { return _pending != null; }
        }

        /// <summary>The last message got no answer (cancelled or failed) and can be sent again.</summary>
        public bool CanRetry
        {
            get { return !Busy && _lastUnanswered != null; }
        }

        /// <summary>Goes up whenever <see cref="Lines"/> change, so the window can scroll to the end.</summary>
        public int Version { get; private set; }

        /// <summary>Raw stream events go here when set; only the debug_tools option sets it.</summary>
        public IRawStreamDump RawDump;

        /// <summary>The model for chat outside a Mission; the Loader sets the player config's default.</summary>
        public string Model = DefaultModel;

        /// <summary>From the player config.</summary>
        public int CompactionTriggerTokens = 150000;

        /// <summary>Sends the player's message. False when it was empty, a reply is in progress or there is no key.</summary>
        public bool Send(string text)
        {
            text = text == null ? "" : text.Trim();
            if (text.Length == 0 || Busy) return false;
            if (!_hasKey())
            {
                AddLine(ChatSpeaker.Notice, "Add your Anthropic API key in the Claudia Kermin settings first.");
                return false;
            }
            AddLine(ChatSpeaker.Player, text);
            Start(text);
            return true;
        }

        public void Retry()
        {
            if (!CanRetry) return;
            Start(_lastUnanswered);
        }

        /// <summary>Aborts the reply in progress. Nothing from it is added to the conversation.</summary>
        public void Cancel()
        {
            if (_pending != null) _pending.CancelSource.Cancel();
        }

        /// <summary>
        /// Starts a new conversation, as restarting KSP would: no lines, no message history. A reply in progress is
        /// cancelled and dropped when it lands. Only the basic chat does this; a Mission's context is never cleared.
        /// </summary>
        public void Clear()
        {
            var pending = _pending;
            _pending = null;
            if (pending != null)
            {
                pending.CancelSource.Cancel();
                pending.CancelSource.Dispose();
            }
            _lines.Clear();
            _conversation.Clear();
            _lastUnanswered = null;
            Version++;
        }

        private sealed class Pending
        {
            public string Text;
            public ChatLine Line;
            public CancellationTokenSource CancelSource;
        }

        private void Start(string text)
        {
            var request = new MessagesRequest { Model = Model, MaxTokens = MaxTokens, Effort = "low", CompactionTriggerTokens = CompactionTriggerTokens };
            request.System.Add(SystemPrompt);
            request.Messages.AddRange(_conversation);
            request.Messages.Add(Message.UserText(text));

            var pending = new Pending
            {
                Text = text,
                Line = AddLine(ChatSpeaker.Claudia, ""),
                CancelSource = new CancellationTokenSource(),
            };
            pending.Line.Streaming = true;
            _pending = pending;
            _lastUnanswered = null;

            var dump = RawDump;
            _background.Run(
                () => _client.Stream(request, update => _background.Post(() => Apply(pending, update)), pending.CancelSource.Token, dump),
                result => Finish(pending, result),
                error => Crashed(pending));
        }

        private void Apply(Pending pending, StreamUpdate update)
        {
            if (pending != _pending) return;
            var line = pending.Line;
            switch (update.Kind)
            {
                case StreamUpdateKind.Restarted:
                    line.Text = "";
                    line.Thinking = false;
                    break;
                case StreamUpdateKind.BlockStarted:
                    if (update.BlockType == "thinking" && line.Text.Length == 0) line.Thinking = true;
                    break;
                case StreamUpdateKind.TextDelta:
                    line.Thinking = false;
                    line.Text += update.Text;
                    break;
                default:
                    return;
            }
            Version++;
        }

        private void Finish(Pending pending, StreamResult result)
        {
            if (!Settle(pending)) return;
            switch (result.Status)
            {
                case StreamStatus.Cancelled:
                    var partial = pending.Line.Text;
                    Unanswered(pending, partial.Length == 0 ? "Cancelled." : partial + " [cancelled]");
                    break;
                case StreamStatus.Failed:
                    Unanswered(pending, Describe(result));
                    break;
                default:
                    Completed(pending, result.Message);
                    break;
            }
            Version++;
        }

        private void Completed(Pending pending, AssistantMessage message)
        {
            var line = pending.Line;
            var text = message.Text;
            if (message.StopReason == StopReason.Refusal)
            {
                line.Text = RefusalLine;
                return;
            }

            var answered = (message.StopReason == StopReason.EndTurn || message.StopReason == StopReason.MaxTokens) && text.Trim().Length > 0;
            if (!answered)
            {
                Unanswered(pending, message.StopReason == StopReason.ModelContextWindowExceeded
                    ? "This chat has grown too long for Claudia to read. Clear it to start a new conversation."
                    : "Claudia gave no answer (" + (message.RawStopReason ?? "no stop reason") + ").");
                return;
            }

            _conversation.Add(Message.UserText(pending.Text));
            _conversation.Add(message.ToMessage());
            line.Text = text;
            if (message.StopReason == StopReason.MaxTokens)
                AddLine(ChatSpeaker.Notice, "Claudia's reply was cut short.");
            if (message.FallbackRan && message.Model != null)
                AddLine(ChatSpeaker.Notice, "Answered by " + message.Model + ".");
        }

        private void Crashed(Pending pending)
        {
            if (!Settle(pending)) return;
            Unanswered(pending, "Something went wrong getting Claudia's reply; see KSP.log.");
            Version++;
        }

        /// <summary>Ends the pending reply; false when it was already superseded.</summary>
        private bool Settle(Pending pending)
        {
            if (pending != _pending) return false;
            _pending = null;
            pending.Line.Streaming = false;
            pending.Line.Thinking = false;
            pending.CancelSource.Dispose();
            return true;
        }

        /// <summary>Turns the reply line into a notice; the player's message can be sent again.</summary>
        private void Unanswered(Pending pending, string notice)
        {
            pending.Line.Speaker = ChatSpeaker.Notice;
            pending.Line.Text = notice;
            _lastUnanswered = pending.Text;
        }

        private static string Describe(StreamResult result)
        {
            switch (result.Failure)
            {
                case StreamFailure.Unauthorized:
                    return "Anthropic rejected the API key. Check it in the Claudia Kermin settings.";
                case StreamFailure.LowCredit:
                    return "Your Anthropic credit balance is too low for Claudia to answer.";
                case StreamFailure.NoNetwork:
                    return "No network: Claudia could not be reached. Retry when you are back online.";
                case StreamFailure.Stalled:
                    return "Claudia's reply stalled. Retry in a moment.";
                case StreamFailure.HttpStatus:
                    return "Claudia could not answer (HTTP " + result.HttpStatus.ToString(CultureInfo.InvariantCulture)
                        + (result.ErrorMessage != null ? ": " + result.ErrorMessage : "") + ").";
                case StreamFailure.StreamError:
                    return "Claudia's reply broke off (" + (result.ErrorType ?? "error") + "). Retry in a moment.";
                default:
                    return "Claudia's reply was cut off. Retry in a moment.";
            }
        }

        private ChatLine AddLine(ChatSpeaker speaker, string text)
        {
            var line = new ChatLine { Speaker = speaker, Text = text };
            _lines.Add(line);
            Version++;
            return line;
        }
    }
}
