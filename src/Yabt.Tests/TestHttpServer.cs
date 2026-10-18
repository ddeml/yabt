using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Yabt.Tests;

/// <summary>A loopback server recording small, ASCII-only provider test requests.</summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new(TimeSpan.FromSeconds(30));
    private readonly ConcurrentQueue<(string Method, string Target, IReadOnlyDictionary<string, string> Headers)> _requests = new();
    private readonly Task _serverTask;

    public TestHttpServer(int statusCode, string content = "")
    {
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/");
        _serverTask = ServeAsync(statusCode, content);
    }

    public Uri Endpoint { get; }

    public IReadOnlyList<(string Method, string Target, IReadOnlyDictionary<string, string> Headers)> Requests =>
        _requests.ToArray();

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();
        try
        {
            await _serverTask;
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException and not SocketException) { throw; }
            Debug.WriteLine(ex);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task ServeAsync(int statusCode, string content)
    {
        var cancellationToken = _shutdown.Token;
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken) ??
                throw new IOException("The test HTTP request did not contain a request line.");
            var requestParts = requestLine.Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } header)
            {
                var separator = header.IndexOf(':');
                headers.Add(header[..separator], header[(separator + 1)..].Trim());
            }

            if (headers.TryGetValue("Content-Length", out var contentLengthValue))
            {
                var contentLength = int.Parse(contentLengthValue, CultureInfo.InvariantCulture);
                var body = new char[contentLength];
                if (contentLength > 0 && await reader.ReadBlockAsync(body.AsMemory(), cancellationToken) != contentLength)
                {
                    throw new IOException("The test HTTP request body ended early.");
                }
            }

            _requests.Enqueue((requestParts[0], requestParts[1], headers));
            var bodyBytes = requestParts[0] == "HEAD" ? [] : Encoding.UTF8.GetBytes(content);
            var responseHeaders = Encoding.ASCII.GetBytes
            (
                $"HTTP/1.1 {statusCode} Test response\r\n" +
                "Connection: close\r\nContent-Type: application/xml\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "ETag: \"test-etag\"\r\nLast-Modified: Mon, 01 Jan 2024 00:00:00 GMT\r\n\r\n"
            );
            await stream.WriteAsync(responseHeaders, cancellationToken);
            await stream.WriteAsync(bodyBytes, cancellationToken);
        }
    }
}
