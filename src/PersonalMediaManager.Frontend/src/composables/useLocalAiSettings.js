import { computed, ref } from 'vue';

export const localAiModes = [
  { value: 'Disabled', label: '关闭', description: '默认关闭，本地模型不参与解析。' },
  { value: 'BeforeRules', label: '规则前建议', description: '先提取有来源的检索建议，再完整运行规则；规则证据始终优先。' },
  { value: 'AfterRules', label: '规则后补充', description: '仅在规则与 TMDB 仍未解决识别时运行，补充有来源的检索建议。' },
];

export const localAiLimits = [
  { key: 'port', label: '本机端口', min: 1024, max: 65535, step: 1, hint: '只监听 127.0.0.1，请选择未占用端口。' },
  { key: 'threads', label: 'CPU 线程', min: 1, max: 16, step: 1, hint: '较少线程可给媒体处理保留资源。' },
  { key: 'contextTokens', label: '上下文长度', min: 512, max: 4096, step: 256, hint: '包含输入与输出，单位为 token。' },
  { key: 'maxOutputTokens', label: '最大输出', min: 64, max: 1024, step: 64, hint: '单位为 token，必须小于上下文长度。' },
  { key: 'timeoutSeconds', label: '推理超时', min: 5, max: 120, step: 5, hint: '单位为秒，超时后回退原解析流程。' },
  { key: 'startupTimeoutSeconds', label: '启动超时', min: 5, max: 180, step: 5, hint: '单位为秒，为模型加载与健康检查留出时间。' },
  { key: 'memoryLimitMb', label: '内存保护阈值', min: 768, max: 4096, step: 256, hint: '单位为 MB，每秒检查进程工作集，超限即停止；不是系统硬上限。' },
];

export function defaultLocalAiSettings() {
  return {
    mode: 'Disabled', modelId: 'qwen2.5-0.5b-instruct-q8_0', runtimeExecutablePath: '',
    port: 18081, threads: 2, contextTokens: 2048, maxOutputTokens: 512,
    timeoutSeconds: 30, startupTimeoutSeconds: 90, memoryLimitMb: 2048,
  };
}

function settingsPayload(settings) {
  return Object.fromEntries(Object.keys(defaultLocalAiSettings()).map((key) => [key,
    key === 'runtimeExecutablePath' ? (settings[key] || '').trim() : settings[key],
  ]));
}

export function validateLocalAiSettings(settings) {
  if (!localAiModes.some((mode) => mode.value === settings.mode)) return '请选择有效的介入模式';
  if (!['qwen2.5-0.5b-instruct-q8_0', 'huihui-qwen2.5-0.5b-v3-q8_0'].includes(settings.modelId)) return '请选择允许列表中的模型';
  const path = settings.runtimeExecutablePath;
  if (typeof path !== 'string' || path.length > 1024 || /[\u0000-\u001f\u007f]/.test(path)) return '运行时路径无效，请填写可执行文件的绝对路径';
  if (path && (!/^(?:[a-z]:[\\/]|\/(?!\/))/.test(path.toLowerCase())
      || !/(?:^|[\\/])llama-server(?:\.exe)?$/i.test(path))) {
    return '请填写 PMM 所在电脑上 llama-server 或 llama-server.exe 的绝对路径，不要附加命令参数';
  }
  for (const field of localAiLimits) {
    const value = settings[field.key];
    if (!Number.isInteger(value) || value < field.min || value > field.max) return `${field.label}必须是 ${field.min}～${field.max} 之间的整数`;
  }
  if (settings.maxOutputTokens >= settings.contextTokens) return '最大输出必须小于上下文长度';
  return '';
}

export function downloadPercent(status) {
  if (!status?.downloadTotalBytes || status.downloadTotalBytes <= 0) return 0;
  return Math.max(0, Math.min(100, Math.floor(status.downloadedBytes / status.downloadTotalBytes * 100)));
}

export function modelVerificationLabel(model, status, stale = false) {
  if (status?.modelId === model.id && status.state === 'Running' && !stale) return '本次启动已通过大小与 SHA256 校验。';
  if (status?.downloadModelId === model.id && status.downloadState === 'Completed') return '本次下载已通过大小与 SHA256 校验；下次启动会重新校验。';
  if (model.installed) return '本地文件大小匹配；启动前会完整复验 SHA256。';
  if (model.canDownload) return '下载后校验大小与 SHA256，通过后才安装。';
  return model.canVerify ? '请手动放入下方固定路径后刷新状态；启动前校验大小与 SHA256，不匹配时保留原文件并回退。' : '缺少可验证的固定制品，暂不可用。';
}

/** 将设置编辑、单次操作与轮询分开，防止状态刷新覆盖未保存的表单。 */
export function createLocalAiSettingsController(api, { schedule = setTimeout, unschedule = clearTimeout } = {}) {
  const form = ref(defaultLocalAiSettings());
  const saved = ref(null);
  const models = ref([]);
  const status = ref(null);
  const ready = ref(false);
  const loading = ref(false);
  const refreshing = ref(false);
  const action = ref('');
  const error = ref('');
  const notice = ref('');
  const stale = ref(true);
  const busy = computed(() => loading.value || refreshing.value || !!action.value);
  const dirty = computed(() => saved.value !== null && JSON.stringify(settingsPayload(form.value)) !== JSON.stringify(saved.value));
  const downloading = computed(() => status.value?.downloadState === 'Downloading');
  let disposed = false;
  let timer;
  let currentRequest;

  function clearPoll() {
    if (timer !== undefined) unschedule(timer);
    timer = undefined;
  }

  function planPoll() {
    clearPoll();
    if (!disposed && ready.value && !stale.value) timer = schedule(() => refresh(false), downloading.value ? 1500 : 5000);
  }

  function beginRequest() {
    clearPoll();
    currentRequest = new AbortController();
    return { signal: currentRequest.signal };
  }

  async function readStatus(options, includeModels = false) {
    const next = await api.status(options);
    if (disposed) return;
    // 手动刷新及下载状态变化时重读文件状态；启动前仍须完整校验哈希。
    const changed = status.value?.downloadState !== next.downloadState;
    if (includeModels || changed) {
      const list = await api.models(options);
      if (disposed) return;
      models.value = list;
    }
    status.value = next;
    stale.value = false;
  }

  function fail(prefix, cause) {
    if (disposed) return;
    stale.value = true;
    error.value = `${prefix}：${cause?.message || '请求失败，请刷新状态后重试'}`;
  }

  async function load() {
    if (disposed || busy.value) return false;
    loading.value = true;
    error.value = '';
    const options = beginRequest();
    try {
      const settings = await api.get(options);
      if (disposed) return false;
      await readStatus(options, true);
      if (disposed) return false;
      form.value = settingsPayload({ ...defaultLocalAiSettings(), ...settings });
      saved.value = { ...form.value };
      ready.value = true;
      return true;
    } catch (cause) {
      fail('加载设置失败', cause);
      return false;
    } finally {
      loading.value = false;
      currentRequest = undefined;
      planPoll();
    }
  }

  async function refresh(manual = true) {
    if (!ready.value) return load();
    if (disposed || busy.value) return false;
    refreshing.value = true;
    if (manual) error.value = '';
    const options = beginRequest();
    try {
      await readStatus(options, manual);
      return !disposed;
    } catch (cause) {
      fail('状态刷新失败，自动刷新已暂停', cause);
      return false;
    } finally {
      refreshing.value = false;
      currentRequest = undefined;
      planPoll();
    }
  }

  async function perform(name, work, success) {
    if (disposed || !ready.value || busy.value) return false;
    action.value = name;
    error.value = '';
    notice.value = '';
    const options = beginRequest();
    let completed = false;
    try {
      await work(options);
      if (disposed) return false;
      completed = true;
      notice.value = success;
      await readStatus(options, true);
      return !disposed;
    } catch (cause) {
      fail(completed ? '操作已提交，但状态刷新失败' : '操作未确认完成，请先刷新状态', cause);
      return false;
    } finally {
      action.value = '';
      currentRequest = undefined;
      planPoll();
    }
  }

  function save() {
    const payload = settingsPayload(form.value);
    const validation = validateLocalAiSettings(payload);
    if (validation) {
      error.value = validation;
      return Promise.resolve(false);
    }
    return perform('save', async (options) => {
      await api.update(payload, options);
      if (disposed) return;
      saved.value = { ...payload };
      form.value = { ...payload };
    }, '设置已保存，旧进程已停止。需要运行时请单独点击“启动本地服务”。');
  }

  function dispose() {
    disposed = true;
    clearPoll();
    currentRequest?.abort();
  }

  return {
    form, saved, models, status, ready, loading, refreshing, action, error, notice, stale,
    busy, dirty, downloading, load, refresh, save, dispose,
    download: (modelId) => perform('download', (options) => api.download(modelId, options), '下载请求已提交，下载后仍需保存选择并单独启动。'),
    cancelDownload: () => perform('cancelDownload', (options) => api.cancelDownload(options), '下载取消请求已提交。'),
    start: () => perform('start', (options) => api.start(options), '启动请求已完成，请以当前健康状态为准。介入模式保持不变。'),
    stop: () => perform('stop', (options) => api.stop(options), '本地服务已停止，介入模式保持不变。'),
  };
}
