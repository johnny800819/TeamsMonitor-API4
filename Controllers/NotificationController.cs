using Microsoft.AspNetCore.Mvc;
using API4_TEAMS.Models;

[ApiController]
[Route("api/v1/notifications")]
public class NotificationController : ControllerBase
{
    private readonly TeamsNotifier _teamsNotifier;
    private readonly ILogger<NotificationController> _logger;

    public NotificationController(TeamsNotifier teamsNotifier, ILogger<NotificationController> logger)
    {
        _teamsNotifier = teamsNotifier;
        _logger = logger;
    }

    /// <summary>
    /// 發送一則訊息到指定的 Microsoft Teams 頻道。
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
            // 如果呼叫端未指定頻道，我們的業務邏輯是使用 "default"
            string channel = request.TargetChannel ?? "default";

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
