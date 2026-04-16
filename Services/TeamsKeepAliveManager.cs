using System.Text;
using API4_TEAMS.Models;

public class TeamsKeepAliveManager
{
    private readonly TeamsNotifier _teamsNotifier;
    private readonly MailService _mailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TeamsKeepAliveManager> _logger;

    public TeamsKeepAliveManager(
        TeamsNotifier teamsNotifier,
        MailService mailService,
        IConfiguration configuration,
        ILogger<TeamsKeepAliveManager> logger)
    {
        _teamsNotifier = teamsNotifier;
        _mailService = mailService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// 執行完整的活性維護任務：發送 Teams 訊息並寄送 Email 報告。
    /// </summary>
    /// <param name="isManualTrigger">是否為手動觸發</param>
    /// <returns>執行結果報告</returns>
    public async Task<KeepAliveResult> RunKeepAliveWorkAsync(bool isManualTrigger = false)
    {
        var intervalDays = _configuration.GetValue<int>("Teams:KeepAlive:IntervalDays", 80);
        var targetChannel = _configuration.GetValue<string>("Teams:KeepAlive:TargetChannel", "資訊室通知頻道");

        _logger.LogInformation("{TriggerType} 執行 Teams Flow 活性維護任務... (目標頻道: {Channel})", isManualTrigger ? "[手動]" : "[排程]", targetChannel);

        string title = "💓 系統活性維護服務 (Keep-Alive)";
        string nextRunDate = DateTime.Now.AddDays(intervalDays).ToString("yyyy-MM-dd");

        var sb = new StringBuilder();
        sb.AppendLine($"<h4>這是一則{(isManualTrigger ? "手動發送" : "自動發送")}的心跳訊息</h4>");
        sb.AppendLine("<p>目的：確保此 Teams Flow 保持啟動狀態，避免因 90 天無活動而被系統停用。</p>");
        sb.AppendLine($"<p>下一輪預計執行日期：<strong>{nextRunDate}</strong></p>");

        string messageBody = sb.ToString();
        bool teamsSuccess = false;
        string teamsError = string.Empty;

        // --- Step 1: 發送 Teams ---
        try
        {
            await _teamsNotifier.SendTeamsNotification(targetChannel, title, messageBody);
            teamsSuccess = true;
        }
        catch (Exception ex)
        {
            teamsError = ex.Message;
        }

        // --- Step 2: 發送 Email ---
        try
        {
            string emailSubject = $"{(teamsSuccess ? "✅" : "❌")} Teams Flow 活性維護狀態報告 - {DateTime.Now:yyyy/MM/dd} {(isManualTrigger ? "(手動觸發)" : "")}";

            var emailBody = new StringBuilder();
            emailBody.Append($"<h3>Teams Flow 活性維護任務執行完畢 {(isManualTrigger ? "- 手動觸發" : "")}</h3>");
            emailBody.Append($"<ul>");
            emailBody.Append($"<li><strong>執行時間：</strong> {DateTime.Now:yyyy-MM-dd HH:mm:ss}</li>");
            emailBody.Append($"<li><strong>Teams 發送結果：</strong> {(teamsSuccess ? "<span style='color:green;'>成功</span>" : "<span style='color:red;'>失敗</span>")}</li>");
            if (!teamsSuccess)
            {
                emailBody.Append($"<li><strong>錯誤訊息：</strong> {teamsError}</li>");
            }
            emailBody.Append($"<li><strong>目標頻道：</strong> {targetChannel}</li>");
            emailBody.Append($"<li><strong>下一輪預計執行日期：</strong> {nextRunDate}</li>");
            emailBody.Append($"</ul>");
            emailBody.Append($"<hr/><p>本郵件由 API4-TEAMS 系統對應之 Keep-Alive 模組發出。</p>");

            await _mailService.SendEmailAsync(emailSubject, emailBody.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Keep-Alive 狀態回報 Email 發送失敗。");
        }

        return new KeepAliveResult
        {
            IsTeamsSuccess = teamsSuccess,
            ErrorMessage = teamsError,
            Channel = targetChannel,
            NextRunDate = nextRunDate
        };
    }

    public class KeepAliveResult
    {
        public bool IsTeamsSuccess { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string Channel { get; set; } = string.Empty;
        public string NextRunDate { get; set; } = string.Empty;
    }
}
