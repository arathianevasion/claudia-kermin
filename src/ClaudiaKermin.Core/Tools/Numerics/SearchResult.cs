// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKermin.Core.Tools.Numerics
{
    /// <summary>Why a search ended.</summary>
    internal enum SearchStatus
    {
        /// <summary>It reached its tolerance: <see cref="SearchResult.X"/> is the answer.</summary>
        Converged,
        /// <summary>The interval's ends have the same sign, so it holds no root to find.</summary>
        NotBracketed,
        /// <summary>It ran out of iterations; <see cref="SearchResult.X"/> is the best point so far, not an answer.</summary>
        IterationCap,
        /// <summary>The function returned NaN or infinity.</summary>
        NonFinite,
        /// <summary>A bound, tolerance or cap was not usable; the function was never called.</summary>
        InvalidInput,
        /// <summary>Newton's step hit a zero or non-finite derivative.</summary>
        FlatDerivative,
    }

    /// <summary>
    /// What a search found. A search never throws or guesses: anything but <see cref="SearchStatus.Converged"/> carries a
    /// <see cref="Reason"/> in words for the caller to pass on.
    /// </summary>
    internal sealed class SearchResult
    {
        public SearchStatus Status { get; private set; }
        public string Reason { get; private set; }
        /// <summary>The root or minimiser; the best point reached when the search did not converge, NaN when it never got going.</summary>
        public double X { get; private set; }
        /// <summary>The function at <see cref="X"/>.</summary>
        public double Fx { get; private set; }
        /// <summary>How many times the function was called.</summary>
        public int Evaluations { get; private set; }

        public bool Converged
        {
            get { return Status == SearchStatus.Converged; }
        }

        public static SearchResult Of(SearchStatus status, string reason, double x, double fx, int evaluations)
        {
            return new SearchResult { Status = status, Reason = reason, X = x, Fx = fx, Evaluations = evaluations };
        }

        public static SearchResult Invalid(string reason)
        {
            return Of(SearchStatus.InvalidInput, reason, double.NaN, double.NaN, 0);
        }

        public static SearchResult Done(double x, double fx, int evaluations)
        {
            return Of(SearchStatus.Converged, null, x, fx, evaluations);
        }
    }

    /// <summary>The input checks the searches share, run in every build.</summary>
    internal static class SearchInput
    {
        public static bool Finite(double x)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x);
        }

        /// <summary>The first thing wrong with the arguments, or null.</summary>
        public static string Problem(string method, double a, double b, double tol, int maxIter)
        {
            if (!Finite(a) || !Finite(b)) return method + ": the bounds must be finite";
            return Problem(method, tol, maxIter);
        }

        public static string Problem(string method, double tol, int maxIter)
        {
            if (!Finite(tol) || tol <= 0) return method + ": the tolerance must be a positive number";
            if (maxIter < 1) return method + ": the iteration cap must be at least 1";
            return null;
        }
    }
}
