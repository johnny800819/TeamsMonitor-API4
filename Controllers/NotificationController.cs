using Microsoft.AspNetCore.Mvc;
using API4_TEAMS.Models;

[ApiController]
[Route("api/v1/notifications")]
public class NotificationController : ControllerBase
{
    private readonly TeamsNotifier _teamsNotifier;
    private readonly TeamsKeepAliveManager _keepAliveManager;
    private readonly ILogger<NotificationController> _logger;

    public NotificationController(TeamsNotifier teamsNotifier, TeamsKeepAliveManager keepAliveManager, ILogger<NotificationController> logger)
    {
        _teamsNotifier = teamsNotifier;
        _keepAliveManager = keepAliveManager;
        _logger = logger;
    }

    /// <summary>
    /// [手動執行] 立即觸發一次完整的活性維護心跳流程。
    /// </summary>
    /// <remarks>
    /// 此 API 會執行【背景服務(HeartBeat)】的核心邏輯：
    /// 1. 發送訊息到 Teams 的「資訊室通知頻道」。
    /// 2. 發送 Email 狀態報告給管理員 (含 Teams 執行結果)。
    /// 這是為了確認整個自動維護鍊路 (Teams + Email) 是否運作正常。
    /// </remarks>
    /// <returns>執行結果報告</returns>
    [HttpPost("keepalive/trigger")]
    public async Task<IActionResult> TriggerKeepAlive()
    {
        try
        {
            var result = await _keepAliveManager.RunKeepAliveWorkAsync(isManualTrigger: true);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "手動觸發活性維護任務時發生錯誤。");
            return StatusCode(500, new { message = "Failed to trigger keep-alive task.", error = ex.Message });
        }
    }

    /// <summary>
    /// [快速測試] 僅測試 Teams 通道連通性。
    /// </summary>
    /// <remarks>
    /// 單純發送一則訊息到「資訊室通知頻道」。
    /// 此 API 【不會】發送 Email 報告，純粹用於驗證 Teams Webhook URL 是否有效。
    /// </remarks>
    /// <returns>發送結果</returns>
    [HttpGet("teams")]
    public async Task<IActionResult> TestTeamsMessage()
    {
        var request = new SendTeamsNotificationRequest
        {
            Title = "API 一鍵測試通知",
            Message = "這是一則來自 API GET 端點的測試訊息，證明連線正常。",
            TargetChannel = "資訊室通知頻道"
        };
        return await SendTeamsMessage(request);
    }

    /// <summary>
    /// 發送自訂訊息到指定的 Microsoft Teams 頻道。
    /// </summary>
    /// <remarks>
    /// 此 API 受 API 金鑰保護，請在 HTTP Header 中提供 X-API-Key。
    /// </remarks>
    /// <param name="request">包含標題、訊息和目標頻道的請求內容。</param>
    /// <returns>發送成功或失敗的結果。</returns>
    [HttpPost("teams")]
    public async Task<IActionResult> SendTeamsMessage([FromBody] SendTeamsNotificationRequest request)
    {
        // [ApiController] 屬性會自動驗證 request model，若不符規則會回傳 400 Bad Request
        try
        {
            // 如果呼叫端未指定頻道，預設使用「資訊室通知頻道」
            string channel = request.TargetChannel ?? "資訊室通知頻道";

            // 取得呼叫者名稱 (由 ApiKeyMiddleware 注入)
            var clientName = HttpContext.Items["ClientName"] as string ?? "Unknown";

            await _teamsNotifier.SendTeamsNotification(channel, request.Title, request.Message);

            _logger.LogInformation("[Caller: {ClientName}] 成功透過 API 發送一則通知到 Teams 頻道 '{ChannelName}'。", clientName, channel);

            // 回傳成功的訊息
            return Ok(new { message = $"Notification successfully sent to channel: {channel}" });
        }
        catch (Exception ex)
        {
            // 捕捉從 TeamsNotifier 拋出的所有錯誤
            _logger.LogError(ex, "透過 API 發送 Teams 通知時發生未預期的錯誤。");
            return StatusCode(500, new { message = "An internal server error occurred.", error = ex.Message });
        }
    }
}
