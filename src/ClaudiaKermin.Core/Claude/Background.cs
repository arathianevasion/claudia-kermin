// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace ClaudiaKermin.Core.Claude
{
    /// <summary>
    /// Runs blocking work on a worker thread and queues its result for the main thread, which drains
    /// the queue each frame (ADR 0001). No exception escapes a worker thread (an unhandled one would
    /// take KSP down) or stops a drain part-way: each goes to the error handler instead.
    /// </summary>
    public sealed class Background
    {
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private readonly Action<Exception> _onError;

        /// <param name="onError">Told, on the main thread, about any exception from work or a callback; null to drop them.</param>
        public Background(Action<Exception> onError = null)
        {
            _onError = onError ?? (e => { });
        }

        /// <summary>Runs <paramref name="work"/> on a worker thread; <paramref name="done"/> runs on whoever calls <see cref="Drain"/>.</summary>
        /// <param name="failed">Runs on the drain thread instead of <paramref name="done"/> if the work throws; the error handler also hears of it.</param>
        public void Run<T>(Func<T> work, Action<T> done, Action<Exception> failed = null)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var result = work();
                    _mainThread.Enqueue(() => done(result));
                }
                catch (Exception e)
                {
                    // Two queued actions, so a throwing error handler cannot stop the caller hearing of the failure.
                    _mainThread.Enqueue(() => _onError(e));
                    if (failed != null) _mainThread.Enqueue(() => failed(e));
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>Queues <paramref name="action"/> for the next <see cref="Drain"/>; safe from any thread.</summary>
        public void Post(Action action)
        {
            _mainThread.Enqueue(action);
        }

        /// <summary>Call from the main thread each frame.</summary>
        public void Drain()
        {
            Action action;
            while (_mainThread.TryDequeue(out action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    try
                    {
                        _onError(e);
                    }
                    catch (Exception)
                    {
                        // Nothing left to report it to.
                    }
                }
            }
        }
    }
}
