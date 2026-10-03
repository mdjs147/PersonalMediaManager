import { rawRequest } from './http';

export const aiDiagnosticsApi = {
  get: () => rawRequest('/api/diagnostics/parse/settings'),
  update: (level) => rawRequest('/api/diagnostics/parse/settings', {
    method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ level }),
  }),
  export: (mediaItemId) => rawRequest('/api/diagnostics/parse/export', { query: { mediaItemId } }),
};
