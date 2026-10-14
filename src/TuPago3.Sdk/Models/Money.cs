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
    /// Smallest representable, non-lossy monetary magnitude (one centavo). Values below this are
    /// unsafe for financial amounts and are rejected.
    /// </summary>
    public const double MinMonto = 0.01;

    /// <summary>
    /// Largest accepted monetary magnitude. Values at or above this are rejected as unsafe.
    /// The backend remains the authority for commercial limits; this is a fail-closed safety bound.
    /// </summary>
    public const double MaxMonto = 1_000_000_000.0;

    /// <summary>
    /// Validates a double monto: rejects NaN, Infinity, non-positive, sub-cent and oversized values.
    /// The backend remains the authority for commercial limits; these are fail-closed safety bounds.
    /// </summary>
    public static void Validate(double monto)
    {
        if (double.IsNaN(monto)) throw new TupagoConfigException("Monto", "monto is NaN");
        if (double.IsPositiveInfinity(monto) || double.IsNegativeInfinity(monto))
            throw new TupagoConfigException("Monto", "monto is Infinite");
        if (monto <= 0) throw new TupagoConfigException("Monto", "monto must be strictly positive");
        if (monto < MinMonto) throw new TupagoConfigException("Monto", $"monto must be at least {MinMonto}");
        if (monto >= MaxMonto) throw new TupagoConfigException("Monto", $"monto must be below {MaxMonto}");
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
