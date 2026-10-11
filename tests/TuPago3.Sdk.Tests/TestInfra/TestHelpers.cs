using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.Generated.V1;

namespace TuPago3.Sdk.Tests.TestInfra;

public static class TestSecrets
{
    // Synthetic dev secret for the in-process simulated server (NOT a production credential).
    public const string SimSecretBase64 =
        "MzM0ODYwZjI2MzFlNWE3NjAwNzZmZDViYWVjM2MyNDY1Y2JkYmVhMzA5ZTY0YzZjMTE2NTFmMmRmNGEzY2Q2MmQ2OGYyMjk1OGQ5ZjEyMjkxODY3YWEyNWYzMWZkMWQ0ZWE1ZDU4OTdkMDUxNWY5ZjU2NTBmNzIzYTUxNmQ0YTM=";

    // Dev backend3 JWT secret (lab only). Overridable via env; never a production credential.
    public static string BackendSecretBase64 =>
        Environment.GetEnvironmentVariable("TUPAGO_JWT_BASE64_SECRET") ?? SimSecretBase64;
}

public static class ClientFactory
{
    public static TupagoPaymentClient ForAddress(string address, string? token = null, bool useTls = false,
        string? caPem = null, string? serverName = null, TimeSpan? deadline = null)
    {
        return new TupagoPaymentClient(new TupagoClientOptions
        {
            Target = HostPort(address),
            UseTls = useTls,
            CaCertificatePem = caPem,
            ServerName = serverName,
            Credentials = token is null ? null : new StaticTokenProvider(token),
            DefaultDeadline = deadline ?? TimeSpan.FromSeconds(30),
        });
    }

    public static string HostPort(string address)
    {
        var uri = new Uri(address);
        return uri.Host + ":" + uri.Port;
    }
}
