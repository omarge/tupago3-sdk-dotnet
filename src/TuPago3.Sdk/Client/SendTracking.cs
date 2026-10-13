using System.Net.Http;

namespace TuPago3.Sdk.Client;

/// <summary>
/// Per-call send tracker. Set into <see cref="SendTracker.Current"/> immediately before a
/// Create RPC. The <see cref="SendTrackingHandler"/> records that the request was handed to the
/// next handler. This is a conservative signal: it does NOT prove that request bytes were
/// actually placed on the wire. False UnknownOutcome is acceptable; claiming "definitely not sent"
/// after possible dispatch is not.
/// </summary>
public sealed class SendTracker
{
    public bool Dispatched { get; set; }

    public static readonly AsyncLocal<SendTracker?> Current = new();
}

/// <summary>
/// Delegating handler that records that the request was passed to the next handler.
/// It has NO certificate-validation logic and cannot downgrade TLS; it simply delegates
/// to the underlying SocketsHttpHandler.
/// </summary>
internal sealed class SendTrackingHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var tracker = SendTracker.Current.Value;
        if (tracker is not null)
            tracker.Dispatched = true;
        return base.SendAsync(request, cancellationToken);
    }
}
