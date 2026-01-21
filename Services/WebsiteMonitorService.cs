using System.Net;
using API4_TEAMS.Models;
using System.Text;

// BackgroundService 是 .NET Core 內建的一個類別，專門用來處理「長時間執行的後台服務」，例如定期檢查網站狀態。
public class WebsiteMonitorService : BackgroundService
{
    private readonly Guid _instanceId = Guid.NewGuid(); // 加入這個新的「指紋」欄位 (測試檢測用)
    private readonly IHttpClientFactory _httpClientFactory; // 改用 IHttpClientFactory 來建立 HttpClient，這是處理 DNS 更新和 Socket 管理的正確方式
    private readonly TeamsNotifier _teamsNotifier;
    private readonly ILogger<WebsiteMonitorService> _logger;
    private readonly IConfiguration _configuration;

    public WebsiteMonitorService(
        IHttpClientFactory httpClientFactory,
        TeamsNotifier teamsNotifier,
        ILogger<WebsiteMonitorService> logger,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _teamsNotifier = teamsNotifier;
        _logger = logger;
        _configuration = configuration;
    }

    // ExecuteAsync 是 BackgroundService 的核心方法，會在服務啟動時由 ASP.NET Core 框架自動呼叫並執行。
    // 只要應用程式在運行，這個方法就會持續執行（直到收到停止請求），不需手動觸發。
    // 本方法採用「固定心跳」模式，每分鐘喚醒一次，並根據策略判斷是否需要觸發網站狀態檢查。
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ✨ 1. 初始化「節拍器」：計算出服務啟動後的第一個「下一個整分鐘」作為首次執行的目標時間點。
        var nextTick = DateTime.Now.Date.AddHours(DateTime.Now.Hour).AddMinutes(DateTime.Now.Minute + 1);

        while (!stoppingToken.IsCancellationRequested) // stoppingToken.IsCancellationRequested 代表 應用程式是否收到「停止請求」
        {
            try
            {
                // ✨ 2. 等待節拍點：計算到下一個目標時間點的延遲，並非同步等待。
                var delayTime = nextTick - DateTime.Now;
                if (delayTime > TimeSpan.Zero)
                {
                    await Task.Delay(delayTime, stoppingToken);
                }

                // ✨ 3. 使用節拍點作為精準時間：這是我們這一輪判斷所有邏輯的「官方時間」，它永遠是整分鐘。
                var tickTime = nextTick;

                // ✨ 4. 設定下一次的節拍點：立刻計算出再下一分鐘的目標時間，為下一次迴圈做準備。
                nextTick = nextTick.AddMinutes(1);

                // --- 以下為主要監控邏輯 ---

                // 1. 取得所有監控策略
                var policies = _configuration.GetSection("Teams:MonitoringPolicies").Get<List<MonitoringPolicy>>() ?? new List<MonitoringPolicy>();
                if (!policies.Any())
                {
                    _logger.LogWarning("[監控-{InstanceId}] 未設定任何監控策略 (MonitoringPolicies)！服務將暫停24小時。", _instanceId.ToString().Substring(0, 8));
                    await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
                    continue;
                }

                // 2. 找出本輪需要執行的策略，並收集所有需要檢查的網站
                var policiesToRun = new List<MonitoringPolicy>();
                var allUrlsToCheck = new HashSet<string>();

                // ✨ 使用 tickTime 來進行判斷 ✨
                var tickTimeOnly = TimeOnly.FromDateTime(tickTime);

                foreach (var policy in policies)
                {
                    // 檢查策略是否啟用
                    if (!policy.IsPolicyEnabled) continue;

                    // 進行兩階段條件判斷
                    bool isInTimeWindow = IsTimeInWindow(tickTimeOnly, policy.StartTime, policy.EndTime);
                    bool isOnInterval = IsOnInterval(tickTime, policy.IntervalMinutes);

                    if (isInTimeWindow && isOnInterval)
                    {
                        var validWebsites = policy.Websites?.Where(url => !string.IsNullOrWhiteSpace(url)).ToList();
                        if (validWebsites != null && validWebsites.Any())
                        {
                            policiesToRun.Add(policy);
                            foreach (var url in validWebsites)
                            {
                                allUrlsToCheck.Add(url);
                            }
                        }
                    }
                }

                // 3. 如果有需要監控的網站，執行並行檢查
                if (allUrlsToCheck.Any())
                {
                    _logger.LogInformation("========== [監控-{InstanceId}] [{Time}] 開始本輪監控 ==========", _instanceId.ToString().Substring(0, 8), tickTime.ToString("HH:mm:ss"));
                    _logger.LogInformation("[監控-{InstanceId}] 本輪觸發 {PolicyCount} 個策略，共需檢查 {UrlCount} 個獨立網站。", 
                        _instanceId.ToString().Substring(0, 8), policiesToRun.Count, allUrlsToCheck.Count);

                    // 呼叫並行檢查方法，同時對所有不重複的 URL 進行 HTTP 請求
                    // 這能確保即使有多個策略包含同一個網站，我們也只會檢查一次，節省資源
                    var checkResults = await CheckWebsitesInParallelAsync(allUrlsToCheck);

                    // 4. 針對每個觸發的策略，聚合結果並發送通知
                    foreach (var policy in policiesToRun)
                    {
                        // 將檢查結果依照策略進行分組與過濾，並發送聚合後的通知
                        await ProcessPolicyNotificationAsync(policy, checkResults);
                    }
                    
                    _logger.LogInformation("========== [監控-{InstanceId}] 本輪監控完成 ==========\n\n", _instanceId.ToString().Substring(0, 8));
                }
                else
                {
                    _logger.LogDebug("[監控-{InstanceId}] 本輪無策略觸發或無網站需檢查。", _instanceId.ToString().Substring(0, 8));
                }
            }
            catch (TaskCanceledException)
            {
                // 當應用程式關閉時，Task.Delay 會拋出此例外，這是正常行為，直接跳出迴圈即可。
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[監控-{InstanceId}] 發生未預期錯誤", _instanceId.ToString().Substring(0, 8));
                // 發生未知錯誤時，稍微等待一下避免CPU空轉
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    // 判斷目前時間是否在策略的啟動時窗內
    private bool IsTimeInWindow(TimeOnly currentTime, string startTimeStr, string endTimeStr)
    {
        // 先嘗試將字串解析為 TimeOnly 物件
        if (!TimeOnly.TryParse(startTimeStr, out var startTime) || !TimeOnly.TryParse(endTimeStr.Replace("24:00", "23:59"), out var endTime))
        {
            return false; // 如果格式錯誤，則視為不在時窗內
        }
        // 處理跨午夜的特殊情況 (例如 22:00 - 02:00)
        if (endTime < startTime)
        {
            // 如果結束時間比開始時間早，代表這是一個跨午夜的時段
            // 此時，只要「比開始時間晚」或「比結束時間早」都算在範圍內
            return currentTime >= startTime || currentTime <= endTime;
        }
        // 標準的日內情況
        return currentTime >= startTime && currentTime <= endTime;
    }

    // 判斷目前時間是否剛好在策略的頻率點上
    private bool IsOnInterval(DateTime currentTime, int intervalMinutes)
    {
        if (intervalMinutes <= 0) return false;
        // 計算從午夜零點到現在總共過了多少分鐘
        var totalMinutesSinceMidnight = (int)currentTime.TimeOfDay.TotalMinutes;
        // 如果總分鐘數是頻率的整倍數，就代表時間到了
        return totalMinutesSinceMidnight % intervalMinutes == 0;
    }

    // 處理單一策略的通知邏輯
    private async Task ProcessPolicyNotificationAsync(MonitoringPolicy policy, Dictionary<string, WebsiteCheckResult> allResults)
    {
        try
        {
            var policyResults = new List<WebsiteCheckResult>();

            // 篩選出屬於此策略的網站結果
            if (policy.Websites != null)
            {
                foreach (var url in policy.Websites)
                {
                    if (string.IsNullOrWhiteSpace(url)) continue; // Skip empty/null URLs

                    if (allResults.TryGetValue(url, out var result))
                    {
                        policyResults.Add(result);
                    }
                }
            }

            // 根據設定過濾結果
            // 若 IsSuccessNotificationEnabled 為 false，則只保留異常的結果
            var resultsToReport = policy.IsSuccessNotificationEnabled 
                ? policyResults 
                : policyResults.Where(r => !r.IsHealthy).ToList();

            // 若沒有需要回報的項目 (例如：不通知成功且所有網站都正常)，則直接結束
            if (!resultsToReport.Any())
            {
                return;
            }

            // 組裝通知內容
            bool hasFailure = resultsToReport.Any(r => !r.IsHealthy);
            string title = hasFailure 
                ? $"🚨 [{policy.PolicyName}] 網站異常警報" 
                : $"✅ [{policy.PolicyName}] 網站檢查正常";
            
            // 建立 HTML 表格內容
            // 建立 HTML 表格內容 (美化版)
            var messageBuilder = new StringBuilder();
            
            // 摘要資訊
            int total = resultsToReport.Count;
            int failed = resultsToReport.Count(r => !r.IsHealthy);
            string summaryColor = hasFailure ? "#d13438" : "#107c10"; // 紅 : 綠
            
            messageBuilder.Append($"<div style='margin-bottom: 10px; font-size: 14px;'>");
            messageBuilder.Append($"<strong>檢查時間：</strong> {DateTime.Now:yyyy-MM-dd HH:mm:ss}<br/>");
            messageBuilder.Append($"<strong>檢測結果：</strong> 共 {total} 個網站，<span style='color:{summaryColor}; font-weight:bold;'>{failed} 個異常</span>");
            messageBuilder.Append($"</div>");

            // 表格開始
            messageBuilder.Append("<table style='border-collapse: collapse; width: 100%; font-family: Segoe UI, sans-serif; border: 1px solid #e0e0e0; border-radius: 4px; overflow: hidden;'>");
            
            // 表頭
            messageBuilder.Append("<tr style='background-color: #f0f0f0; text-align: left;'>");
            messageBuilder.Append("<th style='padding: 8px; border-bottom: 2px solid #d0d0d0;'>網站</th>");
            messageBuilder.Append("<th style='padding: 8px; border-bottom: 2px solid #d0d0d0; width: 80px;'>狀態</th>");
            messageBuilder.Append("<th style='padding: 8px; border-bottom: 2px solid #d0d0d0;'>詳細訊息</th>");
            messageBuilder.Append("</tr>");

            // 內容列
            foreach (var result in resultsToReport)
            {
                string rowBg = result.IsHealthy ? "#ffffff" : "#fff4f4"; // 異常時底色微紅
                string statusColor = result.IsHealthy ? "#107c10" : "#d13438";
                string statusIcon = result.IsHealthy ? "✔" : "✘";
                string statusText = result.IsHealthy ? "正常" : "異常";
                string link = $"<a href='{result.Url}' style='text-decoration: none; color: #0078d4;'>{result.Url}</a>";

                messageBuilder.Append($"<tr style='background-color: {rowBg}; border-bottom: 1px solid #e0e0e0;'>");
                messageBuilder.Append($"<td style='padding: 8px;'>{link}</td>");
                messageBuilder.Append($"<td style='padding: 8px; color: {statusColor}; font-weight: bold;'>{statusIcon} {statusText}</td>");
                messageBuilder.Append($"<td style='padding: 8px; color: #666;'>{result.Message}</td>");
                messageBuilder.Append("</tr>");
            }
            messageBuilder.Append("</table>");

            // 發送通知
            await _teamsNotifier.SendTeamsNotification(policy.TargetChannel, title, messageBuilder.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[監控-{InstanceId}] 處理策略 '{PolicyName}' 通知時發生錯誤", _instanceId.ToString().Substring(0, 8), policy.PolicyName);
        }
    }

    // 內部結果類別
    private class WebsiteCheckResult
    {
        public string Url { get; set; } = "";
        public bool IsHealthy { get; set; }
        public string Message { get; set; } = "";
    }

    // CheckWebsitesInParallelAsync 改為回傳結果字典
    private async Task<Dictionary<string, WebsiteCheckResult>> CheckWebsitesInParallelAsync(IEnumerable<string> urls)
    {
        _logger.LogInformation("[監控-{InstanceId}] 啟動並行網站健康檢查...", _instanceId.ToString().Substring(0, 8));
        
        var tasks = urls.Select(url => CheckWebsiteAsync(url));
        var results = await Task.WhenAll(tasks); // 所有 TASK 會同時發送，不會互相等待。

        var resultMap = results.ToDictionary(r => r.Url, r => r);

        _logger.LogInformation("[監控-{InstanceId}] 並行網站健康檢查完成", _instanceId.ToString().Substring(0, 8));
        return resultMap;
    }

    // CheckWebsiteAsync 改為回傳單一結果
    private async Task<WebsiteCheckResult> CheckWebsiteAsync(string url)
    {
        int retry = 3;
        int finalRetry = retry - 1;
        
        for (int i = 0; i < retry; i++)
        {
            try
            {
                using var client = _httpClientFactory.CreateClient();
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Monitoring/1.0 (feb.gov.tw)");
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await client.SendAsync(request, cts.Token);

                int statusCode = (int)response.StatusCode;
                bool isHealthy = (statusCode >= 200 && statusCode <= 399) || 
                                 statusCode == 401 || 
                                 statusCode == 403;

                if (isHealthy)
                {
                    _logger.LogInformation("  ✓ 網站 '{Url}' 正常運行 (回應碼: {StatusCode})", url, response.StatusCode);
                    return new WebsiteCheckResult 
                    { 
                        Url = url, 
                        IsHealthy = true, 
                        Message = $"回應碼: {response.StatusCode}" 
                    };
                }
                else
                {
                    _logger.LogWarning("  ⚠ 網站 '{Url}' 回應碼 {StatusCode}，第 {Attempt} 次嘗試", url, response.StatusCode, i + 1);
                    if (i == finalRetry)
                    {
                        return new WebsiteCheckResult 
                        { 
                            Url = url, 
                            IsHealthy = false, 
                            Message = $"回應碼: {response.StatusCode}" 
                        };
                    }
                }
            }
            catch (Exception) when (i < finalRetry)
            {
                // 發生錯誤但還有重試機會，忽略錯誤並等待後重試
                await Task.Delay(1000);
            }
            catch (Exception ex) when (i == finalRetry)
            {
                _logger.LogError("  ✗ 無法連接網站 '{Url}'，錯誤：{ErrorMessage}", url, ex.Message);
                return new WebsiteCheckResult 
                { 
                    Url = url, 
                    IsHealthy = false, 
                    Message = $"無法存取: {ex.Message}" 
                };
            }
        }
        
        // 理論上迴圈內一定會回傳結果 (成功或最後一次重試失敗)，
        // 但編譯器無法判斷迴圈一定會終止並回傳，因此需要這個預設回傳值以滿足語法要求。
        // 此行代碼在正常邏輯下永遠不會被執行。
        return new WebsiteCheckResult { Url = url, IsHealthy = false, Message = "Unknown Error (Unreachable Code)" };
    }
}
