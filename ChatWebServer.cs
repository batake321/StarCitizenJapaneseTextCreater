using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

public class ChatWebServer : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<int, WebSocket> _wsClients = new();
    private int _wsIdCounter;
    private Func<string, Task<string>>? _onSendMessage;
    private string? _htmlCache;

    public event Action<string, bool>? MessageReceived;
    public event Action? HistoryCleared;
    public bool IsRunning => _listener?.IsListening == true;

    public static string[] GetLocalIpAddresses()
    {
        var ips = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        ips.Add(addr.Address.ToString());
                }
            }
        }
        catch { }
        return ips.ToArray();
    }

    public void SetMessageHandler(Func<string, Task<string>> handler)
    {
        _onSendMessage = handler;
    }

    public async Task StartAsync(int port)
    {
        Stop();
        _cts = new CancellationTokenSource();

        _listener = new HttpListener();
        try
        {
            _listener.Prefixes.Add($"http://+:{port}/");
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            _listener.Close();
            if (TryRegisterUrlAcl(port))
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{port}/");
                _listener.Start();
            }
            else
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
            }
        }

        _ = Task.Run(() => ListenLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;

        foreach (var kv in _wsClients)
        {
            try { kv.Value.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(1000); }
            catch { }
        }
        _wsClients.Clear();
    }

    public async Task BroadcastMessageAsync(string text, bool isUser)
    {
        var msg = JsonSerializer.Serialize(new { type = "message", text, isUser });
        await BroadcastWsAsync(msg);
    }

    public async Task BroadcastClearAsync()
    {
        await BroadcastWsAsync(JsonSerializer.Serialize(new { type = "clear" }));
    }

    public async Task BroadcastTypingAsync(string status)
    {
        await BroadcastWsAsync(JsonSerializer.Serialize(new { type = "typing", status }));
    }

    private async Task BroadcastWsAsync(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var segment = new ArraySegment<byte>(bytes);
        var dead = new List<int>();

        foreach (var kv in _wsClients)
        {
            try
            {
                if (kv.Value.State == WebSocketState.Open)
                    await kv.Value.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                else
                    dead.Add(kv.Key);
            }
            catch { dead.Add(kv.Key); }
        }

        foreach (var id in dead)
        {
            if (_wsClients.TryRemove(id, out var ws))
                try { ws.Dispose(); } catch { }
        }
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequestAsync(ctx, ct));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var method = ctx.Request.HttpMethod;

        try
        {
            if (ctx.Request.IsWebSocketRequest)
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                var wsId = Interlocked.Increment(ref _wsIdCounter);
                _wsClients[wsId] = wsCtx.WebSocket;
                try { await HandleWebSocketAsync(wsCtx.WebSocket, ct); }
                finally { _wsClients.TryRemove(wsId, out _); }
                return;
            }

            if (method == "OPTIONS")
            {
                AddCorsHeaders(ctx.Response);
                ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            switch (path)
            {
                case "/":
                    await ServeHtml(ctx.Response);
                    break;
                case "/api/backends":
                    await ServeBackends(ctx.Response);
                    break;
                case "/api/chat" when method == "POST":
                    await HandleChat(ctx, ct);
                    break;
                case "/api/voicevox/speakers":
                    await ProxyVoiceVoxSpeakers(ctx.Response);
                    break;
                case "/api/settings" when method == "POST":
                    await HandleUpdateSettings(ctx, ct);
                    break;
                case "/api/knowledge" when method == "GET":
                    await ServeKnowledge(ctx.Response);
                    break;
                case "/api/knowledge" when method == "POST":
                    await HandleAddKnowledge(ctx, ct);
                    break;
                case "/api/knowledge" when method == "DELETE":
                    await HandleDeleteKnowledge(ctx, ct);
                    break;
                case var p when p.StartsWith("/api/voicevox/"):
                    await ProxyVoiceVox(ctx, ct);
                    break;
                default:
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            try
            {
                ctx.Response.StatusCode = 500;
                var errBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = ex.Message }));
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.OutputStream.WriteAsync(errBytes, ct);
                ctx.Response.Close();
            }
            catch { }
        }
    }

    private async Task HandleWebSocketAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(json);
                var msgType = doc.RootElement.GetProperty("type").GetString();

                if (msgType == "chat")
                {
                    var text = doc.RootElement.GetProperty("text").GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    MessageReceived?.Invoke(text, true);
                    await BroadcastMessageAsync(text, true);
                    await BroadcastTypingAsync("AI が回答を生成中...");

                    if (_onSendMessage != null)
                    {
                        try
                        {
                            var response = await _onSendMessage(text);
                            MessageReceived?.Invoke(response, false);
                            await BroadcastMessageAsync(response, false);
                        }
                        catch (Exception ex)
                        {
                            var errMsg = $"エラー: {ex.Message}";
                            MessageReceived?.Invoke(errMsg, false);
                            await BroadcastMessageAsync(errMsg, false);
                        }
                    }
                    await BroadcastTypingAsync("");
                }
                else if (msgType == "clear")
                {
                    HistoryCleared?.Invoke();
                    await BroadcastClearAsync();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            try { ws.Dispose(); } catch { }
        }
    }

    private async Task ServeHtml(HttpListenerResponse response)
    {
        _htmlCache ??= LoadEmbeddedHtml();
        var bytes = Encoding.UTF8.GetBytes(_htmlCache);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        AddCorsHeaders(response);
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private async Task ServeBackends(HttpListenerResponse response)
    {
        var backends = App.Config.Translation.Backends
            .Where(b => !string.IsNullOrWhiteSpace(b.ApiKey) || b.Type == "Ollama")
            .Select(b => new { b.Name, b.Model, b.Type, b.SupportsSkills })
            .ToArray();
        var voicevoxSpeakerId = App.Config.VoiceVoxSpeakerId;
        var json = JsonSerializer.Serialize(new { backends, voicevoxSpeakerId });
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json; charset=utf-8";
        AddCorsHeaders(response);
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private async Task HandleChat(HttpListenerContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.GetProperty("message").GetString() ?? "";

        MessageReceived?.Invoke(text, true);
        await BroadcastMessageAsync(text, true);
        await BroadcastTypingAsync("AI が回答を生成中...");

        string responseText;
        try
        {
            responseText = _onSendMessage != null ? await _onSendMessage(text) : "バックエンド未設定";
        }
        catch (Exception ex)
        {
            responseText = $"エラー: {ex.Message}";
        }

        MessageReceived?.Invoke(responseText, false);
        await BroadcastMessageAsync(responseText, false);
        await BroadcastTypingAsync("");

        var json = JsonSerializer.Serialize(new { response = responseText });
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        AddCorsHeaders(ctx.Response);
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();
    }

    private async Task ProxyVoiceVoxSpeakers(HttpListenerResponse response)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = await http.GetStringAsync($"{App.Config.VoiceVoxUrl}/speakers");
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json; charset=utf-8";
            AddCorsHeaders(response);
            await response.OutputStream.WriteAsync(bytes);
        }
        catch
        {
            response.StatusCode = 502;
            var err = Encoding.UTF8.GetBytes("{\"error\":\"VoiceVox に接続できません\"}");
            response.ContentType = "application/json; charset=utf-8";
            await response.OutputStream.WriteAsync(err);
        }
        response.Close();
    }

    private async Task ProxyVoiceVox(HttpListenerContext ctx, CancellationToken ct)
    {
        var subPath = ctx.Request.Url!.AbsolutePath["/api/voicevox".Length..];
        var queryString = ctx.Request.Url.Query ?? "";
        var targetUrl = $"{App.Config.VoiceVoxUrl}{subPath}{queryString}";
        System.Diagnostics.Debug.WriteLine($"[VoiceVox Proxy] {ctx.Request.HttpMethod} {subPath}{queryString} -> {targetUrl}");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            HttpResponseMessage result;

            if (ctx.Request.HttpMethod == "POST")
            {
                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms, ct);
                var content = new ByteArrayContent(ms.ToArray());
                if (ctx.Request.ContentType != null)
                    content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
                System.Diagnostics.Debug.WriteLine($"[VoiceVox Proxy] POST body: {ms.Length} bytes, content-type: {ctx.Request.ContentType}");
                result = await http.PostAsync(targetUrl, content, ct);
            }
            else
            {
                result = await http.GetAsync(targetUrl, ct);
            }

            var resBytes = await result.Content.ReadAsByteArrayAsync(ct);
            System.Diagnostics.Debug.WriteLine($"[VoiceVox Proxy] Response: {(int)result.StatusCode}, {resBytes.Length} bytes, type={result.Content.Headers.ContentType}");
            ctx.Response.StatusCode = (int)result.StatusCode;
            ctx.Response.ContentType = result.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            AddCorsHeaders(ctx.Response);
            await ctx.Response.OutputStream.WriteAsync(resBytes, ct);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VoiceVox Proxy] ERROR: {ex.Message}");
            ctx.Response.StatusCode = 502;
            var err = Encoding.UTF8.GetBytes($"{{\"error\":\"VoiceVox proxy error: {ex.Message}\"}}");
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(err, ct);
        }
        ctx.Response.Close();
    }

    private async Task HandleUpdateSettings(HttpListenerContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("voicevoxSpeakerId", out var spk))
            App.Config.VoiceVoxSpeakerId = spk.GetInt32();
        var json = JsonSerializer.Serialize(new { ok = true });
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        AddCorsHeaders(ctx.Response);
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();
    }

    private async Task ServeKnowledge(HttpListenerResponse response)
    {
        var qs = ChatService.QueryService;
        if (qs == null) { response.StatusCode = 503; response.Close(); return; }
        var entries = qs.GetAllKnowledge().Select(e => new
        {
            e.id, e.category, e.content, createdAt = e.createdAt.ToString("yyyy-MM-dd HH:mm")
        });
        var json = JsonSerializer.Serialize(new { entries });
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json; charset=utf-8";
        AddCorsHeaders(response);
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private async Task HandleAddKnowledge(HttpListenerContext ctx, CancellationToken ct)
    {
        var qs = ChatService.QueryService;
        if (qs == null) { ctx.Response.StatusCode = 503; ctx.Response.Close(); return; }
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("content").GetString() ?? "";
        var category = doc.RootElement.TryGetProperty("category", out var cat) ? cat.GetString() ?? "general" : "general";
        var id = qs.AddKnowledge(content, category);
        var json = JsonSerializer.Serialize(new { id });
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        AddCorsHeaders(ctx.Response);
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();
    }

    private async Task HandleDeleteKnowledge(HttpListenerContext ctx, CancellationToken ct)
    {
        var qs = ChatService.QueryService;
        if (qs == null) { ctx.Response.StatusCode = 503; ctx.Response.Close(); return; }
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var ids = doc.RootElement.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32());
        var deleted = qs.DeleteKnowledgeByIds(ids);
        var json = JsonSerializer.Serialize(new { deleted });
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        AddCorsHeaders(ctx.Response);
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
    }

    private static string LoadEmbeddedHtml()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("chat.html"));
        if (name != null)
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var sr = new StreamReader(stream, Encoding.UTF8);
            return sr.ReadToEnd();
        }
        return "<html><body><h1>chat.html not found</h1></body></html>";
    }

    private static bool TryRegisterUrlAcl(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"http add urlacl url=http://+:{port}/ user=Everyone",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        Stop();
    }
}
