// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace ClaudiaKermin.Core.Claude
{
    /// <summary>
    /// Runs blocking work on a worker thread and queues its result for the main thread, which drains
    /// the queue each frame (ADR 0001).
    /// </summary>
    public sealed class Background
    {
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();

        /// <summary>Runs <paramref name="work"/> on a worker thread; <paramref name="done"/> runs on whoever calls <see cref="Drain"/>.</summary>
        public void Run<T>(Func<T> work, Action<T> done)
        {
            var thread = new Thread(() =>
            {
                var result = work();
                _mainThread.Enqueue(() => done(result));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>Call from the main thread each frame.</summary>
        public void Drain()
        {
            Action action;
            while (_mainThread.TryDequeue(out action))
                action();
        }
    }
}
