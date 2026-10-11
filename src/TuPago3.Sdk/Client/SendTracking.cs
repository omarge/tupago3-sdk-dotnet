using System.Net.Http;

namespace TuPago3.Sdk.Client;

/// <summary>
/// Per-call wire-send tracker. Set into <see cref="SendTracker.Current"/> immediately before a
/// Create RPC; the <see cref="SendTrackingHandler"/> records when request bytes are dispatched to
/// the socket. This distinguishes "not sent" from "indeterminate result" after the request left
/// the process. The handler only records a boolean and never alters TLS validation.
/// </summary>
public sealed class SendTracker
{
    public bool Dispatched { get; set; }

    public static readonly AsyncLocal<SendTracker?> Current = new();
}

/// <summary>
/// Delegating handler that records dispatch of the HTTP/2 request. It has NO certificate-validation
/// logic and cannot downgrade TLS; it simply delegates to the underlying SocketsHttpHandler.
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
