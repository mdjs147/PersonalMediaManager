// @ts-check
import createClient from 'openapi-fetch';
import { ElMessage } from 'element-plus';

const TOKEN_KEY = 'pmm.token';

export const tokenStorage = {
  get: () => localStorage.getItem(TOKEN_KEY),
  set: (t) => localStorage.setItem(TOKEN_KEY, t),
  clear: () => localStorage.removeItem(TOKEN_KEY),
};

/**
 * 类型化 fetch 客户端（P3-13）
 * - 用 openapi-fetch 替代 axios：path / params / body / response 全部受 schema.d.ts 监督
 * - baseUrl 留空：index.js 中 path 字面量已含 /api 前缀，与 openapi schema key 完全一致，方便 IDE 跳转
 * - 中间件：注入 Authorization；抓 X-Token-Refresh 头自动续 token
 * - 401 / 信封拆解 / ElMessage 提示 由 unwrap 统一处理（避免中间件循环依赖 stores/auth）
 *
 * @type {import('openapi-fetch').Client<import('./schema').paths>}
 */
const client = createClient({ baseUrl: '' });

client.use({
  onRequest({ request }) {
    const token = tokenStorage.get();
    if (token) request.headers.set('Authorization', `Bearer ${token}`);
    return request;
  },
  onResponse({ response }) {
    const refreshed = response.headers.get('x-token-refresh');
    if (refreshed) tokenStorage.set(refreshed);
  },
});

/**
 * 401 兜底：清 token + 跳登录。动态 import 规避 http.js ↔ stores/auth.js 循环依赖。
 */
function handle401() {
  import('@/stores/auth').then(({ useAuthStore }) => {
    try { useAuthStore().logout(); } catch { /* 即使 store 失败也强制跳登录 */ }
  }).catch(() => { /* import 失败兜底：仍走清 token + 跳登录 */ })
    .finally(() => {
      if (location.pathname !== '/login' && location.pathname !== '/setup') {
        const redirect = encodeURIComponent(location.pathname + location.search);
        location.replace(`/login?redirect=${redirect}`);
      }
    });
}

/**
 * 解开 ApiResponse 信封（与 axios 旧拦截器完全等价）：
 * - 401 → 跳登录页 + reject
 * - HTTP 非 2xx → 把 error body.message 或状态码弹错并 reject
 * - body.code === 0 → 返 data.data（业务负载）
 * - body.code !== 0 → ElMessage 弹 message 并 reject
 * - 既无 envelope 也无 error（极少数情况）→ 原样返
 *
 * @template T
 * @param {Promise<{ data?: any, error?: any, response: Response }>} req
 * @returns {Promise<T>}
 */
export async function unwrap(req) {
  const { data, error, response } = await req;
  if (response.status === 401) {
    handle401();
    return Promise.reject(new Error('未授权'));
  }
  if (error !== undefined) {
    // openapi-fetch 把 HTTP 非 2xx 的 body 放到 error；若是信封也带 message 字段
    const msg = error?.message || `HTTP ${response.status}`;
    ElMessage.error(msg);
    return Promise.reject(new Error(msg));
  }
  if (data && typeof data.code === 'number') {
    if (data.code === 0) return data.data;
    ElMessage.error(data.message || `操作失败 (code ${data.code})`);
    return Promise.reject(new Error(data.message || `code ${data.code}`));
  }
  return data;
}

/**
 * 兜底原生 fetch（仅给极少数未被 OpenAPI schema 收录的端点用）：
 * - /api/history/{id}/nfo —— [Produces("application/xml")] 让 OpenAPI 生成器跳过
 * - /api/settings/parse-rules/import —— IFormFile multipart 形参未被 OpenAPI 元数据导出
 * 与 client 共用 token 注入 + X-Token-Refresh + 401 + 信封流程，避免行为分叉。
 *
 * @param {string} url 完整路径（如 '/api/history/123/nfo'）
 * @param {RequestInit & { parseAs?: 'json'|'blob', query?: Record<string, any> }} [init]
 * @returns {Promise<any>}
 */
export async function rawRequest(url, init = {}) {
  const { parseAs = 'json', query, headers: rawHeaders, ...rest } = init;
  const headers = new Headers(rawHeaders || {});
  const token = tokenStorage.get();
  if (token) headers.set('Authorization', `Bearer ${token}`);
  let finalUrl = url;
  if (query) {
    const usp = new URLSearchParams();
    for (const [k, v] of Object.entries(query)) {
      if (v !== undefined && v !== null) usp.append(k, String(v));
    }
    const qs = usp.toString();
    if (qs) finalUrl += (url.includes('?') ? '&' : '?') + qs;
  }
  const response = await fetch(finalUrl, { ...rest, headers });
  const refreshed = response.headers.get('x-token-refresh');
  if (refreshed) tokenStorage.set(refreshed);
  if (response.status === 401) {
    handle401();
    throw new Error('未授权');
  }
  if (parseAs === 'blob') {
    if (!response.ok) {
      const msg = `HTTP ${response.status}`;
      ElMessage.error(msg);
      throw new Error(msg);
    }
    return response.blob();
  }
  const body = await response.json();
  if (body && typeof body.code === 'number') {
    if (body.code === 0) return body.data;
    ElMessage.error(body.message || `操作失败 (code ${body.code})`);
    throw new Error(body.message || `code ${body.code}`);
  }
  return body;
}

export { client };
export default client;
