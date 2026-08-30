/**
 * 构建期版本号常量（由 vite.config.js define 注入）。
 * 数据源：
 *   __APP_PRODUCT_VERSION__   ← Directory.Build.props:PmmProductVersion（主版本号，对外展示）
 *   __APP_BACKEND_VERSION__   ← Directory.Build.props:VersionPrefix（仅供登录前 footer 参考）
 *   __APP_FRONTEND_VERSION__  ← package.json:version（前端自身版本号）
 *   __APP_DB_VERSION__        ← Directory.Build.props:PmmDbVersion（数据库目标版本号）
 *   __APP_COMMIT__            ← git rev-parse --short=8 HEAD（无 git 时 'unknown'）
 *   __APP_BUILD_TIME__        ← vite 启动时刻 new Date().toISOString()
 *
 * 运行时实际值仍以 GET /api/system/version 返回为准（登录后再校验前后端是否同步）。
 */
declare const __APP_PRODUCT_VERSION__: string;
declare const __APP_BACKEND_VERSION__: string;
declare const __APP_FRONTEND_VERSION__: string;
declare const __APP_DB_VERSION__: string;
declare const __APP_COMMIT__: string;
declare const __APP_BUILD_TIME__: string;
