import { client, unwrap } from './http';

// 本页用可重试的内联错误承载失败，避免后台轮询反复弹出消息。
async function request(send, { signal, timeoutMs = 15000 } = {}) {
  const controller = new AbortController();
  const abort = () => controller.abort();
  let timedOut = false;
  if (signal?.aborted) abort();
  signal?.addEventListener('abort', abort, { once: true });
  const timer = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, timeoutMs);
  try {
    const result = await send(controller.signal);
    if (result.response.status === 401) return await unwrap(Promise.resolve(result));
    const body = result.error ?? result.data;
    if (!result.response.ok || (typeof body?.code === 'number' && body.code !== 0)) {
      throw new Error(body?.message || `请求失败（HTTP ${result.response.status}），请刷新状态后重试`);
    }
    return typeof body?.code === 'number' ? body.data : body;
  } catch (error) {
    if (signal?.aborted) throw error;
    if (timedOut) throw new Error('请求超时，操作可能仍在后台进行；请刷新状态后再决定是否重试');
    if (error instanceof TypeError) throw new Error('无法连接 PMM，请检查服务是否运行，再刷新状态');
    throw error;
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener('abort', abort);
  }
}

export const localAiApi = {
  get: (options) => request((signal) => client.GET('/api/settings/local-ai', { signal }), options),
  models: (options) => request((signal) => client.GET('/api/settings/local-ai/models', { signal }), options),
  status: (options) => request((signal) => client.GET('/api/settings/local-ai/status', { signal }), options),
  update: (body, options) => request((signal) => client.POST('/api/settings/local-ai/update', { body, signal }), { timeoutMs: 30000, ...options }),
  download: (modelId, options) => request((signal) => client.POST('/api/settings/local-ai/download', { body: { modelId }, signal }), options),
  cancelDownload: (options) => request((signal) => client.POST('/api/settings/local-ai/download/cancel', { signal }), { timeoutMs: 30000, ...options }),
  start: (options) => request((signal) => client.POST('/api/settings/local-ai/start', { signal }), { timeoutMs: 240000, ...options }),
  stop: (options) => request((signal) => client.POST('/api/settings/local-ai/stop', { signal }), { timeoutMs: 30000, ...options }),
};
