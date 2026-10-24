// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class BackgroundTests
    {
        private static void DrainUntil(Background background, Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!done() && DateTime.UtcNow < deadline)
            {
                background.Drain();
                Thread.Sleep(5);
            }
        }

        [Fact]
        public void AnExceptionInWorkNeverEscapesTheWorkerThread()
        {
            var errors = new List<Exception>();
            Exception failed = null;
            var doneCalled = false;
            var background = new Background(errors.Add);

            background.Run<int>(() => throw new InvalidOperationException("boom"), v => doneCalled = true, e => failed = e);
            DrainUntil(background, () => failed != null);

            Assert.IsType<InvalidOperationException>(failed);
            Assert.False(doneCalled);
            Assert.IsType<InvalidOperationException>(Assert.Single(errors));
        }

        [Fact]
        public void AThrowingCallbackDoesNotStopTheRestOfTheDrain()
        {
            var errors = new List<Exception>();
            var background = new Background(errors.Add);
            var ran = new List<int>();

            background.Post(() => ran.Add(1));
            background.Post(() => throw new InvalidOperationException("callback"));
            background.Post(() => ran.Add(3));
            background.Drain();

            Assert.Equal(new[] { 1, 3 }, ran);
            Assert.Single(errors);
        }

        [Fact]
        public void PostedActionsRunInOrderBeforeTheResult()
        {
            var background = new Background();
            var order = new List<string>();
            background.Run(() =>
            {
                background.Post(() => order.Add("update 1"));
                background.Post(() => order.Add("update 2"));
                return "result";
            }, r => order.Add(r));
            DrainUntil(background, () => order.Count == 3);

            Assert.Equal(new[] { "update 1", "update 2", "result" }, order);
        }
    }
}
