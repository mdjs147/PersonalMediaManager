export const diagnosticLevels = ['Off', 'Standard', 'Detailed', 'Full'];
function validSettings(value) {
  return value && diagnosticLevels.includes(value.level) && Number.isFinite(value.maxArtifactUtf8Bytes)
    && Number.isFinite(value.maxArtifactTotalBytes) && Number.isFinite(value.maxArtifacts) && Number.isFinite(value.retentionDays);
}
export function createAiDiagnosticSettings(api, reactive = (value) => value) {
  const state = reactive({ settings: null, level: 'Standard', busy: false, error: '', notice: '', uncertain: false });
  async function load() {
    if (state.busy) return false;
    state.busy = true; state.error = ''; state.notice = '';
    try {
      const value = await api.get(); if (!validSettings(value)) throw new Error('invalid settings');
      state.settings = value; state.level = value.level; state.uncertain = false; return true;
    } catch { state.error = '无法读取诊断设置，请确认管理员权限后重试'; return false; }
    finally { state.busy = false; }
  }
  async function save() {
    if (state.busy || state.uncertain || !state.settings || !diagnosticLevels.includes(state.level) || state.settings.level === state.level) return false;
    state.busy = true; state.error = ''; state.notice = '';
    try {
      const requested = state.level;
      const value = await api.update(requested);
      if (!validSettings(value) || value.level !== requested) throw new Error('unverified save');
      state.settings = value; state.level = value.level; state.notice = '诊断级别已保存，后续采集生效'; return true;
    } catch { state.uncertain = true; state.error = '保存结果未确认，请先刷新核对后再操作'; return false; }
    finally { state.busy = false; }
  }
  return { state, load, save };
}
