using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

namespace Talkty.App.Services.Engines;

/// <summary>
/// Measures when the request body has been handed to the HTTP transport. This is not a server
/// acknowledgement: socket buffering means the remaining wait can still include network transfer.
/// </summary>
internal sealed class CloudRequestContent : HttpContent
{
    private readonly byte[] _body;
    private readonly Stopwatch _clock;
    internal long? BodyWrittenMs { get; private set; }

    internal CloudRequestContent(byte[] body, Stopwatch clock)
    {
        _body = body;
        _clock = clock;
        Headers.ContentType = new("application/json") { CharSet = "utf-8" };
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _body.Length;
        return true;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(_body, cancellationToken).ConfigureAwait(false);
        BodyWrittenMs = _clock.ElapsedMilliseconds;
    }
}
