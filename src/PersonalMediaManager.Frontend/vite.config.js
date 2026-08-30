import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import AutoImport from 'unplugin-auto-import/vite';
import Components from 'unplugin-vue-components/vite';
import { ElementPlusResolver } from 'unplugin-vue-components/resolvers';
import path from 'path';
import { readFileSync, existsSync } from 'fs';
import { execSync } from 'child_process';

/**
 * Vite 配置（E3.1 脚手架）
 * - 别名 @ -> src/
 * - ElementPlus 按需自动导入（unplugin-auto-import + unplugin-vue-components）减小打包体积
 * - dev server 5173 端口，代理 /api /hubs /openapi /scalar 到 Host 调试端口 47333（Debug 默认端口；正式环境为 7288）
 * - build 产物输出到 dist/，由 scripts/copy-to-host-wwwroot.js 拷到 Host wwwroot/
 * - 构建期版本号注入：定义 __APP_*__ 全局常量供 src/ 直接引用（详见 src/types/version.d.ts）
 */

/**
 * 解析构建期版本号：
 *   __APP_FRONTEND_VERSION__   = package.json:version（前端自身版本号）
 *   __APP_PRODUCT_VERSION__    = Directory.Build.props:PmmProductVersion（主版本号，对外展示）
 *   __APP_BACKEND_VERSION__    = Directory.Build.props:VersionPrefix（后端版本号，仅供登录前 footer 参考）
 *   __APP_DB_VERSION__         = Directory.Build.props:PmmDbVersion（数据库目标版本号）
 *   __APP_COMMIT__             = git rev-parse --short=8 HEAD（无 git 时 'unknown'）
 *   __APP_BUILD_TIME__         = ISO 8601 UTC
 *
 * 运行时实际值仍以 /system/version 返回为准（登录后调一次校验前后端是否同步）。
 */
function resolveVersionInfo() {
  const pkgPath = path.resolve(import.meta.dirname, 'package.json');
  const propsPath = path.resolve(import.meta.dirname, '..', '..', 'Directory.Build.props');

  // 前端版本号
  let frontendVersion = '0.0.0';
  try {
    const pkg = JSON.parse(readFileSync(pkgPath, 'utf8'));
    frontendVersion = pkg.version || frontendVersion;
  } catch (e) {
    console.warn('[vite] 读取 package.json:version 失败，前端版本号置为 0.0.0', e);
  }

  // 后端 / 数据库 / 主版本号 — 从 Directory.Build.props 解析（正则提取避免引入 XML 解析依赖）
  let productVersion = '0.0.0';
  let backendVersion = '0.0.0';
  let dbVersion = '0.0.0';
  if (existsSync(propsPath)) {
    try {
      const propsText = readFileSync(propsPath, 'utf8');
      const m1 = propsText.match(/<PmmProductVersion>([^<]+)<\/PmmProductVersion>/);
      const m2 = propsText.match(/<VersionPrefix>([^<]+)<\/VersionPrefix>/);
      const m3 = propsText.match(/<PmmDbVersion>([^<]+)<\/PmmDbVersion>/);
      if (m1) productVersion = m1[1];
      if (m2) backendVersion = m2[1];
      if (m3) dbVersion = m3[1];
    } catch (e) {
      console.warn('[vite] 解析 Directory.Build.props 失败，相关版本号置为 0.0.0', e);
    }
  }

  // commit
  let commit = 'unknown';
  try {
    commit = execSync('git rev-parse --short=8 HEAD', { stdio: ['ignore', 'pipe', 'ignore'] })
      .toString().trim() || 'unknown';
  } catch {
    // 无 git 环境（如 docker build）忽略
  }

  return {
    frontendVersion,
    productVersion,
    backendVersion,
    dbVersion,
    commit,
    buildTime: new Date().toISOString(),
  };
}

const versionInfo = resolveVersionInfo();

export default defineConfig({
  define: {
    __APP_PRODUCT_VERSION__: JSON.stringify(versionInfo.productVersion),
    __APP_BACKEND_VERSION__: JSON.stringify(versionInfo.backendVersion),
    __APP_FRONTEND_VERSION__: JSON.stringify(versionInfo.frontendVersion),
    __APP_DB_VERSION__: JSON.stringify(versionInfo.dbVersion),
    __APP_COMMIT__: JSON.stringify(versionInfo.commit),
    __APP_BUILD_TIME__: JSON.stringify(versionInfo.buildTime),
  },
  plugins: [
    vue(),
    AutoImport({
      resolvers: [ElementPlusResolver()],
      imports: ['vue', 'vue-router', 'pinia'],
      dts: 'src/auto-imports.d.ts',
    }),
    Components({
      resolvers: [ElementPlusResolver()],
      dts: 'src/components.d.ts',
    }),
  ],
  resolve: {
    alias: {
      '@': path.resolve(import.meta.dirname, 'src'),
      // 启用 Vue 的运行时 + 编译器版本，使 defineComponent({ template: '...' }) 在客户端可编译
      // 用于 CategoryRuleBuilder.vue 内联的递归子组件 RuleGroup。增量约 14KB。
      vue: 'vue/dist/vue.esm-bundler.js',
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:47333', changeOrigin: true },
      '/hubs': { target: 'http://localhost:47333', changeOrigin: true, ws: true },
      '/openapi': { target: 'http://localhost:47333', changeOrigin: true },
      '/scalar': { target: 'http://localhost:47333', changeOrigin: true },
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 1500,
    rolldownOptions: {
      output: {
        // 把第三方大库拆成独立 chunk，避免一个 1.3MB 主 bundle；
        // 用户切页时主 bundle 走浏览器缓存，仅按需加载未访问过的 vendor chunk
        codeSplitting: {
          groups: [
            {
              name: 'vendor-vue',
              test: /[\\/]node_modules[\\/](?:vue|vue-router|pinia)[\\/]/,
            },
            {
              name: 'vendor-element-plus',
              test: /[\\/]node_modules[\\/](?:element-plus|@element-plus[\\/]icons-vue)[\\/]/,
            },
            {
              name: 'vendor-signalr',
              test: /[\\/]node_modules[\\/]@microsoft[\\/]signalr[\\/]/,
            },
          ],
        },
      },
    },
  },
});
