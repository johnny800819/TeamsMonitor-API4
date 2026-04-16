namespace API4_TEAMS.Models
{
    public class MailSettings
    {
        public string SMTPServer { get; set; } = string.Empty;
        public int ServerPort { get; set; } = 25;
        public string MailAccount { get; set; } = string.Empty;
        public string MailPassword { get; set; } = string.Empty;
        public string SenderName { get; set; } = string.Empty;
        public string SenderAddress { get; set; } = string.Empty;
        public string ReceiverAddress { get; set; } = string.Empty;
        public string CcReceiverAddress { get; set; } = string.Empty;
    }
}
