using System.Text;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

public class UexSubmissionService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const string SubmitUrl = "https://api.uexcorp.space/2.0/data_submit";

    public event Action<string>? OnLog;

    public async Task<UexSubmitResult> SubmitAsync(
        string apiKey,
        TerminalCaptureData captureData,
        string? gameVersion = null,
        bool includeScreenshot = false)
    {
        if (string.IsNullOrEmpty(apiKey))
            return new UexSubmitResult { Success = false, Message = "UEX APIキーが設定されていません" };

        if (captureData.TerminalId <= 0)
            return new UexSubmitResult { Success = false, Message = "ターミナルIDが不明です" };

        var matched = captureData.Commodities.Where(c => c.IsMatched && c.CommodityId > 0).ToList();
        if (matched.Count == 0)
            return new UexSubmitResult { Success = false, Message = "送信可能なコモディティがありません" };

        var prices = new List<object>();
        foreach (var c in matched)
        {
            if (captureData.Mode == "BUY")
            {
                prices.Add(new
                {
                    id_commodity = c.CommodityId,
                    price_buy = c.Price,
                    scu_buy = c.Inventory,
                    status_buy = c.Inventory > 0 ? 1 : 0,
                });
            }
            else
            {
                prices.Add(new
                {
                    id_commodity = c.CommodityId,
                    price_sell = c.Price,
                    scu_sell = c.Inventory,
                    status_sell = c.Inventory > 0 ? 1 : 0,
                });
            }
        }

        var payload = new Dictionary<string, object>
        {
            ["id_terminal"] = captureData.TerminalId,
            ["type"] = "commodity",
            ["is_production"] = 1,
            ["prices"] = prices,
        };

        if (!string.IsNullOrEmpty(gameVersion))
            payload["game_version"] = gameVersion;

        if (includeScreenshot && captureData.ScreenshotPng != null)
            payload["screenshot"] = Convert.ToBase64String(captureData.ScreenshotPng);

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        OnLog?.Invoke($"UEX送信中: ターミナル={captureData.TerminalName} (ID:{captureData.TerminalId}), {matched.Count}品目");

        using var request = new HttpRequestMessage(HttpMethod.Post, SubmitUrl);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var resp = await Http.SendAsync(request);
            var body = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                OnLog?.Invoke($"UEX送信成功: {body}");
                return new UexSubmitResult
                {
                    Success = true,
                    Message = $"{matched.Count}品目の価格データを送信しました",
                    ResponseBody = body,
                    HttpStatus = (int)resp.StatusCode,
                };
            }
            else
            {
                OnLog?.Invoke($"UEX送信エラー: {resp.StatusCode} {body}");
                return new UexSubmitResult
                {
                    Success = false,
                    Message = $"HTTP {(int)resp.StatusCode}: {body}",
                    ResponseBody = body,
                    HttpStatus = (int)resp.StatusCode,
                };
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"UEX送信例外: {ex.Message}");
            return new UexSubmitResult
            {
                Success = false,
                Message = ex.Message,
            };
        }
    }
}

public class UexSubmitResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? ResponseBody { get; set; }
    public int HttpStatus { get; set; }
}
