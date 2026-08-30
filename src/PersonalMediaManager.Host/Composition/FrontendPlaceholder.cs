namespace PersonalMediaManager.Host.Composition;

/// <summary>前端未构建时的占位页 HTML</summary>
/// <remarks>
/// wwwroot 是 vite 构建产物、整目录 gitignore；前端没跑 npm run build:host 时
/// wwwroot/index.html 不存在，SPA fallback 改返回此占位页提示构建步骤，而非 404。
/// </remarks>
public static class FrontendPlaceholder
{
    public const string Html = """
        <!DOCTYPE html>
        <html lang="zh-CN">
          <head>
            <meta charset="UTF-8" />
            <title>PersonalMediaManager</title>
            <style>
              body {
                font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'PingFang SC', 'Microsoft YaHei', Roboto, sans-serif;
                background: #1f2937;
                color: #e5e7eb;
                display: flex;
                align-items: center;
                justify-content: center;
                height: 100vh;
                margin: 0;
                text-align: center;
              }
              .box { max-width: 520px; padding: 32px; background: rgba(0,0,0,0.2); border-radius: 8px; }
              code { background: rgba(255,255,255,0.1); padding: 2px 6px; border-radius: 3px; }
            </style>
          </head>
          <body>
            <div class="box">
              <h1>PersonalMediaManager</h1>
              <p>Backend 服务运行中，但前端尚未构建。</p>
              <p>请在 <code>src/PersonalMediaManager.Frontend</code> 下运行：</p>
              <pre><code>npm install
        npm run build:host</code></pre>
              <p>构建完成后重启应用即可看到完整 UI。</p>
              <hr style="border-color:#374151; margin: 16px 0;" />
              <p>API 文档：<a href="/scalar/v1" style="color:#60a5fa;">/scalar/v1</a></p>
            </div>
          </body>
        </html>
        """;
}
