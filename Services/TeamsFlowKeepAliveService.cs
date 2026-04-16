using System.Globalization;
using API4_TEAMS.Models;

/// <summary>
/// 背景自動執行服務 (Background Service)：用於 Teams Flow 活性維護。
/// 每 80 天 (或指定間隔) 搭配指定時間 (PreferredTime) 自動發送一次 Teams 訊息與 Email 通報。
/// 避免 Power Automate Flow 因為 90 天沒有執行而被系統停用。
/// </summary>
public class TeamsFlowKeepAliveService : BackgroundService
{
    private readonly TeamsKeepAliveManager _keepAliveManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TeamsFlowKeepAliveService> _logger;
    private readonly string _logDirectory;

    public TeamsFlowKeepAliveService(
        TeamsKeepAliveManager keepAliveManager,
        IConfiguration configuration,
        ILogger<TeamsFlowKeepAliveService> logger,
        IHostEnvironment env)
    {
        _keepAliveManager = keepAliveManager;
        _configuration = configuration;
        _logger = logger;
        
        // 設定 Logs 資料夾路徑以存放紀錄檔
        _logDirectory = Path.Combine(env.ContentRootPath, "Logs");
        if (!Directory.Exists(_logDirectory))
        {
            Directory.CreateDirectory(_logDirectory);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Teams Flow 活性維護背景服務已啟動。");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. 讀取設定
                var isEnabled = _configuration.GetValue<bool>("Teams:KeepAlive:Enabled", true);
                if (!isEnabled)
                {
                    _logger.LogInformation("Teams Flow 活性維護服務目前設定為禁用狀態。將於 1 小時後重新檢查設定...");
                    await SafeDelayAsync(TimeSpan.FromHours(1), stoppingToken);
                    continue;
                }

                var isTestMode = _configuration.GetValue<bool>("Teams:KeepAlive:TestMode", false);
                var intervalDays = _configuration.GetValue<int>("Teams:KeepAlive:IntervalDays", 80);
                var testIntervalMinutes = _configuration.GetValue<int>("Teams:KeepAlive:TestIntervalMinutes", 1);
                var preferredTimeStr = _configuration.GetValue<string>("Teams:KeepAlive:PreferredTime", "09:00");
                var fileName = _configuration.GetValue<string>("Teams:KeepAlive:LastRunDateFileName", "TeamsKeepAlive_LastRunDate.txt");

                // 2. 獲取上次發送日期
                DateTime lastRunDate = GetLastRunDate(fileName);
                DateTime now = DateTime.Now;

                // 3. 計算下一次應該觸發的精確時間點 (nextRunTime)
                DateTime nextRunTime;

                if (isTestMode)
                {
                    // 測試模式下，下一次執行時間為：上次執行時間 + N 分鐘
                    nextRunTime = lastRunDate.AddMinutes(testIntervalMinutes);
                    _logger.LogWarning("[測試模式] 預計下一次執行時間：{Time}", nextRunTime.ToString("yyyy-MM-dd HH:mm:ss"));
                }
                else
                {
                    // 標準模式：下一次執行時間為：上次日期 + 80天，且對齊設定好的 "時:分:00"
                    DateTime targetDate = lastRunDate.Date.AddDays(intervalDays);
                    
                    if (TimeSpan.TryParseExact(preferredTimeStr, "hh\\:mm", CultureInfo.InvariantCulture, out var preferredTime))
                    {
                        nextRunTime = targetDate + preferredTime;
                    }
                    else
                    {
                        // 若設定失敗，退回每天凌晨 09:00 作為防呆機制
                        _logger.LogWarning("PreferredTime '{Str}' 解析失敗，強制使用 09:00。", preferredTimeStr);
                        nextRunTime = targetDate + new TimeSpan(9, 0, 0); 
                    }
                    
                    _logger.LogInformation("活性維護預定下一次執行時間為：{Time}", nextRunTime.ToString("yyyy-MM-dd HH:mm:ss"));
                }

                // 4. 判斷是否需要立即補發，或是繼續睡覺
                if (now >= nextRunTime)
                {
                    // 已經超過時間 (可能因為停機)，馬上發送！
                    _logger.LogInformation("檢測到已到達或超過預定時間，準備觸發心跳...");
                    await _keepAliveManager.RunKeepAliveWorkAsync(isManualTrigger: false);
                    
                    // 成功後更新紀錄為「此時此刻」
                    UpdateLastRunDate(fileName, DateTime.Now);
                }
                else
                {
                    // 時間還沒到，算出時間差並交給 SafeDelay 去睡眠
                    TimeSpan delay = nextRunTime - now;
                    _logger.LogInformation("系統將進入休眠 {Days} 天 {Hours} 小時 {Minutes} 分，直到下次目標時間。", delay.Days, delay.Hours, delay.Minutes);
                    
                    // 等待
                    await SafeDelayAsync(delay, stoppingToken);

                    // 睡醒了！再次驗證沒有被中斷
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        await _keepAliveManager.RunKeepAliveWorkAsync(isManualTrigger: false);
                        UpdateLastRunDate(fileName, DateTime.Now);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Teams Flow 活性維護背景排程發生例外錯誤。");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // 稍微等一下避免出錯無限迴圈
            }
        }
    }

    /// <summary>
    /// 安全的延遲方法，支援超過 24.8 天的間隔。
    /// .NET Task.Delay 底層使用 int.MaxValue (毫秒) 作為最大值，約 24.85 天。
    /// 透過分段睡眠避免例外崩潰，若直接傳入 80 天，會觸發 ArgumentOutOfRangeException 並導致整台應用程式崩潰。
    /// </summary>
    private async Task SafeDelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        var remaining = delay;
        var maxDelay = TimeSpan.FromDays(24); // 安全上限

        while (remaining > TimeSpan.Zero && !stoppingToken.IsCancellationRequested)
        {
            var currentDelay = remaining > maxDelay ? maxDelay : remaining;
            await Task.Delay(currentDelay, stoppingToken);
            remaining -= currentDelay;
        }
    }

    /// <summary>
    /// 從檔案讀取上次執行日期。若檔案不存在，則預設為今日並紀錄。
    /// </summary>
    private DateTime GetLastRunDate(string fileName)
    {
        var filePath = Path.Combine(_logDirectory, fileName);
        if (File.Exists(filePath))
        {
            var content = File.ReadAllText(filePath).Trim();
            if (DateTime.TryParse(content, out var lastDate))
            {
                return lastDate;
            }
        }

        // 第一次啟動時，將今日視為上次紀錄時間 (以今日為起點開始倒數)
        var defaultDate = DateTime.Now;
        UpdateLastRunDate(fileName, defaultDate);
        _logger.LogInformation("建立新的活性維護紀錄檔，啟動基準日：{Date}", defaultDate.ToString("yyyy-MM-dd HH:mm:ss"));
        return defaultDate;
    }

    /// <summary>
    /// 更新執行日期紀錄檔。
    /// </summary>
    private void UpdateLastRunDate(string fileName, DateTime date)
    {
        var filePath = Path.Combine(_logDirectory, fileName);
        File.WriteAllText(filePath, date.ToString("yyyy-MM-dd HH:mm:ss"));
    }
}
