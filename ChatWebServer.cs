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
    private HttpListener? _httpsListener;
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

    public int HttpsPort { get; private set; }

    public async Task StartAsync(int port, int httpsPort = 0)
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

        if (httpsPort <= 0) httpsPort = port + 1;
        _ = Task.Run(async () =>
        {
            try
            {
                var thumbprint = SslCertHelper.EnsureCertificateAndBind(httpsPort);
                if (thumbprint == null)
                {
                    System.Diagnostics.Debug.WriteLine("[HTTPS] Certificate setup failed");
                    return;
                }
                System.Diagnostics.Debug.WriteLine($"[HTTPS] Cert ready: {thumbprint}");

                await Task.Delay(1000);

                _httpsListener = new HttpListener();
                _httpsListener.Prefixes.Add($"https://+:{httpsPort}/");
                _httpsListener.Start();
                HttpsPort = httpsPort;
                System.Diagnostics.Debug.WriteLine($"[HTTPS] Listening on port {httpsPort}");
                await ListenLoop(_httpsListener, _cts!.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HTTPS] Failed to start: {ex.Message}\n{ex.StackTrace}");
            }
        });

        _ = Task.Run(() => ListenLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        try { _httpsListener?.Stop(); } catch { }
        try { _httpsListener?.Close(); } catch { }
        _httpsListener = null;
        HttpsPort = 0;

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

    public async Task BroadcastTypingAsync(string status, string? speak = null)
    {
        if (speak != null)
            await BroadcastWsAsync(JsonSerializer.Serialize(new { type = "typing", status, speak }));
        else
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

    private Task ListenLoop(CancellationToken ct) => ListenLoop(_listener, ct);

    private async Task ListenLoop(HttpListener? listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener?.IsListening == true)
        {
            try
            {
                var ctx = await listener.GetContextAsync().WaitAsync(ct);
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
                case "/cert":
                    await ServeCertPage(ctx.Response);
                    break;
                case "/cert/download":
                    await ServeCertDownload(ctx.Response);
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
        var (id, isDup) = qs.AddKnowledgeSafe(content, category);
        var json = JsonSerializer.Serialize(new { id, isDuplicate = isDup });
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

    private async Task ServeCertPage(HttpListenerResponse response)
    {
        var ips = GetLocalIpAddresses();
        var port = HttpsPort > 0 ? HttpsPort : 8100;
        var ip = ips.Length > 0 ? ips[0] : "PC_IP";
        var html = "<!DOCTYPE html><html lang=\"ja\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<title>証明書インストール</title><style>" +
            "body{font-family:-apple-system,sans-serif;max-width:600px;margin:20px auto;padding:0 16px;line-height:1.6}" +
            "h1{font-size:1.3em}.btn{display:inline-block;background:#4CAF50;color:#fff;padding:14px 28px;" +
            "border-radius:8px;text-decoration:none;font-size:1.1em;margin:12px 0}" +
            ".step{background:#f5f5f5;border-radius:8px;padding:12px 16px;margin:8px 0}" +
            ".step b{color:#333}.warn{color:#c00;font-weight:bold}</style></head><body>" +
            "<h1>SC 日本語アシスタント — HTTPS 証明書</h1>" +
            "<p>スマホに証明書をインストールすると、HTTPS 経由でマイクが使えるようになります。</p>" +
            "<p><a class=\"btn\" href=\"/cert/download\">証明書をダウンロード (.cer)</a></p>" +
            "<h2>iPhone / iPad</h2>" +
            "<div class=\"step\"><b>1.</b> 上のボタンをタップ →「プロファイルがダウンロードされました」</div>" +
            "<div class=\"step\"><b>2.</b> 設定 → 一般 → VPN とデバイス管理 → ダウンロード済みプロファイル → インストール</div>" +
            "<div class=\"step\"><b>3.</b> 設定 → 一般 → 情報 → 証明書信頼設定 →「SC Japanese Assistant」を<b>有効</b>にする</div>" +
            $"<div class=\"step\"><b>4.</b> Safari で <b>https://{ip}:{port}/</b> にアクセス</div>" +
            "<h2>Android</h2>" +
            "<div class=\"step\"><b>1.</b> 上のボタンをタップ → ダウンロード完了</div>" +
            "<div class=\"step\"><b>2.</b> 設定 → セキュリティ → 暗号化と認証情報 → 証明書のインストール → CA 証明書</div>" +
            "<div class=\"step\"><b>3.</b> ダウンロードした <b>sc-assistant.cer</b> を選択してインストール</div>" +
            $"<div class=\"step\"><b>4.</b> Chrome で <b>https://{ip}:{port}/</b> にアクセス</div>" +
            "<p class=\"warn\">※ この証明書はこの PC のアシスタント専用です。不要になったらスマホから削除してください。</p>" +
            "</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private async Task ServeCertDownload(HttpListenerResponse response)
    {
        var certBytes = SslCertHelper.ExportPublicCertBytes();
        if (certBytes == null)
        {
            response.StatusCode = 404;
            var err = Encoding.UTF8.GetBytes("証明書が見つかりません。HTTPS サーバーを先に起動してください。");
            response.ContentType = "text/plain; charset=utf-8";
            await response.OutputStream.WriteAsync(err);
            response.Close();
            return;
        }
        response.ContentType = "application/x-x509-ca-cert";
        response.Headers.Add("Content-Disposition", "attachment; filename=sc-assistant.cer");
        response.ContentLength64 = certBytes.Length;
        await response.OutputStream.WriteAsync(certBytes);
        response.Close();
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
