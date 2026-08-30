<script setup>
import { ref, computed, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { ElMessage } from 'element-plus';
import { api } from '@/api';
import { useAuthStore } from '@/stores/auth';
import PmmBrandMark from '@/components/PmmBrandMark.vue';

// 主版本号来自 vite.config define（构建期烤进 bundle），与 Login / 关于对话框口径一致
const productVersion = __APP_PRODUCT_VERSION__;
const router = useRouter();
const auth = useAuthStore();

// ---- 向导整体状态 ----
const started = ref(false);          // 欢迎页 → 向导主体
const active = ref(0);               // 当前步：0 账号 / 1 TMDB / 2 监控 / 3 归档 / 4 AI / 5 完成
const accountMode = ref('create');   // create=首次建管理员；login=账号已建未登录（断点续配）
const busy = ref(false);

// ---- 常量 ----
const TMDB_LANGS = [
  { value: 'zh-CN', label: '简体中文 zh-CN' },
  { value: 'zh-TW', label: '繁体中文 zh-TW' },
  { value: 'en-US', label: 'English en-US' },
  { value: 'ja-JP', label: '日本語 ja-JP' },
];
// AI 厂商快速预设（与 AiProviders.vue 一致；cost 应用时映射到 form.costTier）
const AI_PRESETS = [
  { name: 'Ollama 本地', type: 'Ollama', cost: 'Free', baseUrl: 'http://localhost:11434', model: 'qwen2.5:7b' },
  { name: 'OpenRouter', type: 'OpenAiCompatible', cost: 'Paid', baseUrl: 'https://openrouter.ai/api/v1', model: 'deepseek/deepseek-chat' },
  { name: 'DeepSeek', type: 'OpenAiCompatible', cost: 'Paid', baseUrl: 'https://api.deepseek.com', model: 'deepseek-chat' },
  { name: '硅基流动', type: 'OpenAiCompatible', cost: 'Paid', baseUrl: 'https://api.siliconflow.cn/v1', model: 'deepseek-ai/DeepSeek-V3' },
  { name: '阿里云百炼', type: 'OpenAiCompatible', cost: 'Paid', baseUrl: 'https://dashscope.aliyuncs.com/compatible-mode/v1', model: 'qwen-plus' },
  { name: '智谱 GLM', type: 'OpenAiCompatible', cost: 'Paid', baseUrl: 'https://open.bigmodel.cn/api/paas/v4', model: 'glm-4-flash' },
  { name: 'Kimi', type: 'OpenAiCompatible', cost: 'Paid', baseUrl: 'https://api.moonshot.cn/v1', model: 'moonshot-v1-8k' },
  { name: 'OpenAI', type: 'OpenAiCompatible', cost: 'Paid', baseUrl: 'https://api.openai.com', model: 'gpt-4o-mini' },
  { name: 'Claude', type: 'Anthropic', cost: 'Paid', baseUrl: 'https://api.anthropic.com', model: 'claude-haiku-4-5-20251001' },
  { name: 'Gemini', type: 'Gemini', cost: 'Paid', baseUrl: 'https://generativelanguage.googleapis.com', model: 'gemini-1.5-flash' },
];
const AI_PROTOCOLS = ['Ollama', 'OpenAiCompatible', 'Anthropic', 'Gemini', 'AzureOpenAi'];
const FILE_OPS = [
  { value: 'Move', label: '移动', desc: '移走源文件，原位置消失。适合普通下载、不做种。' },
  { value: 'Copy', label: '复制', desc: '保留源文件，占双倍空间。适合想留底。' },
  { value: 'Link', label: '硬链接', desc: '不占双倍空间，源文件留存可继续做种。PT/BT 用户首选。' },
];
const CONFLICT_POLICIES = [
  { value: 'Skip', label: '跳过（不覆盖已有文件）' },
  { value: 'Overwrite', label: '升级替换（新文件更大才替换）' },
  { value: 'KeepBoth', label: '保留多版本（加序号共存）' },
  { value: 'Ask', label: '询问（放入待确认，人工决定）' },
];
const MEDIA_TYPE_LABEL = { Movie: '电影', Tv: '剧集', Both: '电影+剧集' };

// ---- 各步表单状态 ----
const account = ref({ username: '', password: '', confirm: '' });

// TMDB update 是整体替换语义、数值字段非空 [Range] 必填且四权重和须≈1.0，
// 故须携带从 get 取回的全部字段（向导 UI 只露 key+语言，其余原样回传，避免被默认值覆盖/校验失败）
const tmdb = ref({
  apiKey: '', hasApiKey: false,
  language: 'zh-CN', fallbackLanguage: 'en-US',
  candidateThreshold: 3, rateLimitPerSecond: 40,
  metadataCacheHours: 24, searchCacheMinutes: 60,
  scoreWeightTitle: 0.5, scoreWeightYear: 0.3, scoreWeightPopularity: 0.1, scoreWeightLanguage: 0.1,
});
const tmdbEditKey = ref(true);
const tmdbTesting = ref(false);
const showProxy = ref(false);
const proxy = ref({ enabled: false, httpUrl: '', useForTmdb: true });

const watchForm = ref({ path: '', alias: '', isNetworkShare: false });
const watchList = ref([]);
const watchAdding = ref(false);

const libraryRoot = ref('');
const categories = ref([]);
const fileOperation = ref('Move');
const conflictPolicy = ref('Skip');
const archiveSaving = ref(false);

const aiForm = ref({ name: '', type: 'Ollama', costTier: 'Free', baseUrl: '', model: '', apiKey: '' });
const aiList = ref([]);

const checklist = ref(null);

// ---- computed ----
// v4 token 格式提示（与 Tmdb.vue 同口径）
const tmdbTokenWarn = computed(() => {
  const raw = (tmdb.value.apiKey || '').trim();
  if (!raw) return '';
  if (/^[a-f0-9]{32}$/i.test(raw)) return '检测到 v3 API Key（32 位 hex），TMDB 搜索将返回 401。请改用 v4 Read Access Token（eyJ 开头）。';
  if (!raw.startsWith('eyJ')) return '该字符串不像 v4 Token（应以 eyJ 开头）。请确认复制的是「API 读访问令牌」。';
  return '';
});
const unsetCount = computed(() =>
  categories.value.filter((c) => !c.targetRoot || c.targetRoot === '<UNSET>').length);
const fileOpDesc = computed(() => FILE_OPS.find((o) => o.value === fileOperation.value)?.desc || '');

// ---- 工具 ----
function flattenGroups(g) {
  const kv = {};
  Object.values(g?.groups || {}).forEach((arr) => arr.forEach((it) => { kv[it.key] = it.value; }));
  return kv;
}
function validProxyUrl(raw) {
  const v = (raw || '').trim();
  if (!v) return false;
  try {
    const u = new URL(v);
    return (u.protocol === 'http:' || u.protocol === 'https:') && !!u.hostname;
  } catch { return false; }
}

// 进入设置步前拉取已有配置（断点续配 / 回看前一步已存值）
async function loadExistingConfig() {
  const t = await api.tmdbSetting.get().catch(() => null);
  if (t) {
    tmdb.value = {
      apiKey: '',
      hasApiKey: !!t.hasApiKey,
      language: t.language || 'zh-CN',
      fallbackLanguage: t.fallbackLanguage || 'en-US',
      candidateThreshold: t.candidateThreshold ?? 3,
      rateLimitPerSecond: t.rateLimitPerSecond ?? 40,
      metadataCacheHours: t.metadataCacheHours ?? 24,
      searchCacheMinutes: t.searchCacheMinutes ?? 60,
      scoreWeightTitle: t.scoreWeightTitle ?? 0.5,
      scoreWeightYear: t.scoreWeightYear ?? 0.3,
      scoreWeightPopularity: t.scoreWeightPopularity ?? 0.1,
      scoreWeightLanguage: t.scoreWeightLanguage ?? 0.1,
    };
    tmdbEditKey.value = !t.hasApiKey;
  }
  watchList.value = (await api.watchFolders.list().catch(() => [])) || [];
  const c = await api.categories.list().catch(() => null);
  categories.value = c?.items || c || [];
  const g = await api.generalSettings.get().catch(() => null);
  const kv = flattenGroups(g);
  fileOperation.value = kv['File.Operation'] || 'Move';
  conflictPolicy.value = kv['Archive_ConflictPolicy'] || 'Skip';
  proxy.value.enabled = kv.Proxy_Enabled === 'true';
  proxy.value.httpUrl = kv.Proxy_HttpUrl || '';
  proxy.value.useForTmdb = kv.Proxy_UseForTmdb !== 'false';
  aiList.value = (await api.parseAiProviders.list().catch(() => [])) || [];
}

// ---- 步骤 0：账号 ----
async function submitAccount() {
  if (!account.value.username) return ElMessage.warning('请填写管理员账号');
  if (!account.value.password || account.value.password.length < 6) return ElMessage.warning('密码至少 6 位');
  if (accountMode.value !== 'login' && account.value.password !== account.value.confirm) {
    return ElMessage.warning('两次密码不一致');
  }
  busy.value = true;
  try {
    if (accountMode.value !== 'login') {
      await api.setup.createAdmin({ username: account.value.username, password: account.value.password });
    }
    await auth.login(account.value.username, account.value.password);
    auth.setupCompleted = false; // 账号已建并登录，但 complete 仍留到最后一步
    active.value = 1;
    await loadExistingConfig();
  } finally {
    busy.value = false;
  }
}

// ---- 步骤 1：TMDB ----
async function saveTmdb() {
  // 整体替换语义：回传全部字段（数值字段缺省会触发 [Range]/权重和校验失败）
  const payload = {
    language: tmdb.value.language,
    fallbackLanguage: tmdb.value.fallbackLanguage,
    candidateThreshold: tmdb.value.candidateThreshold,
    rateLimitPerSecond: tmdb.value.rateLimitPerSecond,
    metadataCacheHours: tmdb.value.metadataCacheHours,
    searchCacheMinutes: tmdb.value.searchCacheMinutes,
    scoreWeightTitle: tmdb.value.scoreWeightTitle,
    scoreWeightYear: tmdb.value.scoreWeightYear,
    scoreWeightPopularity: tmdb.value.scoreWeightPopularity,
    scoreWeightLanguage: tmdb.value.scoreWeightLanguage,
  };
  if (tmdbEditKey.value && tmdb.value.apiKey) payload.apiKey = tmdb.value.apiKey.trim();
  await api.tmdbSetting.update(payload);
  if (payload.apiKey) { tmdb.value.hasApiKey = true; tmdbEditKey.value = false; tmdb.value.apiKey = ''; }
}
async function testTmdb() {
  if (tmdbEditKey.value && tmdb.value.apiKey) await saveTmdb(); // test 只测已落库的 key，先保存
  if (!tmdb.value.hasApiKey) return ElMessage.warning('请先填写并保存 API Token');
  tmdbTesting.value = true;
  try {
    const r = (await api.tmdbSetting.test()) || {};
    if (r.success) {
      ElMessage.success(`TMDB 连接正常 · ${(Number(r.elapsedMilliseconds) || 0).toFixed(0)}ms`);
      showProxy.value = false;
    } else {
      const status = r.httpStatus != null ? `HTTP ${r.httpStatus} · ` : '';
      ElMessage.error(`TMDB 连接失败：${status}${r.errorMessage || '未知错误'}`);
      showProxy.value = true; // 失败 → 展开代理补救
    }
  } finally {
    tmdbTesting.value = false;
  }
}
async function saveProxyAndRetest() {
  if (proxy.value.enabled && !validProxyUrl(proxy.value.httpUrl)) {
    return ElMessage.error('代理地址不合法（示例 http://127.0.0.1:7890）');
  }
  await api.generalSettings.update({
    items: [
      { key: 'Proxy_Enabled', value: proxy.value.enabled ? 'true' : 'false' },
      { key: 'Proxy_HttpUrl', value: (proxy.value.httpUrl || '').trim() || null },
      { key: 'Proxy_UseForTmdb', value: proxy.value.useForTmdb ? 'true' : 'false' },
    ],
  });
  ElMessage.success('代理已保存，正在重新测试…');
  await testTmdb();
}
async function nextFromTmdb() {
  busy.value = true;
  try { await saveTmdb(); active.value = 2; } finally { busy.value = false; }
}

// ---- 步骤 2：监控目录 ----
async function addWatch() {
  if (!watchForm.value.path) return ElMessage.warning('请填写目录路径');
  watchAdding.value = true;
  try {
    await api.watchFolders.create({
      path: watchForm.value.path.trim(),
      alias: watchForm.value.alias?.trim() || null,
      isTransit: false,
      isNetworkShare: watchForm.value.isNetworkShare,
      enabled: true,
      autoScan: true,
      priority: 100,
    });
    watchList.value = (await api.watchFolders.list()) || [];
    watchForm.value = { path: '', alias: '', isNetworkShare: false };
    ElMessage.success('已添加监控目录');
  } finally {
    watchAdding.value = false;
  }
}
async function testWatch(row) {
  const d = (await api.watchFolders.test(row.id)) || {};
  if (d.reachable) ElMessage.success(`可达：${d.sampleEntry ?? '(目录为空)'}`);
  else ElMessage.warning(d.errorMessage || '目录不可达');
}
async function removeWatch(row) {
  await api.watchFolders.delete(row.id);
  watchList.value = (await api.watchFolders.list()) || [];
}

// ---- 步骤 3：归档设置 ----
function applyLibraryRoot() {
  const root = (libraryRoot.value || '').trim();
  if (!root) return ElMessage.warning('请先填写媒体库根目录');
  const sep = root.includes('/') ? '/' : '\\';
  const base = root.replace(/[\\/]+$/, '');
  categories.value.forEach((c) => { c.targetRoot = `${base}${sep}${c.name}`; });
  ElMessage.success('已套用到全部分类，可逐个微调');
}
async function saveArchive() {
  archiveSaving.value = true;
  try {
    // 逐个保存分类（顺序，避免并发）；落点为空的跳过（不强制填完，Dashboard 卡片会继续提醒）
    for (const c of categories.value) {
      const root = (c.targetRoot || '').trim();
      if (!root) continue;
      await api.categories.update(c.id, {
        name: c.name, mediaType: c.mediaType, targetRoot: root,
        priority: c.priority, description: c.description, rowVersion: c.rowVersion ?? 0,
      });
    }
    await api.generalSettings.update({
      items: [
        { key: 'File.Operation', value: fileOperation.value },
        { key: 'Archive_ConflictPolicy', value: conflictPolicy.value },
      ],
    });
    const c = await api.categories.list();
    categories.value = c?.items || c || [];
    active.value = 4;
  } finally {
    archiveSaving.value = false;
  }
}

// ---- 步骤 4：AI ----
function applyAiPreset(ps) {
  aiForm.value.type = ps.type;
  aiForm.value.costTier = ps.cost;
  aiForm.value.baseUrl = ps.baseUrl;
  aiForm.value.model = ps.model;
  if (!aiForm.value.name?.trim()) aiForm.value.name = ps.name;
}
async function addAi() {
  if (!aiForm.value.name || !aiForm.value.baseUrl) return ElMessage.warning('名称与 BaseUrl 必填');
  busy.value = true;
  try {
    await api.parseAiProviders.create({
      name: aiForm.value.name.trim(),
      type: aiForm.value.type,
      costTier: aiForm.value.costTier,
      baseUrl: aiForm.value.baseUrl.trim(),
      model: aiForm.value.model.trim(),
      apiKey: aiForm.value.apiKey,
      structuredJson: true,
      isPrimary: false,
      enabled: true,
      priority: 100,
      confidenceThreshold: null,
      timeoutSeconds: 30,
      useProxy: false,
      extraOptions: null,
    });
    aiList.value = (await api.parseAiProviders.list()) || [];
    aiForm.value = { name: '', type: 'Ollama', costTier: 'Free', baseUrl: '', model: '', apiKey: '' };
    ElMessage.success('已添加 AI 提供商');
  } finally {
    busy.value = false;
  }
}
async function testAi(row) {
  const r = (await api.parseAiProviders.test(row.id)) || {};
  if (r.success) ElMessage.success(`${row.name} 连接正常 · ${(Number(r.elapsedMilliseconds) || 0).toFixed(0)}ms`);
  else {
    const status = r.httpStatus != null ? `HTTP ${r.httpStatus} · ` : '';
    ElMessage.error(`${row.name} 连接失败：${status}${r.errorMessage || '未知错误'}`);
  }
}
async function goFinish() {
  checklist.value = await api.setup.checklist().catch(() => null);
  active.value = 5;
}

// ---- 步骤 5：完成 ----
async function finish() {
  busy.value = true;
  try {
    await api.setup.complete();
    auth.setupCompleted = true;
    ElMessage.success('初始化完成，已进入系统');
    router.push({ name: 'Dashboard' });
  } finally {
    busy.value = false;
  }
}

// ---- 断点续配：账号已建（中途退出再进）时跳过欢迎/账号步 ----
onMounted(async () => {
  const st = await api.setup.status().catch(() => null);
  if (st?.isCompleted) { router.replace({ name: 'Dashboard' }); return; }
  if (st?.hasAdmin) {
    started.value = true;
    if (!auth.isLoggedIn) await auth.fetchMe();
    if (auth.isLoggedIn) {
      active.value = 1;
      await loadExistingConfig();
    } else {
      accountMode.value = 'login';
      active.value = 0;
    }
  }
});
</script>

<template>
  <div class="setup-screen">
    <div class="auth-glow" aria-hidden="true" />
    <div class="setup-card card">
      <div class="setup-brand">
        <PmmBrandMark class="brand-mark" :size="30" :show-status-dot="false" />
        <div class="brand-name">PersonalMedia<span>Manager</span></div>
      </div>

      <!-- 欢迎页 -->
      <template v-if="!started">
        <div class="welcome">
          <span class="eyebrow">首次启动</span>
          <h1 class="h1">欢迎，先完成首次配置</h1>
          <p class="muted">接下来约 3–5 分钟，引导你配齐让系统跑起来所需的设置。可随时跳过、稍后在「设置」里补。</p>
          <ul class="welcome-list">
            <li><b>管理员账号</b> · 登录与所有设置（必需）</li>
            <li><b>TMDB</b> · 刮削元数据的唯一来源（必需）</li>
            <li><b>监控目录</b> · 系统监控新影视文件的位置（必需）</li>
            <li><b>归档设置</b> · 分类落点 + 文件操作方式（必需）</li>
            <li><b>AI 提供商</b> · 非标准命名的智能兜底（可选）</li>
          </ul>
          <el-button type="primary" class="wide-btn" @click="started = true">开始配置</el-button>
        </div>
      </template>

      <!-- 向导主体 -->
      <template v-else>
        <el-steps :active="active" finish-status="success" align-center class="setup-steps">
          <el-step title="账号" />
          <el-step title="TMDB" />
          <el-step title="监控目录" />
          <el-step title="归档设置" />
          <el-step title="AI" />
          <el-step title="完成" />
        </el-steps>

        <div class="step-body">
          <!-- 步骤 0：账号 -->
          <section v-if="active === 0" class="step">
            <h2 class="step-title">{{ accountMode === 'login' ? '登录以继续配置' : '创建管理员账户' }}</h2>
            <p class="step-desc" v-if="accountMode === 'login'">管理员已创建，请登录后继续未完成的配置。</p>
            <p class="step-desc" v-else>系统的唯一管理员账号，用于登录与所有设置。</p>
            <el-form label-position="top" @submit.prevent="submitAccount">
              <el-form-item label="账号">
                <el-input v-model="account.username" maxlength="32" placeholder="管理员账号" />
              </el-form-item>
              <el-form-item label="密码">
                <el-input v-model="account.password" type="password" show-password placeholder="至少 6 位" />
              </el-form-item>
              <el-form-item v-if="accountMode !== 'login'" label="确认密码">
                <el-input v-model="account.confirm" type="password" show-password />
              </el-form-item>
            </el-form>
            <div class="step-nav">
              <span />
              <el-button type="primary" :loading="busy" @click="submitAccount">
                {{ accountMode === 'login' ? '登录并继续' : '创建并继续' }}
              </el-button>
            </div>
          </section>

          <!-- 步骤 1：TMDB -->
          <section v-else-if="active === 1" class="step">
            <h2 class="step-title">TMDB 元数据源</h2>
            <p class="step-desc">刮削元数据的唯一来源。需 v4 Read Access Token（eyJ 开头）。不配则刮削会失败。</p>
            <el-form label-position="top">
              <el-form-item label="API Read Access Token (v4)">
                <el-input v-if="tmdbEditKey" v-model="tmdb.apiKey" type="password" show-password placeholder="eyJ... 开头的长串" />
                <div v-else class="key-set">
                  <span class="ok-text">已配置</span>
                  <el-button text @click="tmdbEditKey = true; tmdb.apiKey = ''">点击修改</el-button>
                </div>
              </el-form-item>
              <div v-if="tmdbEditKey && tmdbTokenWarn" class="warn-line">{{ tmdbTokenWarn }}</div>
              <div class="form-row">
                <el-form-item label="优先语言">
                  <el-select v-model="tmdb.language" class="w-full">
                    <el-option v-for="l in TMDB_LANGS" :key="l.value" :label="l.label" :value="l.value" />
                  </el-select>
                </el-form-item>
                <el-form-item label="回退语言">
                  <el-select v-model="tmdb.fallbackLanguage" class="w-full">
                    <el-option v-for="l in TMDB_LANGS" :key="l.value" :label="l.label" :value="l.value" />
                  </el-select>
                </el-form-item>
              </div>
              <el-button :loading="tmdbTesting" @click="testTmdb">测试连接</el-button>
            </el-form>

            <el-alert v-if="showProxy" type="warning" :closable="false" class="proxy-rescue"
              title="连接失败？可能需要代理" show-icon>
              <div class="proxy-form">
                <el-switch v-model="proxy.enabled" active-text="启用代理" />
                <el-input v-model="proxy.httpUrl" placeholder="http://127.0.0.1:7890" :disabled="!proxy.enabled" />
                <el-button :loading="tmdbTesting" :disabled="!proxy.enabled" @click="saveProxyAndRetest">
                  保存代理并重测
                </el-button>
              </div>
            </el-alert>

            <div class="step-nav">
              <el-button @click="active = 0">上一步</el-button>
              <div class="nav-right">
                <el-button text @click="active = 2">暂时跳过</el-button>
                <el-button type="primary" :loading="busy" @click="nextFromTmdb">保存并下一步</el-button>
              </div>
            </div>
          </section>

          <!-- 步骤 2：监控目录 -->
          <section v-else-if="active === 2" class="step">
            <h2 class="step-title">监控目录</h2>
            <p class="step-desc">系统会监控这些目录里的新影视文件。没有监控目录就没有文件可处理。</p>
            <div class="add-watch">
              <el-input v-model="watchForm.path" placeholder="目录路径，如 D:\Downloads 或 \\NAS\media" class="grow" />
              <el-input v-model="watchForm.alias" placeholder="别名（可选）" class="alias" />
              <el-switch v-model="watchForm.isNetworkShare" active-text="网络盘" />
              <el-button type="primary" :loading="watchAdding" @click="addWatch">添加</el-button>
            </div>
            <ul v-if="watchList.length" class="row-list">
              <li v-for="w in watchList" :key="w.id">
                <span class="mono ellipsis">{{ w.path }}</span>
                <span v-if="w.isNetworkShare" class="mini-tag">网络盘</span>
                <span class="grow" />
                <el-button text size="small" @click="testWatch(w)">测试连通</el-button>
                <el-button text size="small" type="danger" @click="removeWatch(w)">移除</el-button>
              </li>
            </ul>
            <p v-else class="empty-hint">尚未添加监控目录</p>
            <div class="step-nav">
              <el-button @click="active = 1">上一步</el-button>
              <div class="nav-right">
                <el-button text @click="active = 3">暂时跳过</el-button>
                <el-button type="primary" @click="active = 3">下一步</el-button>
              </div>
            </div>
          </section>

          <!-- 步骤 3：归档设置 -->
          <section v-else-if="active === 3" class="step">
            <h2 class="step-title">归档设置</h2>
            <p class="step-desc">分类决定每类媒体归档到哪个目录。内置 5 个分类已就绪，只需设置它们的落点。</p>
            <div class="lib-root">
              <el-input v-model="libraryRoot" placeholder="媒体库根目录，如 D:\Media" class="grow" />
              <el-button @click="applyLibraryRoot">一键套用到全部</el-button>
            </div>
            <div class="cat-list">
              <div v-for="c in categories" :key="c.id" class="cat-row">
                <div class="cat-name">{{ c.name }}<span class="mini-tag">{{ MEDIA_TYPE_LABEL[c.mediaType] || c.mediaType }}</span></div>
                <el-input v-model="c.targetRoot" placeholder="目标根目录" class="mono"
                  :class="{ 'is-unset': !c.targetRoot || c.targetRoot === '<UNSET>' }" />
              </div>
            </div>
            <div v-if="unsetCount" class="warn-line">还有 {{ unsetCount }} 个分类未设置落点，归档时会失败。</div>

            <div class="form-row archive-opts">
              <div class="opt-block">
                <label class="opt-label">文件操作方式</label>
                <div class="pills">
                  <button v-for="op in FILE_OPS" :key="op.value" type="button" class="pill"
                    :class="{ active: fileOperation === op.value }" @click="fileOperation = op.value">{{ op.label }}</button>
                </div>
                <div class="op-desc">{{ fileOpDesc }}</div>
              </div>
              <div class="opt-block">
                <label class="opt-label">同名冲突处理</label>
                <el-select v-model="conflictPolicy" class="w-full">
                  <el-option v-for="p in CONFLICT_POLICIES" :key="p.value" :label="p.label" :value="p.value" />
                </el-select>
              </div>
            </div>

            <div class="step-nav">
              <el-button @click="active = 2">上一步</el-button>
              <div class="nav-right">
                <el-button text @click="active = 4">暂时跳过</el-button>
                <el-button type="primary" :loading="archiveSaving" @click="saveArchive">保存并下一步</el-button>
              </div>
            </div>
          </section>

          <!-- 步骤 4：AI -->
          <section v-else-if="active === 4" class="step">
            <h2 class="step-title">AI 提供商 <span class="optional">可选</span></h2>
            <p class="step-desc">规则解析不够用时，AI 兜底识别非标准命名。不配则这类文件进人工确认队列（不影响标准命名自动处理）。</p>
            <div class="pills presets">
              <button v-for="ps in AI_PRESETS" :key="ps.name" type="button" class="pill" @click="applyAiPreset(ps)">{{ ps.name }}</button>
            </div>
            <el-form label-position="top">
              <div class="form-row">
                <el-form-item label="名称"><el-input v-model="aiForm.name" placeholder="如 DeepSeek" /></el-form-item>
                <el-form-item label="协议">
                  <el-select v-model="aiForm.type" class="w-full">
                    <el-option v-for="t in AI_PROTOCOLS" :key="t" :label="t" :value="t" />
                  </el-select>
                </el-form-item>
              </div>
              <el-form-item label="BaseUrl"><el-input v-model="aiForm.baseUrl" placeholder="https://api.deepseek.com" /></el-form-item>
              <div class="form-row">
                <el-form-item label="模型"><el-input v-model="aiForm.model" placeholder="deepseek-chat" /></el-form-item>
                <el-form-item label="API Key（本地 Ollama 可空）"><el-input v-model="aiForm.apiKey" type="password" show-password /></el-form-item>
              </div>
              <el-button type="primary" :loading="busy" @click="addAi">添加</el-button>
            </el-form>
            <ul v-if="aiList.length" class="row-list">
              <li v-for="a in aiList" :key="a.id">
                <span class="ellipsis">{{ a.name }}<span class="mini-tag">{{ a.type }}</span></span>
                <span class="grow" />
                <el-button text size="small" @click="testAi(a)">测试</el-button>
              </li>
            </ul>
            <div class="step-nav">
              <el-button @click="active = 3">上一步</el-button>
              <div class="nav-right">
                <el-button text @click="goFinish">暂时跳过</el-button>
                <el-button type="primary" @click="goFinish">下一步</el-button>
              </div>
            </div>
          </section>

          <!-- 步骤 5：完成 -->
          <section v-else-if="active === 5" class="step">
            <h2 class="step-title">配置完成</h2>
            <p class="step-desc">下面是当前配置就绪度。标 ⚠ 的项可稍后在「设置」里补全。</p>
            <ul v-if="checklist" class="checklist">
              <li :class="checklist.hasAdmin ? 'ok' : 'warn'">
                <span class="ck-icon">{{ checklist.hasAdmin ? '✓' : '⚠' }}</span> 管理员账号
              </li>
              <li :class="checklist.tmdbHasApiKey ? 'ok' : 'warn'">
                <span class="ck-icon">{{ checklist.tmdbHasApiKey ? '✓' : '⚠' }}</span>
                TMDB API Token{{ checklist.tmdbHasApiKey ? '' : '（未配置，刮削会失败）' }}
              </li>
              <li :class="checklist.watchFolderCount > 0 ? 'ok' : 'warn'">
                <span class="ck-icon">{{ checklist.watchFolderCount > 0 ? '✓' : '⚠' }}</span>
                监控目录（{{ checklist.watchFolderCount }} 个）
              </li>
              <li :class="checklist.categoryUnsetCount === 0 ? 'ok' : 'warn'">
                <span class="ck-icon">{{ checklist.categoryUnsetCount === 0 ? '✓' : '⚠' }}</span>
                分类落点{{ checklist.categoryUnsetCount > 0 ? `（还有 ${checklist.categoryUnsetCount} 个未设置）` : '' }}
              </li>
              <li :class="checklist.aiProviderCount > 0 ? 'ok' : 'muted'">
                <span class="ck-icon">{{ checklist.aiProviderCount > 0 ? '✓' : '○' }}</span>
                AI 提供商（{{ checklist.aiProviderCount }} 个，可选）
              </li>
            </ul>
            <p class="more-link muted">需要更多设置（归档命名风格、字幕、通知、代理…）？进入系统后在左侧「设置」里随时调整。</p>
            <div class="step-nav">
              <el-button @click="active = 4">上一步</el-button>
              <el-button type="primary" :loading="busy" @click="finish">进入系统</el-button>
            </div>
          </section>
        </div>
      </template>

      <div class="setup-foot dim">v{{ productVersion }} · Personal Media Manager</div>
    </div>
  </div>
</template>

<style scoped lang="scss">
.setup-screen {
  position: relative;
  min-height: 100vh;
  display: grid;
  place-items: start center;
  padding: 40px 16px;
  background:
    radial-gradient(900px 500px at 20% -100px, var(--accent-soft), transparent),
    radial-gradient(700px 500px at 110% 110%, color-mix(in oklab, var(--accent) 18%, transparent), transparent),
    var(--bg);
  overflow-x: hidden;
}
.auth-glow {
  position: absolute;
  inset: 0;
  background:
    radial-gradient(circle at 30% 40%, rgba(229, 160, 13, 0.08), transparent 40%),
    radial-gradient(circle at 70% 60%, rgba(96, 165, 250, 0.05), transparent 45%);
  pointer-events: none;
}
.setup-card {
  position: relative;
  width: min(760px, 96vw);
  padding: 30px 34px 24px;
  background: var(--bg-elev);
  border: 1px solid var(--border);
  border-radius: var(--r-4);
  box-shadow: var(--shadow-3);
  display: flex;
  flex-direction: column;
  gap: 22px;
}
.setup-brand {
  display: flex;
  align-items: center;
  gap: 10px;
}
.brand-name { font-weight: 700; }
.brand-name span { color: var(--accent); }

.welcome .h1 { margin: 8px 0 0; font-size: 26px; }
.welcome .muted { margin: 8px 0 0; font-size: 13px; }
.welcome-list {
  margin: 18px 0 22px;
  padding: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.welcome-list li {
  font-size: 14px;
  padding-left: 16px;
  position: relative;
  color: var(--text-2, var(--text));
}
.welcome-list li::before {
  content: '';
  position: absolute;
  left: 0; top: 8px;
  width: 6px; height: 6px;
  border-radius: 50%;
  background: var(--accent);
}
.welcome-list b { color: var(--text); }
.wide-btn { width: 100%; height: 42px; font-weight: 700; }

.setup-steps { margin: 4px 0 6px; }
.step-body { min-height: 280px; }
.step { display: flex; flex-direction: column; gap: 14px; }
.step-title { margin: 0; font-size: 20px; }
.step-title .optional {
  font-size: 12px;
  font-weight: 500;
  color: var(--text-soft, var(--accent));
  border: 1px solid var(--border);
  border-radius: 999px;
  padding: 1px 8px;
  margin-left: 6px;
  vertical-align: middle;
}
.step-desc { margin: 0; font-size: 13px; color: var(--text-soft, inherit); opacity: 0.85; }

.form-row { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
.w-full { width: 100%; }
.warn-line {
  font-size: 12.5px;
  color: var(--warning, #c97a0e);
  background: color-mix(in oklab, var(--warning, #c97a0e) 10%, transparent);
  border: 1px solid color-mix(in oklab, var(--warning, #c97a0e) 30%, transparent);
  border-radius: var(--r-2, 8px);
  padding: 8px 10px;
}
.key-set { display: flex; align-items: center; gap: 8px; }
.ok-text { color: var(--success, #2c9e5b); font-weight: 600; font-size: 13px; }

.proxy-rescue { margin-top: 4px; }
.proxy-form { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-top: 8px; }
.proxy-form .el-input { flex: 1; min-width: 200px; }

.add-watch { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.add-watch .grow { flex: 1; min-width: 220px; }
.add-watch .alias { width: 140px; }
.lib-root { display: flex; gap: 10px; }
.lib-root .grow { flex: 1; }

.row-list { list-style: none; margin: 4px 0 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.row-list li {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 7px 10px;
  border: 1px solid var(--border);
  border-radius: var(--r-2, 8px);
  background: var(--surface, transparent);
  font-size: 13px;
}
.grow { flex: 1; }
.ellipsis { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 360px; }
.mono { font-family: var(--font-mono, ui-monospace, monospace); }
.mini-tag {
  font-size: 11px;
  color: var(--text-soft, inherit);
  border: 1px solid var(--border);
  border-radius: 4px;
  padding: 0 5px;
  margin-left: 6px;
}
.empty-hint { font-size: 13px; opacity: 0.6; margin: 6px 0; }

.cat-list { display: flex; flex-direction: column; gap: 10px; }
.cat-row { display: grid; grid-template-columns: 150px 1fr; align-items: center; gap: 12px; }
.cat-name { font-size: 14px; font-weight: 600; }
.is-unset :deep(.el-input__wrapper) {
  box-shadow: 0 0 0 1px var(--danger, #d05656) inset;
}

.archive-opts { margin-top: 6px; align-items: start; }
.opt-block { display: flex; flex-direction: column; gap: 8px; }
.opt-label { font-size: 13px; font-weight: 600; }
.op-desc { font-size: 12px; opacity: 0.75; min-height: 16px; }

.pills { display: flex; flex-wrap: wrap; gap: 8px; }
.pill {
  font-size: 13px;
  padding: 6px 12px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--surface, transparent);
  color: var(--text);
  cursor: pointer;
  transition: all 0.15s;
}
.pill:hover { border-color: var(--accent); }
.pill.active { background: var(--accent); color: #fff; border-color: var(--accent); }
.presets { margin-bottom: 4px; }

.checklist { list-style: none; margin: 4px 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
.checklist li { display: flex; align-items: center; gap: 8px; font-size: 14px; }
.checklist .ck-icon { width: 18px; text-align: center; font-weight: 700; }
.checklist .ok .ck-icon { color: var(--success, #2c9e5b); }
.checklist .warn { color: var(--warning, #c97a0e); }
.checklist .warn .ck-icon { color: var(--warning, #c97a0e); }
.checklist .muted { opacity: 0.6; }
.more-link { font-size: 12.5px; margin: 8px 0 0; }

.step-nav {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-top: 8px;
  padding-top: 16px;
  border-top: 1px solid var(--border-soft, var(--border));
}
.nav-right { display: flex; gap: 8px; align-items: center; }

.setup-foot {
  text-align: center;
  font-size: 12px;
  letter-spacing: 0.04em;
  border-top: 1px solid var(--border-soft, var(--border));
  padding-top: 14px;
}
</style>
