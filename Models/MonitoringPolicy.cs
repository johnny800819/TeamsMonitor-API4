namespace API4_TEAMS.Models
{
    public class MonitoringPolicy
    {
        public string PolicyName { get; set; } = string.Empty;
        public int IntervalMinutes { get; set; } = 60; // 提供一個預設值
        public string StartTime { get; set; } = "00:00";
        public string EndTime { get; set; } = "24:00";
        public string TargetChannel { get; set; } = "default"; // 預設使用 default 頻道
        public bool IsPolicyEnabled { get; set; } = true; // 策略啟用開關
        public bool IsSuccessNotificationEnabled { get; set; } = true; // 成功通知啟用開關
        public List<string> Websites { get; set; } = new List<string>();
    }
}
