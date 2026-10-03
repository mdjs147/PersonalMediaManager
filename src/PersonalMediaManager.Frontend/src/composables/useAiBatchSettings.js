import { computed, ref } from 'vue';

export const batchSizeFields = [
  { key: 'externalBatchSize', label: '外部 AI 每批文件上限', min: 1, max: 128 },
  { key: 'localBatchSize', label: '内置模型每批文件上限', min: 1, max: 2 },
];
export const batchBudgetFields = [
  { key: 'contextTokenBudget', label: '上下文预算（token）', min: 512, max: 1048576 },
  { key: 'maxOutputTokens', label: '输出上限（token）', min: 64, max: 262144 },
  { key: 'maxResponseBytes', label: '响应上限（字节）', min: 1024, max: 8388608 },
];
export const batchWaitField = { key: 'maxWaitMilliseconds', label: '最多等待合批（毫秒）', min: 0, max: 1000 };
export const providerBatchFields = [
  { key: 'batchSize', label: '此提供商每批文件上限', min: 1, max: 128 },
  ...batchBudgetFields,
];
const globalFields = [...batchSizeFields, batchWaitField, ...batchBudgetFields];
const providerKeys = ['configurationKey', 'disableThinking', ...providerBatchFields.map((field) => field.key)];
const validConfigurationKey = (value) => typeof value === 'string' && /^[a-f\d]{64}$/i.test(value);
export const maxProviderOverrides = 16;
const providerLimitMessage = `最多可保存 ${maxProviderOverrides} 条提供商覆盖，请移除不再使用的覆盖后再添加`;
export const defaultAiBatchSettings = () => ({ externalBatchSize: 1, localBatchSize: 1,
  maxWaitMilliseconds: 50, contextTokenBudget: 8192, maxOutputTokens: 2048, maxResponseBytes: 65536, providerSettings: [] });

function validateFields(value, fields) {
  for (const field of fields) {
    if (!Number.isInteger(value?.[field.key]) || value[field.key] < field.min || value[field.key] > field.max) return `${field.label}必须为 ${field.min}～${field.max} 的整数`;
  }
  return '';
}
export function validateBatchSizes(value) { return validateFields(value, batchSizeFields); }
export function validateAiBatchSettings(value) {
  const invalid = validateFields(value, globalFields);
  if (invalid) return invalid;
  if (!Array.isArray(value.providerSettings)) return '提供商覆盖设置格式无效';
  if (value.providerSettings.length > maxProviderOverrides) return providerLimitMessage;
  const ids = new Set();
  for (const row of value.providerSettings) {
    if (!Number.isSafeInteger(row?.providerId) || row.providerId < 1 || ids.has(row.providerId)) return '提供商编号必须为不重复的有效正整数';
    ids.add(row.providerId);
    if (!validConfigurationKey(row.configurationKey)) return '提供商配置标识无效，请刷新后核对';
    if (typeof row.disableThinking !== 'boolean') return '提供商非思考模式设置必须为布尔值';
    const rowInvalid = validateFields(row, providerBatchFields);
    if (rowInvalid) return `提供商 #${row.providerId}：${rowInvalid}`;
  }
  return '';
}

const clone = (value) => ({ ...value, providerSettings: value.providerSettings.map((row) => ({ disableThinking: false, ...row })) });
const normalize = (value) => {
  const settings = { ...defaultAiBatchSettings(), ...value };
  return Array.isArray(settings.providerSettings) ? clone(settings) : settings;
};
const findRow = (settings, id) => settings.providerSettings.find((row) => row.providerId === id);
const sameRow = (left, right) => left === right || Boolean(left && right && providerKeys.every((key) => left[key] === right[key]));
function changesBetween(base, draft) {
  const fields = globalFields.filter((field) => draft[field.key] !== base[field.key]).map((field) => field.key);
  const ids = new Set([...base.providerSettings, ...draft.providerSettings].map((row) => row.providerId));
  const providers = [];
  for (const providerId of ids) {
    const before = findRow(base, providerId); const after = findRow(draft, providerId);
    if (sameRow(before, after)) continue;
    providers.push({ providerId, before, after, keys: before && after ? providerKeys.filter((key) => before[key] !== after[key]) : providerKeys });
  }
  return { fields, providers };
}
function normalizeProviders(value) {
  if (!Array.isArray(value)) throw new Error('提供商目录格式无效');
  const ids = new Set();
  return value.map((provider) => {
    if (!Number.isSafeInteger(provider?.providerId) || provider.providerId < 1 || ids.has(provider.providerId) || !validConfigurationKey(provider.configurationKey)) throw new Error('提供商目录标识无效');
    ids.add(provider.providerId);
    const safePreset = (preset) => {
      const settings = { disableThinking: false, ...preset };
      return settings.providerId === provider.providerId && settings.configurationKey === provider.configurationKey && typeof settings.disableThinking === 'boolean' && !validateFields(settings, providerBatchFields)
        ? Object.fromEntries(['providerId', ...providerKeys].map((key) => [key, settings[key]])) : null;
    };
    return { providerId: provider.providerId, configurationKey: provider.configurationKey,
      name: typeof provider.name === 'string' ? provider.name : `提供商 #${provider.providerId}`,
      model: typeof provider.model === 'string' ? provider.model : '',
      recommendedSettings: safePreset(provider.recommendedSettings), advancedSettings: safePreset(provider.advancedSettings) };
  });
}

/** 两个入口共用草稿；保存前重读，仅合并真正编辑的字段和提供商覆盖。 */
export function createAiBatchSettingsController(api) {
  const form = ref(defaultAiBatchSettings());
  const saved = ref(null);
  const providers = ref([]);
  const providerError = ref('');
  const selectedProviderId = ref(null);
  const ready = ref(false);
  const busy = ref(false);
  const error = ref('');
  const notice = ref('');
  const uncertain = ref(false);
  const dirty = computed(() => {
    if (!saved.value) return false;
    const changes = changesBetween(saved.value, form.value);
    return changes.fields.length > 0 || changes.providers.length > 0;
  });
  const providerOptions = computed(() => {
    const options = [...providers.value];
    for (const row of [...form.value.providerSettings, ...(saved.value?.providerSettings || [])]) {
      if (!options.some((provider) => provider.providerId === row.providerId)) options.push({ providerId: row.providerId, name: `提供商 #${row.providerId}（目录不可用）`, model: '' });
    }
    return options;
  });
  const selectedProvider = computed(() => providers.value.find((provider) => provider.providerId === selectedProviderId.value));
  const selectedProviderSettings = computed(() => findRow(form.value, selectedProviderId.value));
  const providerLimitReached = computed(() => form.value.providerSettings.length >= maxProviderOverrides);
  const selectedSavedSettings = computed(() => saved.value && findRow(saved.value, selectedProviderId.value));
  const inheritedSettings = () => ({ batchSize: form.value.externalBatchSize, disableThinking: false,
    ...Object.fromEntries(batchBudgetFields.map((field) => [field.key, form.value[field.key]])) });
  const providerConfigurationChanged = computed(() => Boolean(selectedProvider.value && selectedProviderSettings.value && selectedProviderSettings.value.configurationKey !== selectedProvider.value.configurationKey));
  const providerNeedsRebind = computed(() => Boolean(selectedProvider.value && selectedSavedSettings.value && selectedSavedSettings.value.configurationKey !== selectedProvider.value.configurationKey));
  const effectiveProviderSettings = computed(() => selectedProviderSettings.value && !providerConfigurationChanged.value && selectedProvider.value ? selectedProviderSettings.value : inheritedSettings());
  const describe = (cause) => cause?.message || '请稍后重试';
  function alignSelectedProvider() {
    if (!providerOptions.value.some((provider) => provider.providerId === selectedProviderId.value)) selectedProviderId.value = providerOptions.value[0]?.providerId ?? null;
  }

  async function load(force = false) {
    if (busy.value) return false;
    if (ready.value && !force && !uncertain.value) return true;
    if (dirty.value && !uncertain.value) { error.value = '有未保存的批量设置修改，请先保存或撤销，再刷新'; return false; }
    busy.value = true;
    error.value = '';
    notice.value = '';
    try {
      const settings = normalize(await api.get());
      const validation = validateAiBatchSettings(settings);
      if (validation) throw new Error(validation);
      let directory = [];
      providerError.value = '';
      try { directory = normalizeProviders(await api.getProviders()); }
      catch (cause) { providerError.value = `提供商目录加载失败：${describe(cause)}。可继续编辑全局设置；请刷新后再编辑提供商覆盖。`; }
      providers.value = directory;
      form.value = clone(settings);
      saved.value = clone(settings);
      ready.value = true;
      uncertain.value = false;
      alignSelectedProvider();
      return true;
    } catch (cause) {
      error.value = `批量设置加载失败：${describe(cause)}`;
      return false;
    } finally { busy.value = false; }
  }
  function reset() {
    if (busy.value || uncertain.value || !saved.value) return;
    form.value = clone(saved.value);
    error.value = '';
    notice.value = '';
  }
  function canEditProvider() { return ready.value && !busy.value && !uncertain.value && Boolean(selectedProvider.value); }
  function setProviderOverride(enabled) {
    if (!ready.value || busy.value || uncertain.value) return;
    if (!enabled) {
      form.value.providerSettings = form.value.providerSettings.filter((row) => row.providerId !== selectedProviderId.value);
    } else if (canEditProvider() && !selectedProviderSettings.value) {
      if (providerLimitReached.value) { error.value = providerLimitMessage; return; }
      form.value.providerSettings.push({ providerId: selectedProviderId.value, configurationKey: selectedProvider.value.configurationKey, ...inheritedSettings() });
    }
  }
  function setProviderField(key, value) {
    if (!canEditProvider() || !selectedProviderSettings.value || !providerBatchFields.some((field) => field.key === key)) return;
    selectedProviderSettings.value[key] = value;
    bindProviderConfiguration();
  }
  function bindProviderConfiguration() {
    const supportsNonThinking = selectedProvider.value.recommendedSettings?.disableThinking === true || selectedProvider.value.advancedSettings?.disableThinking === true;
    if (selectedProviderSettings.value.configurationKey !== selectedProvider.value.configurationKey && !supportsNonThinking) selectedProviderSettings.value.disableThinking = false;
    selectedProviderSettings.value.configurationKey = selectedProvider.value.configurationKey;
  }
  function rebindProvider() {
    if (!canEditProvider() || !selectedProviderSettings.value) return;
    bindProviderConfiguration();
    notice.value = '已将此覆盖草稿绑定当前配置，请核对大小和预算后保存。';
  }
  function applyProviderPreset(kind) {
    if (!canEditProvider() || !['recommendedSettings', 'advancedSettings'].includes(kind)) return false;
    const preset = selectedProvider.value[kind];
    if (!preset) return false;
    const index = form.value.providerSettings.findIndex((row) => row.providerId === selectedProviderId.value);
    if (index < 0 && providerLimitReached.value) { error.value = providerLimitMessage; return false; }
    if (index < 0) form.value.providerSettings.push({ ...preset });
    else form.value.providerSettings[index] = { ...preset };
    notice.value = `已填入每批最多 ${preset.batchSize} 个文件的${preset.disableThinking ? '非思考模式' : ''}${kind === 'recommendedSettings' ? '推荐' : '高级'}配置草稿，点击“保存批量设置”后生效。`;
    return true;
  }
  async function save() {
    if (busy.value || !ready.value || !dirty.value || uncertain.value) return false;
    const validation = validateAiBatchSettings(form.value);
    if (validation) { error.value = validation; return false; }
    const desired = clone(form.value);
    const baseline = clone(saved.value);
    const changes = changesBetween(baseline, desired);
    busy.value = true;
    error.value = '';
    notice.value = '';
    let submitted = false;
    try {
      const latest = normalize(await api.get());
      const latestInvalid = validateAiBatchSettings(latest);
      if (latestInvalid) throw new Error(latestInvalid);
      const conflict = () => { throw new Error('相同批量设置已被其他页面修改，请撤销本页修改并刷新核对'); };
      if (changes.fields.some((key) => latest[key] !== baseline[key])) conflict();
      const payload = clone(latest);
      for (const key of changes.fields) payload[key] = desired[key];
      for (const change of changes.providers) {
        const current = findRow(latest, change.providerId);
        if (!change.before || !change.after) {
          if (!sameRow(current, change.before)) conflict();
        } else if (!current || current.configurationKey !== change.before.configurationKey || change.keys.some((key) => current[key] !== change.before[key])) conflict();
        const index = payload.providerSettings.findIndex((row) => row.providerId === change.providerId);
        if (!change.after) payload.providerSettings.splice(index, 1);
        else if (!change.before) payload.providerSettings.push({ ...change.after });
        else payload.providerSettings[index] = { ...current, ...Object.fromEntries(change.keys.map((key) => [key, change.after[key]])) };
      }
      const payloadInvalid = validateAiBatchSettings(payload);
      if (payloadInvalid) throw new Error(payloadInvalid);
      submitted = true;
      await api.update(payload);
      const verified = normalize(await api.get());
      const verifiedInvalid = validateAiBatchSettings(verified);
      if (verifiedInvalid) throw new Error(verifiedInvalid);
      const mismatch = changes.fields.some((key) => verified[key] !== desired[key]) || changes.providers.some((change) => {
        const actual = findRow(verified, change.providerId);
        if (!change.before || !change.after) return !sameRow(actual, change.after);
        return !actual || actual.configurationKey !== change.after.configurationKey || change.keys.some((key) => actual[key] !== change.after[key]);
      });
      if (mismatch) throw new Error('回读值与提交值不同，请刷新核对');
      form.value = clone(verified);
      saved.value = clone(verified);
      alignSelectedProvider();
      notice.value = '批量设置已保存。每批 1 个保持逐条；文件数是上限，实际会按预算拆批。';
      return true;
    } catch (cause) {
      uncertain.value = submitted;
      error.value = `${submitted ? '设置提交结果尚未核实，请刷新后再操作' : '批量设置未保存'}：${describe(cause)}`;
      return false;
    } finally { busy.value = false; }
  }
  return { form, saved, ready, busy, error, notice, uncertain, dirty, providers, providerOptions, providerError, providerLimitReached,
    selectedProviderId, selectedProvider, selectedProviderSettings, selectedSavedSettings, effectiveProviderSettings,
    providerConfigurationChanged, providerNeedsRebind, load, reset, save, setProviderOverride, setProviderField, rebindProvider, applyProviderPreset };
}

let sharedController;
export function useAiBatchSettings(api) {
  sharedController ??= createAiBatchSettingsController(api);
  return sharedController;
}
