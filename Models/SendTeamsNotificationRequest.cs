using System.ComponentModel.DataAnnotations;

namespace API4_TEAMS.Models
{
    public class SendTeamsNotificationRequest
    {
        [Required(ErrorMessage = "標題為必填欄位")]
        [MaxLength(30, ErrorMessage = "標題長度不可超過 30 字")]
        public string Title { get; set; } = string.Empty;

        [Required(ErrorMessage = "訊息內容為必填欄位")]
        [MaxLength(100, ErrorMessage = "訊息內容不可超過 100 字")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 指定要發送的目標頻道名稱 (對應 appsettings.json 中的名稱)。
        /// 若不提供，則會使用 "default" 頻道。
        /// </summary>
        public string? TargetChannel { get; set; }
    }
}

