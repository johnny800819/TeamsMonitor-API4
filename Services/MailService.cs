using System.Net;
using System.Net.Mail;
using API4_TEAMS.Models;
using Microsoft.Extensions.Options;

public class MailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MailService> _logger;

    public MailService(IConfiguration configuration, ILogger<MailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// 發送通知郵件
    /// </summary>
    /// <param name="subject">主旨</param>
    /// <param name="body">內容 (支援 HTML)</param>
    /// <returns>發送是否成功</returns>
    public async Task<bool> SendEmailAsync(string subject, string body)
    {
        // 直接從 IConfiguration 讀取 MailSend 區段
        var settings = _configuration.GetSection("MailSend").Get<MailSettings>();

        if (settings == null || string.IsNullOrWhiteSpace(settings.SMTPServer))
        {
            _logger.LogError("Email 設定 (MailSend) 缺失或不完整，無法發送郵件。");
            return false;
        }

        try
        {
            using var message = new MailMessage();
            message.From = new MailAddress(settings.SenderAddress, settings.SenderName);
            
            // 處理多個收件人 (以逗號分隔)
            foreach (var addr in settings.ReceiverAddress.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                message.To.Add(addr.Trim());
            }

            // 處理副本
            if (!string.IsNullOrWhiteSpace(settings.CcReceiverAddress))
            {
                foreach (var addr in settings.CcReceiverAddress.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    message.CC.Add(addr.Trim());
                }
            }

            message.Subject = subject;
            message.Body = body;
            message.IsBodyHtml = true;

            using var client = new SmtpClient(settings.SMTPServer, settings.ServerPort);
            
            // 如果有設定帳號密碼則啟用驗證
            if (!string.IsNullOrWhiteSpace(settings.MailAccount) && !string.IsNullOrWhiteSpace(settings.MailPassword))
            {
                client.Credentials = new NetworkCredential(settings.MailAccount, settings.MailPassword);
                client.EnableSsl = true; // 外部 SMTP 密碼通常需要 SSL
            }
            else
            {
                client.UseDefaultCredentials = true; // 內部 Relay 通常不需要帳密
            }

            _logger.LogInformation("正在發送 Email 通知至: {Receivers}，主旨: {Subject}", settings.ReceiverAddress, subject);
            await client.SendMailAsync(message);
            _logger.LogInformation("Email 通知發送成功。");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "發送 Email 時發生錯誤。SMTP: {Server}:{Port}", settings.SMTPServer, settings.ServerPort);
            return false;
        }
    }
}
