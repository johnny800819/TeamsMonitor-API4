using Serilog;
using API4_TEAMS.Middleware;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args); //在 modern .NET (.NET 6+) 中，WebApplication.CreateBuilder(args) 已經預設會幫我們載入 appsettings.json 和 appsettings.{Environment}.json

builder.Services.AddControllers();

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // 定義 API Key 安全性方案
    // 這是為了讓 Swagger UI 能夠支援輸入 API Key (X-API-Key)
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "請輸入 API Key (格式: {Key})",
        Name = "X-API-Key",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "ApiKeyScheme"
    });

    // 套用安全性需求到所有 API
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                },
                Scheme = "ApiKeyScheme",
                Name = "ApiKey",
                In = ParameterLocation.Header
            },
            new List<string>()
        }
    });
});

builder.Services.AddHttpClient(); // 註冊 IHttpClientFactory，這是建立 HttpClient 的最佳實踐，可避免 Socket 耗盡問題
builder.Services.AddSingleton<TeamsNotifier>(); // 將 TeamsNotifier 註冊為 Singleton 服務，因為它被 Singleton 的 HostedService 使用，必須有相同或更長的生命週期

// 註冊 WebsiteMonitorService 為背景服務 (Hosted Service)。
// ASP.NET Core 會在應用程式啟動時自動建立並執行所有註冊的 IHostedService/BackgroundService，
// 不需手動呼叫，服務會在應用程式存活期間持續運作。
builder.Services.AddHostedService<WebsiteMonitorService>();

// 設定 Serilog - 排除 Microsoft 日誌
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()

    // 排除 Microsoft 和 System 的 log
    //.MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    //.MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)

    // 輸出到 Console  
    .WriteTo.Console()

    // 只記錄非 Microsoft/System 的 log 到 app.log
    .WriteTo.Logger(lc => lc
        .Filter.ByExcluding(logEvent =>
            logEvent.Properties.TryGetValue("SourceContext", out var sourceContext) &&
            (sourceContext.ToString().Contains("Microsoft") ||
             sourceContext.ToString().Contains("System")))
        .WriteTo.File("Logs/app.log",
            rollingInterval: RollingInterval.Day,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"))

    // 只記錄 Microsoft/System 相關 log 到 system.log
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(logEvent =>
            logEvent.Properties.TryGetValue("SourceContext", out var sourceContext) &&
            (sourceContext.ToString().Contains("Microsoft") ||
             sourceContext.ToString().Contains("System")))
        .WriteTo.File("Logs/system.log",
            rollingInterval: RollingInterval.Day, // 新增：每天產生一個新檔
            rollOnFileSizeLimit: true,            // 新增：啟用檔案大小限制
            fileSizeLimitBytes: 2 * 1024 * 1024,  // 新增：檔案大小上限設為 2MB
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"))
    .CreateLogger();

// 清除預設的 logging providers
builder.Logging.ClearProviders();

// 使用 Serilog(替換內建的 Logger)
builder.Host.UseSerilog();

var app = builder.Build();

// 使用 Serilog 請求日誌記錄
app.UseSerilogRequestLogging();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

app.UseRouting(); // 確保 UseRouting 在 UseMiddleware 之前

// 啟用我們的 API 金鑰驗證中介軟體 (它應該在路由之後、授權和端點之前)
app.UseMiddleware<ApiKeyMiddleware>();

app.UseAuthorization();

app.MapControllers();

try
{
    Log.Information("應用程式啟動");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "應用程式啟動失敗");
}
finally
{
    Log.CloseAndFlush();
}

