// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Config
{
    /// <summary>USD per million tokens.</summary>
    public struct ModelPrice
    {
        public decimal InputPerMTok;
        public decimal OutputPerMTok;

        public ModelPrice(decimal input, decimal output)
        {
            InputPerMTok = input;
            OutputPerMTok = output;
        }
    }

    /// <summary>
    /// What each model costs, from the player config. A reply's cost is the sum over the attempts in its
    /// <c>usage.iterations</c> (compaction, the message, a fallback model's message), each priced at its own
    /// model's rate; a model that is not in the table is charged at the table's highest price, so cost is
    /// never underestimated. The mod writes every cache breakpoint with the 1-hour TTL, so a cache write
    /// costs twice the input price and a cache read a tenth of it.
    /// </summary>
    public sealed class PriceTable
    {
        public const decimal CacheWriteMultiplier = 2m;
        public const decimal CacheReadMultiplier = 0.1m;

        /// <summary>A Mission's typical load, for the start form's estimate: about $2.40 on Sonnet 5.5, about $5 on Opus 5.5 (a Mun landing and return).</summary>
        public const long ReferenceMissionInputTokens = 700000;
        public const long ReferenceMissionOutputTokens = 100000;

        private readonly Dictionary<string, ModelPrice> _prices = new Dictionary<string, ModelPrice>();

        /// <summary>The shipped table: Sonnet 5.5, Opus 5.5 and the fallback models.</summary>
        public static PriceTable Default()
        {
            var table = new PriceTable();
            table.Set("claude-sonnet-5-5", new ModelPrice(2m, 10m));
            table.Set("claude-opus-5-5", new ModelPrice(4m, 20m));
            table.Set("claude-sonnet-5", new ModelPrice(2m, 10m));
            table.Set("claude-opus-5", new ModelPrice(5m, 25m));
            table.Set("claude-opus-4-8", new ModelPrice(5m, 25m));
            table.Set("claude-fable-5-1", new ModelPrice(10m, 50m));
            return table;
        }

        public void Set(string model, ModelPrice price)
        {
            _prices[model] = price;
        }

        public IEnumerable<string> Models
        {
            get { return _prices.Keys; }
        }

        public bool Knows(string model)
        {
            return model != null && _prices.ContainsKey(model);
        }

        /// <summary>The model's price; the table's highest input and highest output price for an unknown model.</summary>
        public ModelPrice PriceOf(string model)
        {
            ModelPrice price;
            if (model != null && _prices.TryGetValue(model, out price)) return price;
            return new ModelPrice(_prices.Values.Max(p => p.InputPerMTok), _prices.Values.Max(p => p.OutputPerMTok));
        }

        /// <summary>
        /// The real cost of a reply: each iteration at its own model's rate (the request's model when the API
        /// names none); with no iterations, the reply's top-level totals at the model that produced it.
        /// </summary>
        public decimal CostOf(Usage usage, string model)
        {
            if (usage.Iterations.Count == 0)
                return Cost(model, usage.InputTokens, usage.CacheCreationInputTokens, usage.CacheReadInputTokens, usage.OutputTokens);
            decimal total = 0m;
            foreach (var iteration in usage.Iterations)
                total += Cost(iteration.Model ?? model, iteration.InputTokens, iteration.CacheCreationInputTokens, iteration.CacheReadInputTokens, iteration.OutputTokens);
            return total;
        }

        /// <summary>The most a request could cost: every estimated input token as a cache write, and a full <paramref name="maxTokens"/> of output.</summary>
        public decimal WorstCase(string model, long estimatedInputTokens, int maxTokens)
        {
            return Cost(model, 0, estimatedInputTokens, 0, maxTokens);
        }

        /// <summary>The start form's estimate of a whole Mission on this model.</summary>
        public decimal EstimateMission(string model)
        {
            return Cost(model, ReferenceMissionInputTokens, 0, 0, ReferenceMissionOutputTokens);
        }

        private decimal Cost(string model, long input, long cacheWrite, long cacheRead, long output)
        {
            var price = PriceOf(model);
            return (input * price.InputPerMTok
                + cacheWrite * price.InputPerMTok * CacheWriteMultiplier
                + cacheRead * price.InputPerMTok * CacheReadMultiplier
                + output * price.OutputPerMTok) / 1000000m;
        }
    }
}
