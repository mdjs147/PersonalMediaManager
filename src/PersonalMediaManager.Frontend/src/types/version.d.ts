/**
 * 构建期版本号常量（由 vite.config.js define 注入）。
 * 数据源：
 *   __APP_PRODUCT_VERSION__   ← Directory.Build.props:PmmProductVersion（唯一完整主版本号）
 *   __APP_COMMIT__            ← git rev-parse --short=8 HEAD（无 git 时 'unknown'）
 *   __APP_BUILD_TIME__        ← vite 启动时刻 new Date().toISOString()
 *
 * npm 清单版本是主版本的生成副本；关于对话框优先展示 GET /api/system/version 的主版本。
 */
declare const __APP_PRODUCT_VERSION__: string;
declare const __APP_COMMIT__: string;
declare const __APP_BUILD_TIME__: string;
