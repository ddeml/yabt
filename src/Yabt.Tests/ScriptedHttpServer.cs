using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Yabt.Tests;

/// <summary>
/// A bounded loopback HTTP transport for provider protocol fixtures. The caller supplies
/// service semantics; this is not an emulator of Azure Blob Storage or WebDAV.
/// </summary>
public sealed class ScriptedHttpServer : IAsyncDisposable
{
    private const int MaximumRequestBytes = 1024 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new(TimeSpan.FromMinutes(1));
    private readonly ConcurrentQueue<HttpTestRequest> _requests = new();
    private readonly Task _serverTask;

    public ScriptedHttpServer(Func<HttpTestRequest, HttpTestResponse> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/");
        _serverTask = ServeAsync(handler);
    }

    public Uri Endpoint { get; }

    public IReadOnlyList<HttpTestRequest> Requests => _requests.ToArray();

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();
        try
        {
            await _serverTask;
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException and not SocketException and
                not ObjectDisposedException) { throw; }
            Debug.WriteLine(exception);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task ServeAsync(Func<HttpTestRequest, HttpTestResponse> handler)
    {
        var cancellationToken = _shutdown.Token;
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            HttpTestRequest request;
            try
            {
                request = await ReadRequestAsync(stream, cancellationToken);
            }
            catch (Exception exception)
            {
                // A deliberately failing source stream may abandon a request mid-body.
                // Incomplete requests never reach the fixture or publish an object.
                if (exception is not IOException) { throw; }
                Debug.WriteLine(exception);
                continue;
            }
            _requests.Enqueue(request);
            var response = handler(request);
            var responseBody = response.Body ?? [];
            var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Connection"] = "close",
                ["Content-Type"] = "application/octet-stream",
                ["Content-Length"] = responseBody.Length.ToString(CultureInfo.InvariantCulture),
            };
            foreach (var header in response.Headers ?? new Dictionary<string, string>())
            {
                responseHeaders[header.Key] = header.Value;
            }

            var responseHead = $"HTTP/1.1 {response.StatusCode} Test response\r\n" +
                string.Join("", responseHeaders.Select(header => $"{header.Key}: {header.Value}\r\n")) +
                "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(responseHead), cancellationToken);
            if (request.Method != "HEAD")
            {
                await stream.WriteAsync(responseBody, cancellationToken);
            }
        }
    }

    private static async Task<HttpTestRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var requestLine = await ReadLineAsync(stream, cancellationToken);
        var parts = requestLine.Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await ReadLineAsync(stream, cancellationToken) is { Length: > 0 } header)
        {
            var separator = header.IndexOf(':');
            headers.Add(header[..separator], header[(separator + 1)..].Trim());
        }

        if (headers.TryGetValue("Expect", out var expect) &&
            expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken);
        }

        var body = await ReadBodyAsync(stream, headers, cancellationToken);
        return new(parts[0], parts[1], headers, body);
    }

    private static async Task<byte[]> ReadBodyAsync
    (
        Stream stream,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken
    )
    {
        using var body = new MemoryStream();
        if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
            transferEncoding.Equals("chunked", StringComparison.OrdinalIgnoreCase))
        {
            while (true)
            {
                var lengthLine = await ReadLineAsync(stream, cancellationToken);
                var length = int.Parse(lengthLine.Split(';')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (length == 0)
                {
                    while ((await ReadLineAsync(stream, cancellationToken)).Length > 0) { }
                    break;
                }

                await ReadSegmentAsync(length);
                if ((await ReadLineAsync(stream, cancellationToken)).Length != 0)
                {
                    throw new IOException("The test HTTP chunk did not end with CRLF.");
                }
            }
        }
        else if (headers.TryGetValue("Content-Length", out var contentLength))
        {
            await ReadSegmentAsync(int.Parse(contentLength, CultureInfo.InvariantCulture));
        }

        return body.ToArray();

        async Task ReadSegmentAsync(int length)
        {
            if (length < 0 || length > MaximumRequestBytes - body.Length)
            {
                throw new IOException("The test HTTP request exceeds the fixture's size limit.");
            }

            var segment = new byte[length];
            await stream.ReadExactlyAsync(segment, cancellationToken);
            await body.WriteAsync(segment, cancellationToken);
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var next = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(next, cancellationToken);
            if (next[0] == '\n')
            {
                var value = Encoding.ASCII.GetString(line.ToArray());
                return value.TrimEnd('\r');
            }

            if (line.Length >= 16 * 1024)
            {
                throw new IOException("The test HTTP header exceeds the fixture's size limit.");
            }
            line.WriteByte(next[0]);
        }
    }
}
