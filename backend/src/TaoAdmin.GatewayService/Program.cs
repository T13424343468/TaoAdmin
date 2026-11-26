using Microsoft.AspNetCore.RateLimiting;
using Nacos;
using Nacos.AspNetCore.V2;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Sinks.Elasticsearch;
using System.Threading.RateLimiting;
using Yarp.ReverseProxy.Transforms;
using TaoAdmin.GatewayService;

var builder = WebApplication.CreateBuilder(args);

// ==================== Serilog：控制台 + JSON 文件（ES 可选） ====================
var esUrl = builder.Configuration["Elasticsearch:Uri"];
var gatewayLogCfg = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "taoadmin-gateway")
    .WriteTo.Console()
    .WriteTo.File(new Serilog.Formatting.Compact.CompactJsonFormatter(),
        Path.Combine(builder.Configuration["Monitor:LogDir"] ?? "logs", "gateway.jsonl"), shared: true);
if (!string.IsNullOrWhiteSpace(esUrl))
    gatewayLogCfg.WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(esUrl))
    {
        AutoRegisterTemplate = true,
        IndexFormat = "taoadmin-gateway-{0:yyyy.MM.dd}",
        EmitEventFailure = Serilog.Sinks.Elasticsearch.EmitEventFailureHandling.WriteToSelfLog
    });
Log.Logger = gatewayLogCfg.CreateLogger();
builder.Host.UseSerilog();

// ==================== Nacos（可选开关，本地/沙箱可关闭） ====================
if (builder.Configuration.GetValue<bool>("Nacos:Enabled"))
{
    builder.Configuration.AddNacosV2Configuration(builder.Configuration.GetSection("NacosConfig"));
    builder.Services.AddNacosAspNet(builder.Configuration, "Nacos");
}

// ==================== YARP 反向代理 ====================
var routes = builder.Configuration.GetSection("ReverseProxy:Routes").GetChildren();
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(ctx =>
    {
        // 向下游服务透传用户上下文与链路标识
        ctx.AddRequestTransform(async t =>
        {
            var traceId = t.HttpContext.TraceIdentifier;
            t.ProxyRequest.Headers.Add("X-Trace-Id", traceId);
            if (t.HttpContext.User.Identity?.IsAuthenticated == true)
            {
                var userId = t.HttpContext.User.FindFirst("UserId")?.Value ?? "";
                var userName = t.HttpContext.User.FindFirst("UserName")?.Value ?? "";
                t.ProxyRequest.Headers.Add("X-User-Id", userId);
                t.ProxyRequest.Headers.Add("X-User-Name", Uri.EscapeDataString(userName));
            }
            await Task.CompletedTask;
        });
    });

// ==================== JWT 认证（网关统一鉴权） ====================
var jwt = builder.Configuration.GetSection("Jwt");
// SecretKey 缺失即启动失败（fail-fast）：静默回退默认值会让令牌签名可预测，属安全红线
var jwtSecretKey = jwt["SecretKey"];
if (string.IsNullOrWhiteSpace(jwtSecretKey))
    throw new InvalidOperationException("配置缺失：Jwt:SecretKey 未设置（网关启动失败，请补齐配置后重启）");
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwt["Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes(jwtSecretKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });
builder.Services.AddAuthorization();

// ==================== 限流（固定窗口，可配置；按登录用户/IP 分桶计数） ====================
// 配置节 RateLimiting：Enabled 开关、PermitLimit 窗口内最大请求数、WindowSeconds 窗口时长。
// 已登录按 UserId 分桶（一人超速不影响他人），未登录按 IP 分桶。超速返回 429。
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.ContentType = "application/json";
        await ctx.HttpContext.Response.WriteAsJsonAsync(new
        {
            code = 429, success = false, message = "请求太频繁，请稍后再试（限流保护中）"
        }, ct);
    };
    options.AddPolicy("global", context =>
    {
        // 限流中间件注册在认证之前（UseRateLimiter 先于 UseAuthentication），
        // 进入分桶时 context.User 恒为未认证主体，按 UserId 分桶在当前管道不可达——
        // 统一按客户端 IP 分桶；分桶键口径见 GatewayRateLimitKey（含无远端地址兜底）。
        var permit = builder.Configuration.GetValue<int>("RateLimiting:PermitLimit", 100);
        var window = builder.Configuration.GetValue<int>("RateLimiting:WindowSeconds", 10);
        return RateLimitPartition.GetFixedWindowLimiter(GatewayRateLimitKey.From(context.Connection.RemoteIpAddress), _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = permit,
                Window = TimeSpan.FromSeconds(window),
                QueueLimit = 0
            });
    });
});

// ==================== 跨域 ====================
// 允许来源从配置 Cors:AllowedOrigins 读取（逗号分隔）；未配置则不允许跨域（同源部署经 Nginx 反代，无需 CORS）
var corsOrigins = builder.Configuration["Cors:AllowedOrigins"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (!string.IsNullOrWhiteSpace(corsOrigins))
    {
        var origins = corsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (origins.Length > 0)
            p.WithOrigins(origins);
    }
    p.AllowAnyHeader().AllowAnyMethod();
}));

// ==================== 资源监控（②）：本机指标 + 全服务聚合 ====================
builder.Services.AddHttpClient();
builder.Services.AddSingleton<GatewayResourceCollector>();
builder.Services.AddSingleton<ResourceAggregationService>();

// 接口文档：官方内置 OpenAPI + Scalar UI（网关本身仅暴露 /health 与反向代理，文档入口仅作说明页）
builder.Services.AddOpenApi("v1", o =>
    o.AddDocumentTransformer((doc, ctx, ct) =>
    {
        doc.Info = new Microsoft.OpenApi.OpenApiInfo
        {
            Title = "TaoAdmin 网关（YARP）",
            Version = "v1",
            Description = "网关本身仅暴露 /health 与反向代理；业务接口文档见各服务 /scalar：" +
                          "权限服务 http://localhost:5001/scalar 、任务服务 http://localhost:5002/scalar"
        };
        return Task.CompletedTask;
    }));

var app = builder.Build();

// ==================== 中间件管道 ====================
app.UseSerilogRequestLogging();
app.UseCors();
if (builder.Configuration.GetValue<bool>("RateLimiting:Enabled", true))
    app.UseRateLimiter();
// 健康检查端点（供监控中心探测）
app.MapGet("/health", () => Results.Ok(new { status = "UP", service = "taoadmin-gateway" }));
// 资源指标端点（②）：本进程 CPU/内存/磁盘/网络快照，供网关聚合与监控中心采样（要求已认证，防匿名探测服务器资源）
app.MapGet("/metrics/resources", (GatewayResourceCollector collector) =>
    Results.Ok(collector.Collect())).RequireAuthorization();
// 全服务资源聚合（②）：逐服务拉取指标汇总成一份视图（要求已认证）
app.MapGet("/metrics/aggregate", async (ResourceAggregationService aggregation, CancellationToken ct) =>
    Results.Ok(await aggregation.AggregateAsync(ct))).RequireAuthorization();
// 登录/刷新接口放行，其余要求已认证
app.UseAuthentication();
app.UseAuthorization();
// 熔断保护：下游连续失败达到阈值后短暂熔断，防止故障扩散（见 CircuitBreakerMiddleware）
if (builder.Configuration.GetValue<bool>("CircuitBreaker:Enabled", true))
    app.UseMiddleware<TaoAdmin.Common.Resilience.CircuitBreakerMiddleware>();
app.MapOpenApi(); // /openapi/v1.json
// ⑥ 文档访问限制：默认仅本机可看，站点配置 DocsAccess:Mode 可放宽为登录后可看（须在文档端点之前）
app.UseMiddleware<TaoAdmin.Common.Web.ApiDocsAccessMiddleware>();
app.MapScalarApiReference(c => c.WithTitle("TaoAdmin 网关")); // /scalar 可视化文档
app.MapReverseProxy().RequireRateLimiting("global");

Log.Logger.Information("TaoAdmin.GatewayService 启动，端口 {Port}", 5000);
app.Run();
