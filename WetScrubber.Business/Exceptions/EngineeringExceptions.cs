using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Exceptions
{
    public sealed class IncompletePackingInputException : Exception
    {
        public IReadOnlyList<string> MissingFields { get; }

        public IncompletePackingInputException(IReadOnlyList<string> missingFields)
            : base("Mandatory packing/tower data missing or non-positive: " + string.Join(", ", missingFields))
        {
            MissingFields = missingFields;
        }
    }

    public sealed class PropertyOutOfBoundsException : Exception
    {
        public string Property { get; }
        public double Value { get; }
        public double Min { get; }
        public double Max { get; }

        public PropertyOutOfBoundsException(string property, double value, double min, double max)
            : base($"{property} = {value} is outside the valid range [{min}, {max}].")
        {
            Property = property;
            Value = value;
            Min = min;
            Max = max;
        }
    }

    public sealed class SolverNonConvergenceException : Exception
    {
        public int Iterations { get; }
        public double Residual { get; }

        public SolverNonConvergenceException(int iterations, double residual)
            : base($"Counter-current solver did not converge after {iterations} iterations (residual {residual:E3}).")
        {
            Iterations = iterations;
            Residual = residual;
        }
    }
}