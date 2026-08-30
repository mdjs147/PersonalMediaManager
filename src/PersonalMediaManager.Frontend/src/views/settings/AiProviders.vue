<script setup>
import { computed, onMounted, ref } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';
import { useRouter } from 'vue-router';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';
import { formatQuota } from '@/utils/format';

const router = useRouter();

/** 协议预设：选协议时按此预填默认（成本档位 / BaseUrl 占位 / 模型占位）；预设是数据，不参与分发 */
const PROTOCOLS = [
  { value: 'Ollama', label: 'Ollama（本地）', baseUrl: 'http://localhost:11434', cost: 'Free', model: 'qwen2.5:7b' },
  { value: 'OpenAiCompatible', label: 'OpenAI 兼容（Qwen / DeepSeek / Kimi / 本地 LM Studio…）', baseUrl: 'https://api.deepseek.com', cost: 'Paid', model: 'deepseek-chat' },
  { value: 'Anthropic', label: 'Anthropic（Claude）', baseUrl: 'https://api.anthropic.com', cost: 'Paid', model: 'claude-haiku-4-5-20251001' },
  { value: 'Gemini', label: 'Google Gemini', baseUrl: 'https://generativelanguage.googleapis.com', cost: 'Paid', model: 'gemini-1.5-flash' },
  { value: 'AzureOpenAi', label: 'Azure OpenAI', baseUrl: 'https://your-resource.openai.azure.com', cost: 'Paid', model: '你的-deployment-名' },
];
const PROTO_LABEL = Object.fromEntries(PROTOCOLS.map((p) => [p.value, p.label]));

/**
 * 常用提供商快速预设：点一下自动填「协议 / 档位 / BaseUrl / 模型」，用户只需再补 API Key。
 * BaseUrl 按各家官方端点填写，最终请求路径由后端 JoinUrl 智能拼接（/v1、/v4、自定义前缀均正确）。
 * model 仅为常用示例，可自行改成所需型号；除 Ollama 外均需在各家官网申请 API Key。
 */
const PRESETS = [
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

/**
 * 各协议 BaseUrl 填写说明 + 系统自动拼接的固定路径（path 与后端各 *Protocol.cs 逐字一致）。
 * 后端 JoinUrl 已按「路径段重叠」智能去重：BaseUrl 只填域名或带 /v1 都能识别，故文案强调「都行」。
 */
const URL_HELP = {
  Ollama: { path: '/api/chat', note: '填到 主机:端口 即可，无需路径。' },
  OpenAiCompatible: { path: '/v1/chat/completions', note: '填到域名或带版本号都行：第三方要求的 /v1（甚至 /v2、/openai/v1）原样保留，系统只补 /chat/completions，不会拼成 /v1/v1。' },
  Anthropic: { path: '/v1/messages', note: '填到域名或带 /v1 都行，系统会自动补全为 /v1/messages（不会拼成 /v1/v1）。' },
  Gemini: { path: '/v1beta/models/{model}:generateContent', note: '填到域名即可，无需 /v1beta 或模型路径，系统按所填 Model 自动拼接。' },
  AzureOpenAi: { path: '/openai/deployments/{model}/chat/completions', note: '填资源终结点（到 .openai.azure.com 为止）；Model 处填 deployment 名，api-version 用默认。' },
};

/** 前端预览：复刻后端 JoinUrl 的「路径段重叠去重 + 版本槽合并」，让用户实时看到最终请求 URL（误填 /v1 显示纠正后结果，/v2 等用户版本原样保留） */
function joinUrlPreview(base, path) {
  const trimmed = (base || '').replace(/\/+$/, '');
  const p = path.startsWith('/') ? path : '/' + path;
  let u;
  try {
    u = new URL(trimmed);
  } catch {
    return trimmed + p; // 非合法绝对 URL：与后端一致退回朴素拼接
  }
  const baseSegs = u.pathname.split('/').filter(Boolean);
  const pathSegs = p.split('/').filter(Boolean);
  const isVer = (s) => /^v\d/i.test(s); // 版本号段：v1 / v2 / v1beta…
  // 版本槽合并：两端都是版本号段但不同 → 保留用户显式版本（/v2、/openai/v1…），仅追加 path 余下操作段
  if (
    baseSegs.length &&
    pathSegs.length &&
    baseSegs[baseSegs.length - 1].toLowerCase() !== pathSegs[0].toLowerCase() &&
    isVer(baseSegs[baseSegs.length - 1]) &&
    isVer(pathSegs[0])
  ) {
    return `${u.origin}/${baseSegs.concat(pathSegs.slice(1)).join('/')}`;
  }
  let overlap = 0;
  for (let k = Math.min(baseSegs.length, pathSegs.length); k >= 1; k--) {
    let ok = true;
    for (let i = 0; i < k; i++) {
      if (baseSegs[baseSegs.length - k + i].toLowerCase() !== pathSegs[i].toLowerCase()) {
        ok = false;
        break;
      }
    }
    if (ok) {
      overlap = k;
      break;
    }
  }
  const merged = baseSegs.slice(0, baseSegs.length - overlap).concat(pathSegs);
  return merged.length ? `${u.origin}/${merged.join('/')}` : u.origin;
}

const list = ref([]);
const loading = ref(false);
const dialogVisible = ref(false);
const editing = ref(null);
const reordering = ref(false);
/** 正在测试连接的 provider id 集合 */
const testingIds = ref(new Set());
/** 代理总开关 + 是否已配置代理地址 */
const proxyReady = ref(false);
/** 全局满意度阈值（回退用，列表卡片展示「全局」来源时用）；后端 effectiveThreshold 已算好，这里仅作链路头展示 */
const globalThreshold = ref(0.7);

const blankForm = () => ({
  name: '',
  type: 'Ollama',
  costTier: 'Free',
  structuredJson: true,
  baseUrl: '',
  model: '',
  apiKey: '',
  isPrimary: false,
  enabled: true,
  priority: 100,
  confidenceThreshold: null,
  timeoutSeconds: 30,
  extraOptions: null,
  useProxy: false,
  quotaCallLimit: null,
  quotaTokenLimit: null,
  quotaExpiresAt: null,
  quotaPeriod: 'None',
  quotaPeriodTimeZone: null,
  quotaPeriodCallLimit: null,
  quotaPeriodTokenLimit: null,
  rpmLimit: null,
});
const form = ref(blankForm());

const curProto = computed(() => PROTOCOLS.find((p) => p.value === form.value.type) || PROTOCOLS[0]);
const curUrlHelp = computed(() => URL_HELP[form.value.type] || URL_HELP.OpenAiCompatible);
/** 实际请求 URL 实时预览（拿当前 BaseUrl/Model 复刻后端拼接，写错 /v1 也能立刻看到被纠正） */
const urlPreview = computed(() => {
  const base = form.value.baseUrl || curProto.value.baseUrl || '';
  const path = curUrlHelp.value.path.replace('{model}', form.value.model || '{model}');
  return joinUrlPreview(base, path);
});
const isFree = (row) => row.costTier === 'Free';

/** 应用快速预设：填协议/档位/BaseUrl/模型；名称为空时一并带出，已填则尊重用户输入 */
function applyPreset(ps) {
  form.value.type = ps.type;
  form.value.costTier = ps.cost;
  form.value.baseUrl = ps.baseUrl;
  form.value.model = ps.model;
  if (!form.value.name?.trim()) form.value.name = ps.name;
}

function circuitState(row) {
  if (!row.disabledUntil) return null;
  const until = new Date(row.disabledUntil);
  if (Number.isNaN(until.getTime()) || until.getTime() <= Date.now()) return null;
  const hh = String(until.getHours()).padStart(2, '0');
  const mm = String(until.getMinutes()).padStart(2, '0');
  return `自动熔断中 · ${hh}:${mm} 恢复`;
}

function thresholdText(row) {
  const v = row.effectiveThreshold != null ? Number(row.effectiveThreshold).toFixed(2) : '—';
  const src = row.thresholdSource === 'custom' ? '自定义' : '全局';
  return `${v}（${src}）`;
}

/** 套餐超限（后端写标记自动禁用）：quotaExceededAt 非空即已被剔出升级链 */
function quotaExceeded(row) {
  return !!row.quotaExceededAt;
}

/** 套餐已到期（纯时间判断，后端不写标记）：到期时间 <= 当前即自动禁用 */
function quotaExpired(row) {
  if (!row.quotaExpiresAt) return false;
  const t = new Date(row.quotaExpiresAt).getTime();
  return !Number.isNaN(t) && t <= Date.now();
}

/** 是否配置了任一用量限额（次数 / token） */
function hasQuotaLimit(row) {
  return row.quotaCallLimit != null || row.quotaTokenLimit != null;
}

/** 配额进度条数据：已用/上限 + 百分比；≥80% 警示色、≥100% 危险色 */
function quotaBars(row) {
  const bars = [];
  const push = (label, used, limit, fmt) => {
    const pct = limit > 0 ? Math.round(((used || 0) / limit) * 100) : 0;
    bars.push({
      label,
      text: `${fmt(used || 0)} / ${fmt(limit)}`,
      pct,
      width: Math.min(100, pct),
      color: pct >= 100 ? 'var(--danger)' : pct >= 80 ? 'var(--warning)' : 'var(--accent)',
    });
  };
  if (row.quotaCallLimit != null) push('次数', row.quotaUsedCalls, row.quotaCallLimit, (v) => Number(v).toLocaleString());
  if (row.quotaTokenLimit != null) push('Token', row.quotaUsedTokens, row.quotaTokenLimit, formatQuota);
  return bars;
}

/** 套餐到期日卡片展示：「套餐至 YYYY-MM-DD」（本地时区） */
function quotaExpiryText(row) {
  if (!row.quotaExpiresAt) return '';
  const d = new Date(row.quotaExpiresAt);
  if (Number.isNaN(d.getTime())) return '';
  return `套餐至 ${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/** 周期滚动额度是否启用（None / 空 = 未启用） */
function hasPeriodQuota(row) {
  return row.quotaPeriod && row.quotaPeriod !== 'None';
}
const PERIOD_LABEL = { Daily: '每日', Weekly: '每周', Monthly: '每月' };
/** 周期额度展示文本：粒度 + 次数/token 已用/上限（周期到边界自动重置，故只列文本不画进度条） */
function periodQuotaText(row) {
  if (!hasPeriodQuota(row)) return '';
  const parts = [];
  if (row.quotaPeriodCallLimit != null)
    parts.push(`次数 ${Number(row.quotaPeriodUsedCalls || 0).toLocaleString()}/${Number(row.quotaPeriodCallLimit).toLocaleString()}`);
  if (row.quotaPeriodTokenLimit != null)
    parts.push(`Token ${formatQuota(row.quotaPeriodUsedTokens || 0)}/${formatQuota(row.quotaPeriodTokenLimit)}`);
  const label = PERIOD_LABEL[row.quotaPeriod] || row.quotaPeriod;
  return parts.length ? `${label}额度 · ${parts.join(' · ')}（到边界自动重置）` : `${label}额度（到边界自动重置）`;
}

/** 「重置用量」入口条件：已超限必显；配置了限额且已有用量时也可提前手动清零（新套餐周期） */
function canResetQuota(row) {
  return quotaExceeded(row) || (hasQuotaLimit(row) && ((row.quotaUsedCalls || 0) > 0 || (row.quotaUsedTokens || 0) > 0));
}

/** ISO 时间 → <input type="date"> 值（本地时区 YYYY-MM-DD）；空/无效返 null */
function isoToDateInput(iso) {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/** <input type="date"> 值 → 当地时区当天 23:59:59.999 的 ISO 串；空/无效返 null（= 清除到期限制） */
function dateInputToIso(v) {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(v || '').trim());
  if (!m) return null;
  const d = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]), 23, 59, 59, 999);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}

/** 限额数字归一：空/非数归 null（= 不限）；有效数字向下取整、最小 1 */
function normQuotaLimit(v) {
  if (v === '' || v == null) return null;
  const n = Math.floor(Number(v));
  return Number.isFinite(n) ? Math.max(1, n) : null;
}

async function load() {
  loading.value = true;
  try {
    const [data, generalData] = await Promise.all([
      api.parseAiProviders.list(),
      api.generalSettings.get().catch(() => null),
    ]);
    list.value = data?.items || data || [];
    const items = [];
    if (generalData?.groups) {
      Object.values(generalData.groups).forEach((arr) => arr.forEach((it) => items.push(it)));
    }
    const kv = Object.fromEntries(items.map((it) => [it.key, it.value]));
    proxyReady.value = kv.Proxy_Enabled === 'true' && !!(kv.Proxy_HttpUrl || '').trim();
    const gt = Number(kv['Parse.AiConfidenceThreshold']);
    if (!Number.isNaN(gt) && gt >= 0 && gt <= 1) globalThreshold.value = gt;
  } finally {
    loading.value = false;
  }
}

function openAdd() {
  editing.value = null;
  form.value = blankForm();
  dialogVisible.value = true;
}

function openEdit(row) {
  editing.value = row;
  // quotaExpiresAt：后端 ISO 串 → date 输入框值（本地时区年月日）
  form.value = { ...blankForm(), ...row, apiKey: '', quotaExpiresAt: isoToDateInput(row.quotaExpiresAt) };
  dialogVisible.value = true;
}

async function save() {
  if (!form.value.name || !form.value.baseUrl) {
    ElMessage.warning('名称与 BaseUrl 必填');
    return;
  }
  const timeout = Number(form.value.timeoutSeconds);
  if (!Number.isFinite(timeout) || timeout < 5 || timeout > 600) {
    ElMessage.warning('超时时间需在 5~600 秒之间');
    return;
  }
  const payload = {
    ...form.value,
    isPrimary: false,
    timeoutSeconds: timeout,
    confidenceThreshold:
      form.value.confidenceThreshold === '' || form.value.confidenceThreshold == null
        ? null
        : Number(form.value.confidenceThreshold),
    // 套餐限额：空/无效归 null（= 不限 / 清除）；到期日转当地时区当天 23:59:59.999
    quotaCallLimit: normQuotaLimit(form.value.quotaCallLimit),
    quotaTokenLimit: normQuotaLimit(form.value.quotaTokenLimit),
    quotaExpiresAt: dateInputToIso(form.value.quotaExpiresAt),
    // 周期滚动额度：粒度 None 时不启用（限额一并清空）；限额空/无效归 null（= 不限）；时区留空 = 本机
    quotaPeriod: form.value.quotaPeriod || 'None',
    quotaPeriodTimeZone: (form.value.quotaPeriodTimeZone || '').trim() || null,
    quotaPeriodCallLimit: form.value.quotaPeriod === 'None' ? null : normQuotaLimit(form.value.quotaPeriodCallLimit),
    quotaPeriodTokenLimit: form.value.quotaPeriod === 'None' ? null : normQuotaLimit(form.value.quotaPeriodTokenLimit),
    // RPM 每分钟请求上限：空/无效归 null（= 不限流）
    rpmLimit: normQuotaLimit(form.value.rpmLimit),
  };
  // apiKey 三态：编辑时留空 = 保持不变（不下发该字段）
  if (editing.value && !payload.apiKey) delete payload.apiKey;
  if (editing.value) {
    await api.parseAiProviders.update(editing.value.id, payload);
    ElMessage.success('已更新');
  } else {
    await api.parseAiProviders.create(payload);
    ElMessage.success('已添加');
  }
  dialogVisible.value = false;
  load();
}

async function toggle(row) {
  await api.parseAiProviders.update(row.id, { ...row, apiKey: undefined, enabled: !row.enabled });
  load();
}

/** 链路重排：上移/下移调整优先级（priority 按新位次 *10 回写）；同时清掉 isPrimary —— 已无「主用」概念，纯按优先级排序 */
async function move(idx, dir) {
  const j = idx + dir;
  if (reordering.value || j < 0 || j >= list.value.length) return;
  reordering.value = true;
  try {
    const arr = [...list.value];
    [arr[idx], arr[j]] = [arr[j], arr[idx]];
    list.value = arr;
    // 顺序回写：逐行 priority = 位次*10，并清掉 isPrimary（纯优先级排序，杜绝「主」凌驾于排序之上）
    for (let i = 0; i < arr.length; i++) {
      await api.parseAiProviders.update(arr[i].id, { ...arr[i], apiKey: undefined, priority: i * 10, isPrimary: false });
    }
    ElMessage.success('已调整升级顺序');
  } finally {
    reordering.value = false;
    load();
  }
}

async function unban(row) {
  await api.parseAiProviders.enable(row.id);
  ElMessage.success(`已解禁 ${row.name}`);
  load();
}

/** 重置套餐用量：清零已用次数 / token 并解除超限自动禁用（新套餐周期开始时用） */
async function resetQuota(row) {
  await ElMessageBox.confirm(
    `将清零 "${row.name}" 的已用调用次数与 Token 用量，并解除套餐超限禁用，恢复其参与升级链。适用于新套餐周期开始时，继续？`,
    '重置用量',
    { type: 'warning' },
  );
  await api.parseAiProviders.resetQuota(row.id);
  ElMessage.success(`已重置 ${row.name} 的套餐用量`);
  load();
}

async function testConn(row) {
  if (testingIds.value.has(row.id)) return;
  testingIds.value = new Set(testingIds.value).add(row.id);
  const pending = ElMessage({ type: 'info', message: `正在测试 ${row.name} 连接…`, duration: 0, showClose: false });
  try {
    const result = (await api.parseAiProviders.test(row.id)) || {};
    pending.close();
    if (result.success) {
      const elapsed = Number(result.elapsedMilliseconds) || 0;
      ElMessage.success(`${row.name} 连接正常 · ${elapsed.toFixed(0)}ms`);
    } else {
      const status = result.httpStatus != null ? `HTTP ${result.httpStatus} · ` : '';
      ElMessage.error(`${row.name} 连接失败：${status}${result.errorMessage || '未知错误'}`);
    }
  } catch (e) {
    pending.close();
    ElMessage.error(`${row.name} 测试请求异常：${e?.message || e}`);
  } finally {
    testingIds.value.delete(row.id);
    testingIds.value = new Set(testingIds.value);
  }
}

async function remove(row) {
  await ElMessageBox.confirm(`删除提供商 "${row.name}"？`, '提示', { type: 'warning' });
  await api.parseAiProviders.delete(row.id);
  ElMessage.success('已删除');
  load();
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="设置"
      title="AI 提供商"
      subtitle="规则引擎不够用时的兜底通道。按下方阶梯逐级升级：免费档优先，结果不满意 / 接口异常 / 限流 / 达到上限时自动升级到更高级 AI。"
    >
      <template #actions>
        <button class="btn btn-primary btn-sm" @click="openAdd">
          <PmmIcon name="plus" :size="14" /> 新增提供商
        </button>
      </template>
    </PmmPageHeader>

    <div v-if="list.length" class="ladder">
      <div class="ladder-summary">
        <span><b>升级链路</b></span>
        <span class="muted">全局阈值 {{ globalThreshold.toFixed(2) }}</span>
        <span class="muted">·</span>
        <span class="muted">按优先级从上到下逐级升级</span>
        <span class="muted">·</span>
        <span class="muted">全部失败转人工复核</span>
      </div>

      <template v-for="(p, idx) in list" :key="p.id">
        <div class="card rung" :class="{ disabled: !p.enabled || circuitState(p) || quotaExceeded(p) || quotaExpired(p) }">
          <div class="rung-rank">
            <div class="lvl">第 {{ idx + 1 }} 级</div>
            <div class="reorder">
              <button class="mini" :disabled="idx === 0 || reordering" title="上移（更先尝试）" @click="move(idx, -1)">▲</button>
              <button class="mini" :disabled="idx === list.length - 1 || reordering" title="下移（更后尝试）" @click="move(idx, 1)">▼</button>
            </div>
          </div>

          <div class="rung-body">
            <div class="rung-head">
              <span class="name">{{ p.name }}</span>
              <span class="tag" :class="isFree(p) ? 'tag-free' : 'tag-paid'">
                {{ isFree(p) ? '免费' : '计费' }}
              </span>
              <span v-if="!p.enabled" class="tag tag-neutral">已禁用</span>
              <!-- 自动禁用状态（优先级：套餐超限 > 套餐到期 > 健康熔断） -->
              <span v-if="quotaExceeded(p)" class="tag tag-danger">套餐超限 · 已自动禁用</span>
              <span v-else-if="quotaExpired(p)" class="tag tag-warn">套餐已到期 · 已自动禁用</span>
              <span v-else-if="circuitState(p)" class="tag tag-warn">{{ circuitState(p) }}</span>
            </div>
            <div class="muted small">{{ PROTO_LABEL[p.type] || p.type }} · {{ p.model || '未指定模型' }}</div>
            <div class="font-mono endpoint">{{ p.baseUrl }}</div>
            <div class="rung-metrics">
              <span>7天调用 <b>{{ p.calls7d?.toLocaleString?.() ?? '—' }}</b></span>
              <span>成功率 <b>{{ p.successRate != null ? p.successRate + '%' : '—' }}</b></span>
              <span>平均延迟 <b>{{ p.avgLatency != null ? p.avgLatency + ' ms' : '—' }}</b></span>
              <span>满意度阈值 <b>{{ thresholdText(p) }}</b></span>
            </div>
            <!-- 套餐配额行：仅配置了限额 / 到期日时显示 -->
            <div v-if="hasQuotaLimit(p) || p.quotaExpiresAt || hasPeriodQuota(p)" class="rung-quota">
              <div v-for="bar in quotaBars(p)" :key="bar.label" class="quota-line">
                <span class="quota-label">{{ bar.label }}</span>
                <div class="progress quota-progress">
                  <div class="progress-bar" :style="{ width: bar.width + '%', background: bar.color }" />
                </div>
                <span class="quota-text tabular">{{ bar.text }} · {{ bar.pct }}%</span>
              </div>
              <div v-if="hasPeriodQuota(p)" class="quota-period">{{ periodQuotaText(p) }}</div>
              <div v-if="quotaExpiryText(p)" class="quota-expiry" :class="{ expired: quotaExpired(p) }">
                {{ quotaExpiryText(p) }}{{ quotaExpired(p) ? '（已到期）' : '' }}
              </div>
            </div>
          </div>

          <div class="rung-actions">
            <div class="switch" :class="{ on: p.enabled }" title="启用 / 禁用" @click="toggle(p)" />
            <button class="btn btn-ghost btn-sm" @click="router.push(`/settings/parse-ai-providers/${p.id}`)">
              <PmmIcon name="dashboard" :size="13" /> 监控
            </button>
            <button class="btn btn-ghost btn-sm" :disabled="testingIds.has(p.id)" @click="testConn(p)">
              <PmmIcon :name="testingIds.has(p.id) ? 'refresh' : 'link'" :size="13" :class="{ spin: testingIds.has(p.id) }" />
              {{ testingIds.has(p.id) ? '测试中…' : '测试' }}
            </button>
            <button v-if="circuitState(p)" class="btn btn-ghost btn-sm" @click="unban(p)">
              <PmmIcon name="check" :size="13" /> 解禁
            </button>
            <button
              v-if="canResetQuota(p)"
              class="btn btn-ghost btn-sm"
              :style="quotaExceeded(p) ? 'color: var(--danger)' : ''"
              title="清零已用量并解除套餐超限禁用（新套餐周期开始时用）"
              @click="resetQuota(p)"
            >
              <PmmIcon name="refresh" :size="13" /> 重置用量
            </button>
            <button class="btn btn-ghost btn-sm" @click="openEdit(p)"><PmmIcon name="edit" :size="13" /> 编辑</button>
            <button class="btn btn-ghost btn-sm" style="color: var(--danger)" @click="remove(p)"><PmmIcon name="trash" :size="13" /> 删除</button>
          </div>
        </div>

        <!-- 级间升级条件（最后一级不画，改画兜底终点） -->
        <div v-if="idx < list.length - 1" class="rung-arrow">
          <PmmIcon name="chevronDown" :size="14" />
          <span>
            置信度 &lt; {{ thresholdText(p) }} / 接口异常 / 限流 · 配额 → 升级到下一级
          </span>
        </div>
      </template>

      <div class="rung-arrow terminal">
        <PmmIcon name="chevronDown" :size="14" />
        <span>全部失败 → 转人工复核</span>
      </div>
    </div>

    <div v-if="!list.length && !loading" class="card empty-card">
      <PmmIcon name="brain" :size="36" style="color: var(--text-dim); margin-bottom: 10px" />
      <div class="h2">尚未配置 AI 提供商</div>
      <div class="muted">推荐先添加一个免费档（如本地 Ollama），再加计费档兜底</div>
    </div>

    <el-dialog v-model="dialogVisible" :title="editing ? '编辑提供商' : '新增提供商'" width="560px">
      <div class="vstack" style="--gap: 14px">
        <div v-if="!editing" class="field">
          <div class="field-label">快速预设 <span class="muted small" style="font-weight: 400">点一下自动填协议 / 地址 / 模型，再补 API Key 即可</span></div>
          <div class="preset-chips">
            <button v-for="ps in PRESETS" :key="ps.name" type="button" class="chip" @click="applyPreset(ps)">{{ ps.name }}</button>
          </div>
        </div>
        <div class="field">
          <div class="field-label">名称</div>
          <input class="input" v-model="form.name" autocomplete="off" />
        </div>
        <div class="hstack" style="gap: 12px">
          <div class="field" style="flex: 1">
            <div class="field-label">协议</div>
            <select class="select" v-model="form.type">
              <option v-for="p in PROTOCOLS" :key="p.value" :value="p.value">{{ p.label }}</option>
            </select>
          </div>
          <div class="field" style="flex: 1">
            <div class="field-label">成本档位</div>
            <select class="select" v-model="form.costTier">
              <option value="Free">免费</option>
              <option value="Paid">计费</option>
            </select>
          </div>
        </div>
        <div class="field">
          <div class="field-label">BaseUrl</div>
          <input class="input font-mono" v-model="form.baseUrl" :placeholder="curProto.baseUrl" autocomplete="off" />
          <div class="muted small">{{ curUrlHelp.note }}</div>
          <div class="small url-preview">实际请求 <code class="font-mono">{{ urlPreview }}</code></div>
        </div>
        <div class="field">
          <div class="field-label">{{ form.type === 'AzureOpenAi' ? 'Model（即 Azure deployment 名）' : 'Model' }}</div>
          <input class="input font-mono" v-model="form.model" :placeholder="curProto.model" autocomplete="off" />
        </div>
        <div class="field">
          <div class="field-label">API Key</div>
          <input class="input font-mono" type="password" v-model="form.apiKey" :placeholder="editing ? '留空 = 保持不变' : '需鉴权的服务填写，否则留空'" autocomplete="new-password" />
          <div class="muted small">所有协议均可选填：需鉴权的服务（云端 / 带鉴权反代）填写；填了无效会在「测试连接」时报错。</div>
        </div>
        <div class="hstack" style="gap: 16px">
          <label class="hstack" style="gap: 8px; cursor: pointer" title="JsonMode 时是否下发结构化输出（OpenAI=response_format / Gemini=responseMimeType / Ollama=format:json）；不识别该字段的代理请关闭">
            <div class="switch" :class="{ on: form.structuredJson }" @click="form.structuredJson = !form.structuredJson" />
            <span>结构化输出（JSON）</span>
          </label>
        </div>
        <div class="hstack" style="gap: 12px">
          <div class="field" style="flex: 1">
            <div class="field-label">优先级（越小越先；也可在阶梯上下移）</div>
            <input class="input" type="number" v-model.number="form.priority" min="0" max="9999" placeholder="100" />
          </div>
          <div class="field" style="flex: 1">
            <div class="field-label">满意度阈值（0~1，留空=全局）</div>
            <input class="input" type="number" v-model.number="form.confidenceThreshold" min="0" max="1" step="0.05" placeholder="留空回退全局阈值" />
          </div>
        </div>
        <div class="muted small" style="margin-top: -4px">
          置信度低于阈值视为「结果不满意」，自动升级到下一级；接口异常 / 限流 / 达到上限同样升级。免费档默认阈值更高、且豁免限速（鼓励把握不足就升级到计费档）。
        </div>
        <div class="field">
          <div class="field-label">超时时间（秒，5~600）</div>
          <input class="input" type="number" v-model.number="form.timeoutSeconds" min="5" max="600" placeholder="30" />
          <div class="muted small">单次请求的最长等待（含连接 + 首字节 + 收完整响应）。本地模型（如 Ollama）首次加载较慢，可适当调大，例如 120~300。</div>
        </div>
        <hr class="divider" />
        <div class="field" style="gap: 4px">
          <div class="field-label">套餐限额 <span class="muted small" style="font-weight: 400">按量 / 包期套餐防超支</span></div>
          <div class="muted small">超过任一限额或到期后，该提供商将被自动禁用（剔出升级链），避免转按量计费产生费用。</div>
        </div>
        <div class="hstack" style="gap: 12px">
          <div class="field" style="flex: 1">
            <div class="field-label">调用次数上限</div>
            <input class="input" type="number" v-model.number="form.quotaCallLimit" min="1" step="1" placeholder="留空 = 不限" />
          </div>
          <div class="field" style="flex: 1">
            <div class="field-label">Token 总量上限</div>
            <input class="input" type="number" v-model.number="form.quotaTokenLimit" min="1" step="1" placeholder="留空 = 不限" />
          </div>
        </div>
        <div class="field">
          <div class="field-label">套餐到期日</div>
          <input class="input" type="date" v-model="form.quotaExpiresAt" />
          <div class="muted small">按当地时区当天 23:59:59 生效，到期后自动禁用；留空 = 无期限。</div>
        </div>
        <hr class="divider" />
        <div class="field" style="gap: 4px">
          <div class="field-label">周期滚动额度 <span class="muted small" style="font-weight: 400">按日 / 周 / 月自动重置，用于「每日免费额度」等</span></div>
          <div class="muted small">到周期自然边界（按下方时区）自动清零计数并恢复该提供商，无需手动重置。与上方套餐限额正交，可叠加。</div>
        </div>
        <div class="hstack" style="gap: 12px">
          <div class="field" style="flex: 1">
            <div class="field-label">周期粒度</div>
            <select class="select" v-model="form.quotaPeriod">
              <option value="None">不启用</option>
              <option value="Daily">每日</option>
              <option value="Weekly">每周（周一起）</option>
              <option value="Monthly">每月（1 号起）</option>
            </select>
          </div>
          <div class="field" style="flex: 1">
            <div class="field-label">时区（留空 = 本机）</div>
            <input class="input font-mono" v-model="form.quotaPeriodTimeZone" :disabled="form.quotaPeriod === 'None'" placeholder="如 UTC / Asia/Shanghai" autocomplete="off" />
          </div>
        </div>
        <div v-if="form.quotaPeriod !== 'None'" class="hstack" style="gap: 12px">
          <div class="field" style="flex: 1">
            <div class="field-label">周期内调用次数上限</div>
            <input class="input" type="number" v-model.number="form.quotaPeriodCallLimit" min="1" step="1" placeholder="留空 = 不限" />
          </div>
          <div class="field" style="flex: 1">
            <div class="field-label">周期内 Token 上限</div>
            <input class="input" type="number" v-model.number="form.quotaPeriodTokenLimit" min="1" step="1" placeholder="留空 = 不限" />
          </div>
        </div>
        <hr class="divider" />
        <div class="field">
          <div class="field-label">RPM 限流 <span class="muted small" style="font-weight: 400">每分钟请求数上限（滑动 60 秒窗口）</span></div>
          <input class="input" type="number" v-model.number="form.rpmLimit" min="1" step="1" placeholder="留空 = 不限流" />
          <div class="muted small">达到上限时升级链自动跳过本提供商、改用下一级（不等待），窗口滑出后自动恢复。用于防止短时间打爆第三方每分钟请求限额。</div>
        </div>
        <div class="hstack" style="gap: 16px">
          <label class="hstack" style="gap: 8px; cursor: pointer">
            <div class="switch" :class="{ on: form.enabled }" @click="form.enabled = !form.enabled" />
            <span>启用</span>
          </label>
          <label
            class="hstack"
            :class="{ disabled: !proxyReady }"
            :title="proxyReady ? '通过代理访问此 Provider' : '请先在「设置 → 代理」中启用代理并配置地址'"
            style="gap: 8px; cursor: pointer"
          >
            <div class="switch" :class="{ on: form.useProxy && proxyReady, disabled: !proxyReady }" @click="proxyReady && (form.useProxy = !form.useProxy)" />
            <span>通过代理访问</span>
          </label>
        </div>
      </div>
      <template #footer>
        <button class="btn btn-ghost" @click="dialogVisible = false">取消</button>
        <button class="btn btn-primary" @click="save"><PmmIcon name="check" :size="14" /> 保存</button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped lang="scss">
.ladder {
  display: flex;
  flex-direction: column;
  gap: 0;
  max-width: 860px;
}
.ladder-summary {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12.5px;
  padding: 8px 4px 14px;
  flex-wrap: wrap;
}
.rung {
  display: flex;
  align-items: stretch;
  gap: 14px;
  padding: 14px 16px;
}
.rung.disabled { opacity: 0.62; }
.rung-rank {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  width: 52px;
  flex-shrink: 0;
  border-right: 1px solid var(--border-soft);
  padding-right: 12px;
}
.rung-rank .lvl { font-size: 11px; font-weight: 700; color: var(--accent); white-space: nowrap; }
.reorder { display: flex; flex-direction: column; gap: 3px; }
.reorder .mini {
  width: 24px; height: 18px; line-height: 1; font-size: 10px;
  border: 1px solid var(--border-soft); border-radius: 5px; background: transparent; color: var(--text-dim); cursor: pointer;
}
.reorder .mini:disabled { opacity: 0.3; cursor: not-allowed; }
.rung-body { flex: 1; min-width: 0; }
.rung-head { display: flex; align-items: center; gap: 8px; margin-bottom: 4px; flex-wrap: wrap; }
.rung-head .name { font-size: 15px; font-weight: 700; }
.endpoint { font-size: 11.5px; color: var(--text-dim); margin-top: 3px; word-break: break-all; }
.rung-metrics { display: flex; gap: 16px; margin-top: 8px; font-size: 11.5px; color: var(--text-dim); flex-wrap: wrap; }
.rung-metrics b { color: var(--text); font-weight: 600; }
.rung-quota { margin-top: 8px; display: flex; flex-direction: column; gap: 5px; max-width: 420px; }
.quota-line { display: grid; grid-template-columns: 42px 1fr auto; gap: 8px; align-items: center; font-size: 11.5px; }
.quota-label { color: var(--text-dim); }
.quota-progress { width: 100%; }
.quota-text { color: var(--text-dim); white-space: nowrap; }
.quota-expiry { font-size: 11.5px; color: var(--text-dim); }
.quota-period { font-size: 11.5px; color: var(--text-dim); }
.quota-expiry.expired { color: var(--danger); font-weight: 600; }
.rung-actions { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; justify-content: flex-end; max-width: 280px; }
.rung-arrow {
  display: flex; align-items: center; gap: 6px;
  padding: 6px 0 6px 60px;
  font-size: 11.5px; color: var(--text-dim);
}
.rung-arrow.terminal { color: var(--accent); font-weight: 600; padding-top: 8px; }
.tag-free { background: rgba(34, 160, 90, 0.14); color: #1f9d57; }
.tag-paid { background: rgba(210, 135, 0, 0.14); color: #c47f00; }
.tag-warn { background: rgba(210, 70, 70, 0.14); color: var(--danger); }
.field { display: flex; flex-direction: column; gap: 6px; }
.field-label { font-size: 12.5px; font-weight: 600; color: var(--text); }
.small { font-size: 11.5px; }
.url-preview { color: var(--text-dim); margin-top: 2px; }
.url-preview code { color: var(--accent); word-break: break-all; }
.preset-chips { display: flex; flex-wrap: wrap; gap: 6px; }
.chip {
  font-size: 12px; line-height: 1; padding: 6px 10px; cursor: pointer;
  border: 1px solid var(--border-soft); border-radius: 999px;
  background: transparent; color: var(--text-dim); transition: all 0.12s;
}
.chip:hover { border-color: var(--accent); color: var(--accent); background: rgba(210, 135, 0, 0.08); }
.empty-card { padding: 60px 20px; text-align: center; }
.hstack.disabled { cursor: not-allowed; opacity: 0.5; }
.switch.disabled { cursor: not-allowed; }
.spin { animation: pmm-spin 0.9s linear infinite; }
@keyframes pmm-spin { to { transform: rotate(360deg); } }
</style>
