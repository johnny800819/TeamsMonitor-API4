namespace API4_TEAMS.Middleware
{
    public class ApiKeyMiddleware
    {
        private readonly RequestDelegate _next;

        private readonly IConfiguration _configuration;
        private readonly ILogger<ApiKeyMiddleware> _logger;

        public ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<ApiKeyMiddleware> logger)
        {
            _next = next;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // 只保護 /api/ 開頭的路由
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                await _next(context);
                return;
            }

            // 嘗試從 HTTP Header 中讀取 X-API-Key
            if (!context.Request.Headers.TryGetValue("X-API-Key", out var extractedApiKey))
            {
                context.Response.StatusCode = 401; // Unauthorized
                await context.Response.WriteAsync("API Key was not provided.");
                return;
            }

            // 取得設定檔中所有合法的 API Keys
            var apiKeys = _configuration.GetSection("Authentication:ApiKeys").Get<List<ApiKeySetting>>();

            // 檢查傳入的 Key 是否存在於我們的合法列表中，並找出對應的使用者
            var matchedClient = apiKeys?.FirstOrDefault(apiKey => apiKey.Key == extractedApiKey);

            if (matchedClient == null)
            {
                _logger.LogWarning("API Key 驗證失敗: 無效的 Key");
                context.Response.StatusCode = 401; // Unauthorized
                await context.Response.WriteAsync("Invalid API Key.");
                return;
            }

            // 記錄是哪個 Client 正在呼叫
            _logger.LogInformation("Client '{ClientName}' 通過驗證", matchedClient.ClientName);
            
            // 將 ClientName 存入 HttpContext，供後續 Controller 使用
            context.Items["ClientName"] = matchedClient.ClientName;

            // 如果金鑰驗證成功，則繼續處理請求
            await _next(context);
        }
    }

    // 輔助類別，用來對應 appsettings.json 中的結構
    public class ApiKeySetting
    {
        public string ClientName { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
    }
}

