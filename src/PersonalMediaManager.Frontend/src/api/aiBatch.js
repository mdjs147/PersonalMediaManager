import { client, unwrap } from './http';

/** 批量设置与模型启用状态独立；提供商目录不包含密钥或端点。 */
export const aiBatchApi = {
  get: () => unwrap(client.GET('/api/settings/ai-batch')),
  getProviders: () => unwrap(client.GET('/api/settings/ai-batch/providers')),
  update: (body) => unwrap(client.PUT('/api/settings/ai-batch', { body })),
};
