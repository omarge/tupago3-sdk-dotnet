using TuPago3.Sdk.Client;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Generated.V1;
using TuPago3.Sdk.Models;
using TuPago3.Sdk.Tests.TestInfra;
using Xunit;

namespace TuPago3.Sdk.Tests.Client;

/// <summary>T25 MonetaryValidation — monto validation rejects unsafe double/decimal inputs; valid values pass.</summary>
public class T25_MonetaryValidation
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(0.005)] // below minimum
    [InlineData(2_000_000_000.0)] // above maximum
    public void ValidateRejectsUnsafeDouble(double monto)
    {
        Assert.Throws<TupagoConfigException>(() => Money.Validate(monto));
    }

    [Fact]
    public void ValidateAcceptsValidDouble()
    {
        var ex = Record.Exception(() => Money.Validate(1234.56));
        Assert.Null(ex);
    }

    [Fact]
    public void FromDecimalRejectsOverflowAndNonPositive()
    {
        Assert.Throws<TupagoConfigException>(() => Money.FromDecimal(decimal.MinValue));
        Assert.Throws<TupagoConfigException>(() => Money.FromDecimal(0));
        Assert.Throws<TupagoConfigException>(() => Money.FromDecimal(-5));
    }

    [Fact]
    public void FromDecimalAcceptsAndConverts()
    {
        var d = Money.FromDecimal(99.99m);
        Assert.Equal(99.99d, d, 5);
    }

    [Fact]
    public async Task CreateQrRejectsInvalidMontoBeforeSend()
    {
        var servicer = new SimulatedPaymentService();
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address);

        await Assert.ThrowsAsync<TupagoConfigException>(() =>
            client.CreateQrAsync(new CreateQrRequest { IdCuenta = 1, Monto = double.NaN, IdempotencyKey = "m1" }));
        await Assert.ThrowsAsync<TupagoConfigException>(() =>
            client.CreateQrAsync(new CreateQrRequest { IdCuenta = 1, Monto = -10, IdempotencyKey = "m2" }));
        Assert.Equal(0, servicer.CreateQrCount);
    }
}
