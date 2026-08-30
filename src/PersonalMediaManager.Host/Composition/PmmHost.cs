using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.System;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.DependencyInjection;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Host.Controllers.Diagnostics;
using PersonalMediaManager.Host.HostedServices;
using PersonalMediaManager.Host.Hubs;
using PersonalMediaManager.Host.Logging;
using PersonalMediaManager.Host.Middleware;
using PersonalMediaManager.Host.Security;
using PersonalMediaManager.Infrastructure.External.DependencyInjection;
using PersonalMediaManager.Infrastructure.Persistence.DependencyInjection;
using PersonalMediaManager.Infrastructure.Platform.DependencyInjection;
using PersonalMediaManager.Infrastructure.Platform.Security;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

namespace PersonalMediaManager.Host.Composition;

/// <summary>PmmHost 装配工厂：返回可由 Launcher / 测试夹具直接 Run() 的 WebApplication</summary>
/// <remarks>
/// 阶段 A 最小集：Kestrel 监听 Web.Port（默认 DefaultPort：Release 7288 / Debug 47333） + 路由 + PmmDbContext + /health Controller。
/// 后续阶段在此处追加：Serilog（B3）/ JWT（B4）/ DataProtection（B5）/ SignalR + Hosted Services（D6）/ Scalar（C3.1）等。
/// Launcher 通过 PmmHost.CreateApp(args, AppPaths.Resolve()) 启动；测试夹具用 WebApplicationFactory 重写 ConfigureServices。
/// </remarks>
public static class PmmHost
{
#if DEBUG
    // 测试/调试构建默认端口：与正式环境默认端口 7288 错开，避免本机同时运行调试实例
    // 与已安装的正式实例时争抢同一端口（仅影响 Debug 构建；Release 发版仍为 7288）。
    public const int DefaultPort = 47333;
#else
    public const int DefaultPort = 7288;
#endif

    public static WebApplication CreateApp(string[] args, AppPaths paths,
        Action<IWebHostBuilder>? webHostOverride = null,
        Action<IServiceCollection>? servicesOverride = null)
    {
        // r3 P3-r3.20：防御性 paths 验证（Launcher 调用前已保证；这里兜底单测 / 直调）
        // 启动期目录缺失会让 Serilog.File / DataProtection PersistKeysToFileSystem 在运行时迟到爆炸，
        // 报错位置远离根因；前置校验把根因暴露在 PmmHost.CreateApp 第一帧
        ValidatePaths(paths);

        // 端口回退留痕：首选端口不可绑定时 ResolveBindablePort 会换端口，在 logger 就绪后于启动 banner 处告警
        int? portFallbackFrom = null;

        // 必须在 AddJwtBearer 之前清掉默认 claim mapping，否则 "role" 被自动映射为 ClaimTypes.Role 的长 URI，
        // 导致 TokenValidationParameters.RoleClaimType="role" 失效 → RequireRole("Admin") 403
        System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

        // ContentRoot 固定为程序集所在目录：Launcher 托盘启动 / 开机自启时工作目录不可控
        // （可能是 System32 或用户目录），不显式锁定则默认取当前工作目录，UseStaticFiles
        // 在错误位置找 wwwroot → 找不到便空转 → 根路径落到 SetupGuard 返回 JSON 而非前端页面
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // 接入 local.json 覆盖层（详见 docs/需求规范-启动方式与托盘常驻.md §3）
        // 优先级（由低到高）：appsettings.json → appsettings.{Env}.json → local.json → 环境变量 → 命令行
        // 必须用绝对路径：builder.Configuration 默认根目录是 ContentRootPath（单文件发布下是 %TEMP%\.net\... 解压目录），相对路径会找错位置
        // 必须在 GetValue("Web:Port", ...) 之前接入，否则端口覆盖无效
        // 文件名 "local.json" 与 Launcher.LocalConfigStore.FileName 同步保持（Host 不引 Launcher，写字面量避免反向依赖）
        // appsettings.json 已嵌入 Host 程序集（单文件 exe 无外置散文件，CLAUDE.md §十二 单 exe 收敛）：
        // 从嵌入资源读取作为出厂默认配置，插到 sources 最前 = 最低优先级（被 local.json / 环境变量 / 命令行覆盖）。
        // 默认 WebApplicationBuilder 已尝试加载物理 appsettings.json（现已无此散文件，optional 静默跳过），故这里补嵌入源。
        InsertEmbeddedAppSettings(builder);

        string localConfigPath = Path.Combine(paths.Root, "local.json");
        builder.Configuration.AddJsonFile(localConfigPath, optional: true, reloadOnChange: false);

        if (webHostOverride is not null)
        {
            // 测试场景：调用方注入 .UseTestServer() 等替换 Kestrel
            webHostOverride(builder.WebHost);
        }
        else
        {
            // 端口 = local.json override → 回退 DefaultPort（Release 7288 / Debug 47333）；
            // 绑定前探测可用性：撞占用 / 系统 TCP 保留段（SocketException 10013/10048）时自动回退备用端口 → 最终 OS 自动分配，
            // 避免整机因单个端口不可用而彻底启动失败（历史事故：默认端口落入 Hyper-V/WSL 动态保留段 → Kestrel bind 10013 → 启动失败）
            int preferred = builder.Configuration.GetValue("Web:Port", DefaultPort);
            int port = ResolveBindablePort(preferred);
            if (port != preferred)
            {
                // 写回内存配置（最高优先级覆盖）：让托盘 / 其它读 Web:Port 处拿到实际生效端口
                builder.Configuration["Web:Port"] = port.ToString(CultureInfo.InvariantCulture);
                portFallbackFrom = preferred;
            }
            builder.WebHost.ConfigureKestrel(opts => opts.ListenAnyIP(port));
        }

        // Serilog：Console + File（按天 + 10MB 切割 + 30 天保留）+ SignalRSink（B3.2）
        builder.Host.UseSerilog((ctx, sp, cfg) =>
        {
            string logDir = paths.LogDir;
            cfg.MinimumLevel.Information()
               .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
               .Enrich.FromLogContext()
               .WriteTo.Console()
               .WriteTo.File(
                    path: Path.Combine(logDir, "pmm-.log"),
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 10L * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 30,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj} {NewLine}{Exception}")
               .WriteTo.Sink(new SignalRSink(sp));
        });

        // 数据库连接串：优先读 ConnectionStrings:Default（被 local.json 覆盖时由 LocalConfigStore.SetOverrideDbPath 写入），
        // 回退 $"Data Source={paths.DbFile}" 默认值。让 Launcher「配置数据库位置…」菜单切换 db 后重启即生效（规范 §6.4）
        string connectionString = builder.Configuration.GetConnectionString("Default")
            ?? $"Data Source={paths.DbFile}";
        builder.Services.AddInfrastructurePersistence(connectionString);

        // 运行时生效的主库路径（override 感知）：从最终连接串解析，供导出 / 导入暂存 / 容量统计共用，
        // 避免用户用托盘「配置数据库位置…」改库后读写错库（详见 DatabaseFileLocation / ImportStaging）
        string dbFilePath = ResolveDbFilePath(connectionString, paths);
        builder.Services.AddSingleton(new DatabaseFileLocation(dbFilePath));

        // Application + Infrastructure.Platform（IClock / IJwtTokenService / IPasswordHasher / IJwtSigningKeyProvider / IProtectedFieldService）
        builder.Services.AddApplication();
        builder.Services.AddSingleton(paths);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(paths.KeyRingDir))
            .SetApplicationName("PersonalMediaManager")
            .SetDefaultKeyLifetime(TimeSpan.FromDays(365 * 5));
        builder.Services.AddInfrastructurePlatform();
        builder.Services.AddHttpClient();
        // 命名 HttpClient（r2 P2-r2.3）：与 Tmdb / Ai 等风格统一，便于后续在此处集中配置 Polly 策略
        builder.Services.AddHttpClient(HostedServices.WebhookOutboxWorker.HttpClientName);
        builder.Services.AddInfrastructureExternal();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        // JWT 认证（HS256，匿名清单由 [AllowAnonymous] 标记，详见需求文档 §3.12）
        // r1 P3-5：用 IPostConfigureOptions<JwtBearerOptions> 替代旧 BuildServiceProvider() 反模式；
        // 实际配置见 Security/JwtBearerOptionsConfigurator.cs，让 IJwtSigningKeyProvider 在 ServiceProvider 构造完后再解析
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        builder.Services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, Security.JwtBearerOptionsConfigurator>();

        // 全局默认 [Authorize(Roles=Admin)]；公开端点显式 [AllowAnonymous]
        builder.Services.AddAuthorization(opts =>
        {
            opts.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireRole(UserRole.Admin.ToString())
                .Build();
        });

        // 登录端点限速（防口令在线爆破）：仅生产启用——测试场景（webHostOverride 注入 TestServer）跳过，
        // 避免多测试共享进程内固定窗口累积触发限流导致 flaky。按来源 IP 每分钟 ≤10 次；
        // 超限走 OnRejected 返 200 + code:1000 信封（与全站 §0.6「200 + code」约定一致，前端 unwrap 可直接提示）。
        // 策略名 "login" 与 AuthController.Login 的 [EnableRateLimiting] 同步。
        if (webHostOverride is null)
        {
            builder.Services.AddRateLimiter(opts =>
            {
                opts.AddPolicy("login", httpContext =>
                    System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                        {
                            Window = TimeSpan.FromMinutes(1),
                            PermitLimit = 10,
                            QueueLimit = 0,
                        }));
                opts.OnRejected = async (context, ct) =>
                {
                    HttpContext http = context.HttpContext;
                    if (http.Response.HasStarted) return;
                    http.Response.StatusCode = StatusCodes.Status200OK;
                    http.Response.ContentType = "application/json; charset=utf-8";
                    string requestId = http.Items.TryGetValue(RequestIdMiddleware.ItemsKey, out object? rid)
                        ? rid?.ToString() ?? string.Empty : string.Empty;
                    await http.Response.WriteAsync(
                        $"{{\"code\":{ApiCode.BusinessError},\"message\":\"登录尝试过于频繁，请稍后再试\",\"data\":null,\"requestId\":\"{requestId}\"}}", ct);
                };
            });
        }

        // CORS（r2 P2-r2.1）：默认禁用（生产场景 Launcher 单进程同源）；
        // 仅当 appsettings.json Web:CorsOrigins 显式配置非空数组时启用，给 vite dev server 等开发期跨域用。
        // 例：local.json 加 { "Web": { "CorsOrigins": [ "http://localhost:5173" ] } } 即放行 vite。
        string[] corsOrigins = builder.Configuration.GetSection("Web:CorsOrigins").Get<string[]>() ?? [];
        if (corsOrigins.Length > 0)
        {
            builder.Services.AddCors(opts => opts.AddPolicy("PmmCors", policy =>
                policy.WithOrigins(corsOrigins)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials()
                      .WithExposedHeaders("X-Request-Id")));
        }

        // SignalR + MVC（Scalar 在 C3.1 接入）；Entry Assembly = Launcher，必须显式注册 Host 程序集为 ApplicationPart
        builder.Services.AddSignalR()
            .AddJsonProtocol(opts => opts.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        // D8.3 任务推送：Replace Application 默认的 NullTaskNotifier 为 SignalR 实现
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
            .Replace(builder.Services,
                Microsoft.Extensions.DependencyInjection.ServiceDescriptor
                    .Singleton<PersonalMediaManager.Application.Contracts.ITaskNotifier, PersonalMediaManager.Host.Hubs.SignalRTaskNotifier>());
        builder.Services.AddControllers(opts => opts.Conventions.Add(new ApiRoutePrefixConvention()))
            .AddApplicationPart(typeof(HealthController).Assembly)
            .AddJsonOptions(opts =>
            {
                // 枚举默认按 number 序列化；前端按字符串更可读（UserRole.Admin → "Admin"）
                opts.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            });

        // 档 2 边界校验：[ApiController] 模型绑定阶段 DataAnnotations 失败 → 统一 {code:1000} 信封
        // （替代默认 400 ProblemDetails，保持 §0.6「200 + code」约定）；A 类字段校验由此承接，不再抛 BusinessException
        builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(opts =>
            opts.InvalidModelStateResponseFactory = ValidationEnvelopeFactory.Create);

        // OpenAPI 文档（.NET 10 内置 Microsoft.AspNetCore.OpenApi）+ Scalar UI（C3.1）
        // 文档路由：/openapi/v1.json；UI：/scalar/v1
        // r1 P3-6：SchemaTransformer 把所有枚举从 number 改为 string + 显式枚举值清单，
        // 与运行时 JsonStringEnumConverter 行为对齐（避免前端 schema.d.ts 拿到 number 类型导致 IDE 误报）
        builder.Services.AddOpenApi(opts =>
        {
            opts.AddSchemaTransformer((schema, context, _) =>
            {
                Type t = context.JsonTypeInfo.Type;
                Type underlying = Nullable.GetUnderlyingType(t) ?? t;
                if (underlying.IsEnum)
                {
                    schema.Type = Microsoft.OpenApi.JsonSchemaType.String;
                    schema.Format = null;
                    schema.Enum = underlying.GetEnumNames()
                        .Select(n => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(n)!)
                        .ToList();
                }
                return Task.CompletedTask;
            });
        });

        // 后台 Worker（D.6）：FileWatcherWorker 投递 + TaskProcessorWorker 单消费 + WebhookOutboxWorker 出站 + NetworkShareMonitorWorker 可达性
        // StartupRecoveryWorker 必须最先注册（HostedService 按注册顺序串行 StartAsync）；
        // 它实现 IHostedService 而非 BackgroundService（r3 P1-r3.14）：StartAsync 同步阻塞至 Archiving 孤儿恢复完成才返回，
        // Host 之后才依次启动 FileWatcherWorker 等后续 Worker，彻底消除「恢复未完成 vs 新文件入队」竞争
        // MediaExtensionCacheInitializer 必须在 StartupRecoveryWorker 之前：确保 FileWatcherWorker 启动时缓存已就绪
        builder.Services.AddHostedService<MediaExtensionCacheInitializer>();
        builder.Services.AddHostedService<StartupRecoveryWorker>();
        builder.Services.AddHostedService<FileWatcherWorker>();
        builder.Services.AddHostedService<TaskProcessorWorker>();
        builder.Services.AddHostedService<WebhookOutboxWorker>();
        builder.Services.AddHostedService<NetworkShareMonitorWorker>();
        // 归档盘剩余百分比周期巡检（5 分钟）：跌破 Archive_DiskWarn/CriticalPercent 经 IAlertService 发 disk.low，
        // 补齐「磁盘缓慢填满但无归档活动时永无预警」缺口（健康检查为被动端点，不主动通知）
        builder.Services.AddHostedService<DiskSpaceAlertWorker>();
        // D9.1 升级检查：启动延迟 60s 首检 + 每 db Update_CheckIntervalHours 周期检查 GitHub /releases/latest
        // 运行时 Enabled / Hours 走 System_Setting；Worker 每轮从 db 重读，UI 改动下一轮生效（关闭态走 5 分钟短轮询）
        builder.Services.AddHostedService<UpdateCheckWorker>();

        // 升级检查 appsettings 配置绑定：仅作为 System_Setting 首次 seed 默认（运行时 UpdateSettingService 从 db 读）
        // PAT 不在此处，走 System_Setting 加密落库
        builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection(UpdateOptions.SectionName));
        // 命名 HttpClient（GitHubUpdateChecker）：BaseAddress 在 Checker 内部硬编码 api.github.com，
        // 此处仅注册名以便 IHttpClientFactory.CreateClient(name) 路由（避免与默认 client 共享 DefaultRequestHeaders）。
        // PrimaryHttpMessageHandler 接入 IProxyResolver(UpdateCheck)：开启代理 + 用户勾选「升级检查走代理」时自动套 PmmWebProxy。
        builder.Services.AddHttpClient(PersonalMediaManager.Infrastructure.External.Update.GitHubUpdateChecker.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => new System.Net.Http.SocketsHttpHandler
            {
                UseProxy = true,
                Proxy = sp.GetRequiredService<PersonalMediaManager.Application.Contracts.IProxyResolver>()
                    .CreateWebProxy(PersonalMediaManager.Application.Contracts.ProxyConsumer.UpdateCheck),
            });

        // 测试夹具最后一次 service 覆盖（替换 ITmdbSearchService / IAiCallOrchestrator 等为 fake）
        servicesOverride?.Invoke(builder.Services);

        WebApplication app = builder.Build();

        // 应用「待生效的导入暂存包」：必须在任何 DbContext 连接打开之前换库
        // （WAL 模式下运行中热替换主库会被旧 -wal/-shm 回写覆盖 → 导入不生效；故改为启动期换库）。
        // 此处位于 Build 之后、下方 Migrate / 首个查询之前，恰是「无任何连接打开」的安全窗口。
        ImportStaging.ApplyPendingIfAny(dbFilePath, msg =>
            app.Services.GetRequiredService<ILoggerFactory>()
               .CreateLogger("PersonalMediaManager.Host.ImportSwap")
               .LogInformation("{ImportSwap}", msg));

        // 启动期版本号 banner：4 套版本号 + commit + framework，方便日志 / 巡检脚本一眼定位部署的是哪个版本
        // dirty=true 升 Warning（说明二进制带未提交改动，不可被任何 commit 复现），其它正常 Information
        // 用 ILoggerFactory.CreateLogger 拿 Microsoft.Extensions.Logging.ILogger，避免 app.Logger 被 using Serilog 误解析为 Serilog.ILogger
        try
        {
            IVersionInfoProvider versionInfo = app.Services.GetRequiredService<IVersionInfoProvider>();
            StaticVersionInfo v = versionInfo.GetStatic();
            Microsoft.Extensions.Logging.ILogger startupLogger = app.Services
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("PersonalMediaManager.Host.Startup");
            string banner = $"PersonalMediaManager v{v.Product} (后端 {v.Backend}, 前端 {v.Frontend}, db 目标 {v.DbVersionTarget}) on {v.Framework}";
            if (v.Dirty)
            {
                startupLogger.LogWarning("[启动] {Banner}（二进制带未提交改动 dirty=true，仅适用本地调试）", banner);
            }
            else
            {
                startupLogger.LogInformation("[启动] {Banner}", banner);
            }
            if (portFallbackFrom is int fromPort)
            {
                int actualPort = app.Configuration.GetValue("Web:Port", DefaultPort);
                startupLogger.LogWarning(
                    "[启动] 首选端口 {Preferred} 无法绑定（被占用或落入系统 TCP 保留段），已自动回退到端口 {Actual}；如需固定端口请用托盘「配置端口…」改到可用端口",
                    fromPort, actualPort);
            }
        }
        catch (Exception ex)
        {
            // 启动 banner 失败不阻断 app；用 Serilog 静态 Log 兜底留痕
            Log.Warning(ex, "启动期版本号 banner 输出失败（不阻断服务）");
        }

        // 中间件顺序（外 → 内）：RequestId → SerilogRequestLogging → ExceptionHandler → ...
        //   1) RequestId 最外：先注入统一追踪 ID，让后续所有中间件 / Controller / 日志都能取到
        //   2) SerilogRequestLogging 在 ExceptionHandler 外层：让 ExceptionHandler 把 BusinessException / DomainException
        //      改写为 200/code=1000 之后，Serilog 在 finally 取到的 Response.StatusCode 已经是 200，
        //      不会把业务级失败（密码错误 / 字段校验失败等）误记成 [ERR] 5xx 污染告警通道。
        //   3) ExceptionHandler 在 Serilog 内层：仍能接住所有内层（认证 / 授权 / Controller）抛出的异常。
        // 历史回归：原顺序为 RequestId → ExceptionHandler → Serilog，导致业务异常被 Serilog 看到「异常未处理 →
        // 框架默认 500」并以 [ERR] 级别落盘，客户端虽收到 200 但日志看上去像服务器崩了。
        app.UseMiddleware<RequestIdMiddleware>();

        // SerilogRequestLogging 自定义 EnrichDiagnosticContext：QueryString 走 SensitiveDataRedactor 脱敏
        // 默认行为会把 RequestPath + QueryString 完整记入 log message，未脱敏时 ?api_key=xxx 会落盘
        app.UseSerilogRequestLogging(opts =>
        {
            opts.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                string? qs = httpContext.Request.QueryString.HasValue
                    ? httpContext.Request.QueryString.Value
                    : null;
                diagnosticContext.Set("QueryString", PersonalMediaManager.Host.Logging.SensitiveDataRedactor.Redact(qs));
            };
        });

        app.UseMiddleware<ExceptionHandlerMiddleware>();

        // E.3 前端静态资源（wwwroot 由 vite build 产物注入）
        // 顺序：DefaultFiles → StaticFiles → Controllers → fallback index.html
        //
        // 缓存分级（解决「发新版后必须强刷」）：index.html 是固定名入口，记录当前该加载哪些 hash 资源；
        // 默认中间件不发 Cache-Control，浏览器对它启用启发式缓存 → 普通刷新拿旧 index → 引用旧 hash 资源。
        // 故：index.html 用 no-cache 强制每次校验（命中 ETag 仍是 304，开销极小）；
        // /assets/* 带内容 hash、改名即换文件，安全长缓存 immutable。
        // 前端 wwwroot 已嵌入 Host 程序集（Host.csproj GenerateEmbeddedFilesManifest），用 ManifestEmbeddedFileProvider 读：
        // 单文件发布随 exe 内置、内存直读、不落临时盘。provider 为 null（manifest 缺失 / 前端未构建）→ 不挂静态文件，根路径走占位页。
        IFileProvider? frontendProvider = TryCreateFrontendFileProvider();
        StaticFileOptions staticFileOptions = new()
        {
            OnPrepareResponse = ctx =>
            {
                IHeaderDictionary headers = ctx.Context.Response.Headers;
                if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                    headers.CacheControl = "no-cache";
                else
                    headers.CacheControl = "public, max-age=31536000, immutable";
            },
        };
        if (frontendProvider is not null)
        {
            staticFileOptions.FileProvider = frontendProvider;
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontendProvider });
            app.UseStaticFiles(staticFileOptions);
        }

        // CORS 必须在 UseAuthentication / UseAuthorization 之前（否则跨域 pre-flight 被 401 抢先）；
        // SetupGuard 内部已对 OPTIONS 短路放行，所以两者顺序不冲突
        if (corsOrigins.Length > 0)
        {
            app.UseCors("PmmCors");
        }

        app.UseAuthentication();
        // SetupGuard 放认证之后、授权之前：需读已认证身份（未 setup 时放行已登录 Admin，让向导建账号登录后能调 settings/* 配置）；
        // 又必须在 UseAuthorization 之前，使未 setup 的匿名请求返 1000「请先完成初始化」，而非被 FallbackPolicy(Admin) 401/403
        // 抢先（空 body，前端无法区分要先 setup 还是要登录）。
        app.UseMiddleware<SetupGuardMiddleware>();
        app.UseAuthorization();
        // 限速中间件（读端点 [EnableRateLimiting] 元数据，须在路由解析后、端点执行前）：仅生产启用，当前仅 login 端点
        if (webHostOverride is null)
        {
            app.UseRateLimiter();
        }
        app.UseMiddleware<TokenRefreshMiddleware>();

        app.MapControllers();
        // SignalR Hub 按需求文档 §3.12「匿名访问范围」放行；
        // Hub 类已标 [AllowAnonymous]，此处 MapHub 同步 AllowAnonymous 绕过全局 FallbackPolicy(Admin)。
        app.MapHub<LogHub>(LogHub.Path).AllowAnonymous();
        app.MapHub<TaskHub>(TaskHub.Path).AllowAnonymous();

        // OpenAPI + Scalar UI（开发期默认开放；生产环境若需关闭可加 AppEnv 守卫）
        // AllowAnonymous 让全局 FallbackPolicy(Admin) 不拦截；SetupGuard 白名单也已加 /openapi / /scalar
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference().AllowAnonymous();

        // SPA fallback：未命中静态文件 + Controller + Hub + openapi/scalar 的 GET 请求 → 返 index.html，
        // 让 Vue Router 的 history mode 在子路径（/review /history /...）刷新时仍能正确路由。
        // wwwroot 已嵌入 Host 程序集；前端未构建（provider 无 index.html）时改返占位页。
        if (frontendProvider is not null && frontendProvider.GetFileInfo("index.html").Exists)
        {
            app.MapFallbackToFile("index.html", staticFileOptions).AllowAnonymous();
        }
        else
        {
            app.MapFallback(() => Results.Content(FrontendPlaceholder.Html, "text/html; charset=utf-8")).AllowAnonymous();
        }

        return app;
    }

    /// <summary>探测首选端口能否绑定；不能则依次试备用端口，全不可用时交 OS 自动分配空闲端口（端口 0）</summary>
    /// <remarks>
    /// 应对「端口被其它进程占用」与「端口落入 Windows 动态 TCP 保留段（Hyper-V/WSL/Docker 保留，绑定即 SocketException 10013/WSAEACCES）」两类问题——
    /// 后者即便端口空闲也无法绑定，是历史启动失败事故的根因。探测用短暂 Bind 试探（立即释放），存在极小 TOCTOU 窗口但对持久性保留/占用判定可靠。
    /// 备用端口选与默认端口错开的高端口；若均不可用，端口 0 让 OS 分配保证一定能起（代价是端口漂移，托盘会读实际端口展示）。
    /// </remarks>
    private static int ResolveBindablePort(int preferred)
    {
        if (CanBindTcp(preferred)) return preferred;
        foreach (int candidate in new[] { 8288, 9280, 8760, 7000 })
        {
            if (candidate != preferred && CanBindTcp(candidate)) return candidate;
        }
        return GetFreeTcpPort();
    }

    /// <summary>试绑 0.0.0.0:port 判断可用性（立即释放）；SocketException（占用 10048 / 保留段 10013 等）视为不可用</summary>
    private static bool CanBindTcp(int port)
    {
        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>让 OS 分配一个空闲 TCP 端口（绑定端口 0 后读回实际端口）</summary>
    private static int GetFreeTcpPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>从连接串解析主库 .db 绝对路径；解析不出则回退 AppPaths.DbFile 默认路径</summary>
    /// <remarks>
    /// 连接串形如 "Data Source=&lt;path&gt;[;Cache=Shared;...]"。手工解析而非引 Microsoft.Data.Sqlite，
    /// 与 Launcher.LocalConfigStore.ParseDataSource 行为对齐（二者必须算出同一路径，否则启动期换库会落到错库）。
    /// </remarks>
    private static string ResolveDbFilePath(string connectionString, AppPaths paths)
    {
        const string prefix = "Data Source=";
        int idx = connectionString.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return paths.DbFile;
        }
        string rest = connectionString[(idx + prefix.Length)..];
        int semi = rest.IndexOf(';');
        string ds = (semi >= 0 ? rest[..semi] : rest).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(ds))
        {
            return paths.DbFile;
        }
        try { return Path.GetFullPath(ds); } catch { return paths.DbFile; }
    }

    /// <summary>启动期 AppPaths 防御性校验：Root / LogDir / KeyRingDir 必须可写</summary>
    /// <remarks>
    /// r3 P3-r3.20：Launcher 走 AppPaths.Resolve() 已经 CreateDirectory 兜底，
    /// 但本工厂会被单测 / Launcher 之外的直调入口使用（如未来 CLI 工具）。
    /// 缺目录的失败延后到 Serilog.File 首次写盘 / DataProtection 加密时才抛，错位的根因极难排查；
    /// 这里前置一次「读 + 写 + 删」探针把根因暴露在 CreateApp 的第一帧。
    ///
    /// DbFile 故意不强制存在：SQLite 首启允许文件不存在（EnsureCreated 会建表），
    /// 此处仅保证 DbFile 的父目录可访问。
    /// </remarks>
    internal static void ValidatePaths(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        ValidateDirectoryWritable(paths.Root, nameof(paths.Root));
        ValidateDirectoryWritable(paths.LogDir, nameof(paths.LogDir));
        ValidateDirectoryWritable(paths.KeyRingDir, nameof(paths.KeyRingDir));

        // DbFile 仅校验父目录可写：SQLite 文件 EnsureCreated 时建
        string? dbParent = Path.GetDirectoryName(paths.DbFile);
        if (string.IsNullOrEmpty(dbParent))
        {
            throw new InvalidOperationException(
                $"AppPaths.DbFile 不含父目录段（不可用）：{paths.DbFile}");
        }
        ValidateDirectoryWritable(dbParent, $"{nameof(paths.DbFile)} 父目录");
    }

    private static void ValidateDirectoryWritable(string dir, string label)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new InvalidOperationException($"AppPaths.{label} 为空");
        }
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException(
                $"AppPaths.{label} 目录不存在：{dir}（Launcher 调用前应保证 AppPaths.Resolve() 已 CreateDirectory）");
        }

        // 写权限探针：建临时文件 → 删；失败时附详细 label 让 OPS 一眼看出哪个目录有问题
        string probe = Path.Combine(dir, $".pmm-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(probe, []);
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException(
                $"AppPaths.{label} 不可写：{dir}（{ex.GetType().Name}: {ex.Message}）", ex);
        }
        finally
        {
            try { File.Delete(probe); } catch { /* 探针清理失败不影响启动 */ }
        }
    }

    /// <summary>创建前端 wwwroot 的嵌入文件 provider；manifest 缺失（仅构建异常 / 前端从未构建）时返 null，调用方降级占位页</summary>
    /// <remarks>
    /// wwwroot 由 Host.csproj（GenerateEmbeddedFilesManifest + 动态 EmbeddedResource）编译进本程序集；
    /// root="wwwroot" 让 provider 把 /index.html、/assets/* 暴露在根。单文件发布后随 exe 内置、内存直读、不落临时盘。
    /// </remarks>
    private static IFileProvider? TryCreateFrontendFileProvider()
    {
        try
        {
            return new ManifestEmbeddedFileProvider(typeof(PmmHost).Assembly, "wwwroot");
        }
        catch
        {
            // 无嵌入清单（GenerateEmbeddedFilesManifest 失效 / 前端从未构建）→ 降级占位页
            return null;
        }
    }

    /// <summary>把嵌入的 appsettings.json 作为出厂默认配置插到 sources 最前（最低优先级，被 local.json / 环境变量 / 命令行覆盖）</summary>
    /// <remarks>
    /// appsettings.json 嵌入 Host 程序集（单文件 exe 无外置散文件）；用 ManifestEmbeddedFileProvider(root="/") 读 → JsonStream 源。
    /// Insert(0) 保证它优先级低于其余所有源；嵌入缺失（构建异常）时静默跳过，配置走代码默认（端口 DefaultPort 等）。
    /// </remarks>
    private static void InsertEmbeddedAppSettings(WebApplicationBuilder builder)
    {
        try
        {
            ManifestEmbeddedFileProvider provider = new(typeof(PmmHost).Assembly);
            IFileInfo fileInfo = provider.GetFileInfo("appsettings.json");
            if (!fileInfo.Exists)
            {
                return;
            }
            builder.Configuration.Sources.Insert(0, new JsonStreamConfigurationSource
            {
                Stream = fileInfo.CreateReadStream(),
            });
        }
        catch
        {
            // 嵌入资源缺失 / 读取失败：配置回落到代码默认值，不阻断启动
        }
    }
}
