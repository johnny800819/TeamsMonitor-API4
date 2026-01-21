using System.Text;
using System.Text.Json;

public class TeamsNotifier
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TeamsNotifier> _logger;

    public TeamsNotifier(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<TeamsNotifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendTeamsNotification(string channelName, string title, string message)
    {
        // 根據 channelName 查找對應的 Webhook Url
        string configKey = $"Teams:WebhookUrls:{channelName}";
        string? webhookUrl = _configuration[configKey];

        // 如果找不到指定的頻道，則嘗試使用 "default" 頻道作為 fallback
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            _logger.LogWarning("找不到名為 '{ChannelName}' 的 Webhook URL，將嘗試使用 'default' 頻道。", channelName);
            webhookUrl = _configuration["Teams:WebhookUrls:default"];
        }

        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            // 如果連 default 都找不到，就記錄錯誤並放棄發送
            _logger.LogError("連 default頻道的 Webhook URL 都未設定，無法發送 Teams 通知。");
            throw new InvalidOperationException("Webhook URL for the target channel is not configured.");
        }

        var httpClient = _httpClientFactory.CreateClient();

        // 建立 JSON Payload
        // 同時包含 'text' (標準 Teams Webhook 格式) 和 'message' (Power Automate Flow 可能需要的格式)
        // 這樣做可以確保相容性
        var payload = new { title, text = message, message = message };
        var jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        try
        {
            _logger.LogInformation("發送 Teams 通知到頻道 '{ChannelName}'，Title: {Title}", channelName, title);
            var response = await httpClient.PostAsync(webhookUrl, content);
            
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Teams Webhook 回應失敗。狀態碼: {StatusCode}, 原因: {ReasonPhrase}, 回應內容: {ResponseBody}, 發送的 Payload: {Payload}",
                    response.StatusCode,
                    response.ReasonPhrase,
                    responseBody,
                    jsonPayload
                );
                throw new HttpRequestException($"Teams Webhook returned {response.StatusCode}: {responseBody}");
            }
            
            _logger.LogInformation("Teams 通知發送成功到頻道 '{ChannelName}'", channelName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendTeamsNotification 發送 Teams 通知到頻道 '{ChannelName}' 時失敗。", channelName);
            throw;
        }
    }
}
