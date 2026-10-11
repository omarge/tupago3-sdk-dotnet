using Google.Protobuf;
using Google.Protobuf.Reflection;
using TuPago3.Sdk.Generated.V1;
using Xunit;

namespace TuPago3.Sdk.Tests.Contract;

/// <summary>
/// T01 SixRpcContract — the service descriptor and client expose exactly the six required RPCs.
/// </summary>
public class T01_SixRpcContract
{
    [Fact]
    public void ServiceDescriptorExposesSixRpcs()
    {
        var methods = PaymentService.Descriptor.Methods;
        Assert.Equal(6, methods.Count);
        var names = methods.Select(m => m.Name).ToHashSet();
        foreach (var expected in new[] { "GetCliente", "GetCatalogo", "GetQrStatus", "GetTelefonicoStatus", "CreateQr", "CreateTelefonico" })
            Assert.Contains(expected, names);
    }

    [Fact]
    public void ClientExposesSixAsyncMethods()
    {
        var methods = typeof(PaymentService.PaymentServiceClient)
            .GetMethods()
            .Where(m => m.Name.EndsWith("Async"))
            .Select(m => m.Name)
            .ToHashSet();
        foreach (var expected in new[] { "GetClienteAsync", "GetCatalogoAsync", "GetQrStatusAsync", "GetTelefonicoStatusAsync", "CreateQrAsync", "CreateTelefonicoAsync" })
            Assert.Contains(expected, methods);
    }
}

/// <summary>
/// T02 ProtobufWireEquivalence — identical messages serialize to identical bytes; field tags match
/// the descriptor; round-trip preserves values.
/// </summary>
public class T02_ProtobufWireEquivalence
{
    [Fact]
    public void IdenticalMessagesProduceIdenticalBytes()
    {
        var a = new CreateQrRequest
        {
            IdCuenta = 7,
            Monto = 1234.56,
            Concepto = "pago",
            ReferenciaNumerica = 99,
            NombreCliente = "Ana",
            Email = "a@b.com",
            TipoPago = 1,
            Vigencia = 60,
            IdempotencyKey = "key-xyz",
        };
        var b = new CreateQrRequest
        {
            IdCuenta = 7,
            Monto = 1234.56,
            Concepto = "pago",
            ReferenciaNumerica = 99,
            NombreCliente = "Ana",
            Email = "a@b.com",
            TipoPago = 1,
            Vigencia = 60,
            IdempotencyKey = "key-xyz",
        };
        Assert.Equal(a.ToByteArray(), b.ToByteArray());
    }

    [Fact]
    public void FieldTagsMatchDescriptor()
    {
        var desc = CreateQrRequest.Descriptor.Fields;
        Assert.Equal(1, desc[CreateQrRequest.IdCuentaFieldNumber].FieldNumber);
        Assert.Equal(2, desc[CreateQrRequest.MontoFieldNumber].FieldNumber);
        Assert.Equal(9, desc[CreateQrRequest.IdempotencyKeyFieldNumber].FieldNumber);
        Assert.Equal(FieldType.Double, desc[CreateQrRequest.MontoFieldNumber].FieldType);
    }

    [Fact]
    public void RoundTripPreservesAllFields()
    {
        var a = new CreateTelefonicoRequest
        {
            Key = "k",
            Monto = 50.25,
            Concepto = "c",
            ReferenciaNumerica = 1,
            NumeroCelular = "55119999",
            Vigencia = 30,
            IdempotencyKey = "idk",
        };
        var parsed = CreateTelefonicoRequest.Parser.ParseFrom(a.ToByteArray());
        Assert.Equal(a, parsed);
    }
}

/// <summary>
/// T03 Int64Preservation — int64 fields survive the wire without truncation or widening.
/// </summary>
public class T03_Int64Preservation
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Int64RoundTripsExactly(long value)
    {
        var req = new GetClienteRequest { Id = value };
        var parsed = GetClienteRequest.Parser.ParseFrom(req.ToByteArray());
        Assert.Equal(value, parsed.Id);

        var status = new GetStatusRequest { IdCuenta = value };
        var p2 = GetStatusRequest.Parser.ParseFrom(status.ToByteArray());
        Assert.Equal(value, p2.IdCuenta);
    }
}

/// <summary>
/// T04 JsonResponsePassthrough — the raw JSON string is preserved verbatim across the wire.
/// </summary>
public class T04_JsonResponsePassthrough
{
    [Fact]
    public void JsonIsPreservedVerbatim()
    {
        const string json = "{\"status\":\"PENDING\",\"idc\":\"ABC-123\",\"nested\":{\"x\":1}}";
        var resp = new JsonResponse { Json = json };
        var parsed = JsonResponse.Parser.ParseFrom(resp.ToByteArray());
        Assert.Equal(json, parsed.Json);
    }
}
