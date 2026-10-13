using System.Globalization;
using TuPago3.Sdk.Errors;

namespace TuPago3.Sdk.Models;

/// <summary>
/// The wire Protobuf field `monto` is a <c>double</c>. double does NOT provide exact decimal
/// financial semantics. This helper rejects unsafe values (NaN, Infinity, non-positive) and
/// converts decimal input to double. Note that decimal-to-double conversion can lose precision
/// by design; callers that need exact decimal semantics must handle that outside the SDK.
/// Backend is the policy authority for commercial limits; the SDK does not invent them.
/// </summary>
public static class Money
{
    /// <summary>
    /// Validates a double monto: rejects NaN, Infinity and non-positive values.
    /// Does not enforce commercial min/max (those are backend policy).
    /// </summary>
    public static void Validate(double monto)
    {
        if (double.IsNaN(monto)) throw new TupagoConfigException("Monto", "monto is NaN");
        if (double.IsPositiveInfinity(monto) || double.IsNegativeInfinity(monto))
            throw new TupagoConfigException("Monto", "monto is Infinite");
        if (monto <= 0) throw new TupagoConfigException("Monto", "monto must be strictly positive");
    }

    /// <summary>
    /// Validates a decimal monto and converts to double with overflow detection.
    /// Precision may be lost; this is inherent to double.
    /// </summary>
    public static double FromDecimal(decimal monto)
    {
        if (monto <= 0) throw new TupagoConfigException("Monto", "monto must be strictly positive");
        var d = (double)monto;
        if (double.IsInfinity(d) || double.IsNaN(d))
            throw new TupagoConfigException("Monto", "monto overflows double representation");
        Validate(d);
        return d;
    }

    /// <summary>
    /// Parses a culture-invariant string (e.g. "1234.56") and validates.
    /// </summary>
    public static double Parse(string monto)
    {
        if (!double.TryParse(monto, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            throw new TupagoConfigException("Monto", $"monto is not a valid number: '{monto}'");
        Validate(d);
        return d;
    }
}
