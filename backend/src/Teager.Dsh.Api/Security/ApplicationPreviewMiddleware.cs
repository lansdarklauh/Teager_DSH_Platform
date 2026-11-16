using System.Net;
using System.Net.WebSockets;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Runtime;

namespace Teager.Dsh.Api.Security;

/// <summary>Random, isolated preview origins proxy only an authorized application's declared port.</summary>
public class ApplicationPreviewMiddleware(RequestDelegate next)
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
    public async Task InvokeAsync(HttpContext context, DockerSandbox sandbox)
    {
        var authority = context.Request.Host.Value ?? "";
        if (!sandbox.IsPreviewHost(authority)) { await next(context); return; }
        try
        {
            var target = await sandbox.AuthorizePreviewAsync(authority, context.RequestAborted);
            var path = context.Request.Path + context.Request.QueryString;
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "frame-ancestors " + target.PlatformOrigin;
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var upstream = new ClientWebSocket();
                upstream.Options.SetRequestHeader("X-Execution-Key", target.Secret);
                upstream.Options.SetRequestHeader("X-Application-Port", target.ApplicationPort.ToString());
                foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols) upstream.Options.AddSubProtocol(protocol);
                await upstream.ConnectAsync(new Uri($"ws://127.0.0.1:{target.IngressPort}{path}"), context.RequestAborted);
                using var downstream = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                cancel.CancelAfter(target.ExpiresAt - DateTime.Now > TimeSpan.Zero ? target.ExpiresAt - DateTime.Now : TimeSpan.Zero);
                var send = CopySocket(downstream, upstream, cancel.Token); var receive = CopySocket(upstream, downstream, cancel.Token);
                await Task.WhenAny(send, receive); cancel.Cancel();
                try { await Task.WhenAll(send, receive); } catch (OperationCanceledException) { }
                return;
            }
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), $"http://127.0.0.1:{target.IngressPort}{path}");
            request.Headers.Add("X-Execution-Key", target.Secret);
            request.Headers.Add("X-Application-Port", target.ApplicationPort.ToString());
            if (context.Request.ContentLength > 16 * 1024 * 1024) throw ApiException.BadRequest("应用请求体上限 16 MB");
            if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                using var body = new MemoryStream();
                var buffer = new byte[16384];
                int count;
                while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    if (body.Length + count > 16 * 1024 * 1024) throw ApiException.BadRequest("应用请求体上限 16 MB");
                    body.Write(buffer, 0, count);
                }
                request.Content = new ByteArrayContent(body.ToArray());
            }
            foreach (var header in context.Request.Headers)
            {
                if (header.Key.ToLowerInvariant() is "host" or "cookie" or "authorization" or "connection" or "transfer-encoding" or "x-execution-key" or "x-application-port" or "content-length") continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && request.Content != null) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Content.Headers.Concat(response.Headers))
                if (header.Key.ToLowerInvariant() is "content-type" or "content-length" or "location" or "cache-control" or "etag" or "last-modified") context.Response.Headers[header.Key] = header.Value.ToArray();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (ApiException ex) { context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new { ex.Code, ex.Message }); }
        catch (HttpRequestException) { context.Response.StatusCode = 502; await context.Response.WriteAsJsonAsync(new { code = "application_unavailable", message = "应用端口尚未就绪" }); }
    }

    private static async Task CopySocket(WebSocket source, WebSocket target, CancellationToken ct)
    {
        var buffer = new byte[16384];
        while (!ct.IsCancellationRequested)
        {
            var message = await source.ReceiveAsync(buffer, ct);
            if (message.MessageType == WebSocketMessageType.Close) { await target.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct); return; }
            await target.SendAsync(buffer.AsMemory(0, message.Count), message.MessageType, message.EndOfMessage, ct);
        }
    }
}
