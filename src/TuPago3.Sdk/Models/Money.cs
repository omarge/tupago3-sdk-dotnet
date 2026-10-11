using System.Globalization;
using TuPago3.Sdk.Errors;

namespace TuPago3.Sdk.Models;

/// <summary>
/// The wire Protobuf field `monto` is a <c>double</c>. double does NOT provide exact decimal
/// financial semantics. This helper accepts decimal input where reasonable, rejects unsafe values
/// explicitly and converts to double without silent precision loss or rounding.
/// </summary>
public static class Money
{
    // Documented SDK bounds for a single payment (MXN pesos). Do not invent commercial limits.
    public const double MinMonto = 0.01d;
    public const double MaxMonto = 1_000_000_000.0d;

    /// <summary>
    /// Validates a double monto: rejects NaN, Infinity, non-positive, sub-minimum and over-maximum.
    /// </summary>
    public static void Validate(double monto)
    {
        if (double.IsNaN(monto)) throw new TupagoConfigException("Monto", "monto is NaN");
        if (double.IsPositiveInfinity(monto) || double.IsNegativeInfinity(monto))
            throw new TupagoConfigException("Monto", "monto is Infinite");
        if (monto < MinMonto) throw new TupagoConfigException("Monto", $"monto below minimum allowed ({MinMonto})");
        if (monto > MaxMonto) throw new TupagoConfigException("Monto", $"monto above maximum allowed ({MaxMonto})");
    }

    /// <summary>
    /// Validates a decimal monto (exact input) and converts to double with overflow detection.
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
