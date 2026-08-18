using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MaxHub.Agent.Core.Remote;

public sealed record OAuthCallback(string Code, string State);

/// <summary>
/// 本机 OAuth 回调监听器：接收飞书重定向到 http://127.0.0.1:{port}/callback 的授权码。
/// 用 TcpListener 而非 HttpListener，避免非管理员的 URL ACL 限制。只处理一次回调。
/// </summary>
public sealed class LocalCallbackListener : IDisposable
{
    public const int DefaultPort = 47811;

    private readonly TcpListener _listener;

    private LocalCallbackListener(TcpListener listener) => _listener = listener;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static LocalCallbackListener Start(int port = DefaultPort)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return new LocalCallbackListener(listener);
    }

    public async Task<OAuthCallback?> WaitForCallbackAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(cts.Token);
            var stream = client.GetStream();

            var requestLine = await ReadRequestLineAsync(stream, cts.Token);
            var callback = ParseCallback(requestLine);

            var body = callback is null
                ? "<html><body>MaxHub 登录失败：回调参数缺失，请回到应用重试。</body></html>"
                : "<html><body>MaxHub 登录成功，请关闭此页面并返回应用。</body></html>";
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var header = $"HTTP/1.1 {(callback is null ? "400 Bad Request" : "200 OK")}\r\n" +
                         "Content-Type: text/html; charset=utf-8\r\n" +
                         $"Content-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cts.Token);
            await stream.WriteAsync(bodyBytes, cts.Token);
            return callback;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var length = await stream.ReadAsync(buffer, cancellationToken);
        var text = Encoding.ASCII.GetString(buffer, 0, length);
        var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        return lineEnd < 0 ? text : text[..lineEnd];
    }

    /// <summary>解析 "GET /callback?code=..&state=.. HTTP/1.1"。</summary>
    private static OAuthCallback? ParseCallback(string requestLine)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 2 || parts[0] != "GET")
            return null;
        var queryStart = parts[1].IndexOf('?');
        if (queryStart < 0)
            return null;

        string? code = null, state = null;
        foreach (var pair in parts[1][(queryStart + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var key = pair[..eq];
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (key == "code") code = value;
            else if (key == "state") state = value;
        }
        return code is { Length: > 0 } && state is { Length: > 0 } ? new OAuthCallback(code, state) : null;
    }

    public void Dispose() => _listener.Stop();
}
