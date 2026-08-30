<script setup>
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { useRouter } from 'vue-router';
import { ElMessage, ElMessageBox } from 'element-plus';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import { fmtTime, fmtClock, formatQuota } from '@/utils/format';

const props = defineProps({ id: { type: [String, Number], required: true } });
const router = useRouter();

const provider = ref(null);
const stats = ref(null);
const loading = ref(false);

// 统计聚合窗口（与后端 windowHours 对齐，可选 1 / 24 / 168）
const windowHours = ref(24);
const WINDOWS = [
  { h: 1, label: '1 小时' },
  { h: 24, label: '24 小时' },
  { h: 168, label: '7 天' },
];
const windowLabel = computed(() => WINDOWS.find((w) => w.h === windowHours.value)?.label || '24 小时');

// 调用日志（分页 + 过滤 + 原文，独立于统计窗口）
const logs = ref({ items: [], total: 0, page: 1, pageSize: 20 });
const logLoading = ref(false);
const logFilter = ref({ success: null, errorType: null, chainId: null });
const ERROR_TYPES = ['Transient', 'RateLimit', 'Http4xx', 'Http5xx', 'Logical', 'LowConfidence', 'ConfigError', 'Timeout', 'Parse', 'Network'];

// 原文详情对话框
const detail = ref(null);
const detailVisible = ref(false);

let pollTimer = null;

async function loadProvider() {
  const list = await api.parseAiProviders.list().catch(() => null);
  const items = list?.items || list || [];
  provider.value = items.find((p) => String(p.id) === String(props.id)) || items[0] || null;
}

async function loadStats() {
  stats.value = (await api.parseAiProviders.stats(props.id, windowHours.value).catch(() => null)) || null;
}

async function loadLogs() {
  logLoading.value = true;
  try {
    const q = { page: logs.value.page, pageSize: logs.value.pageSize };
    if (logFilter.value.success !== null) q.success = logFilter.value.success;
    if (logFilter.value.errorType) q.errorType = logFilter.value.errorType;
    if (logFilter.value.chainId) q.chainId = logFilter.value.chainId;
    const r = await api.parseAiProviders.logs(props.id, q).catch(() => null);
    if (r) logs.value = { items: r.items || [], total: r.total || 0, page: r.page || 1, pageSize: r.pageSize || 20 };
  } finally {
    logLoading.value = false;
  }
}

async function load() {
  loading.value = true;
  try {
    await Promise.all([loadProvider(), loadStats(), loadLogs()]);
  } finally {
    loading.value = false;
  }
}

function switchWindow(h) {
  if (windowHours.value === h) return;
  windowHours.value = h;
  loadStats();
}

function applyFilter(patch) {
  logFilter.value = { ...logFilter.value, ...patch };
  logs.value.page = 1;
  loadLogs();
}

function setPage(p) {
  logs.value.page = p;
  loadLogs();
}

function openDetail(row) {
  detail.value = row;
  detailVisible.value = true;
}

function viewChain(chainId) {
  if (!chainId) return;
  detailVisible.value = false;
  applyFilter({ chainId });
}

// 24h 调用趋势：后端 hourlyBuckets 倒序（hoursAgo 0..N = 现在..N前）→ 正向时间轴
const callSeries = computed(() => {
  const buckets = stats.value?.hourlyBuckets || [];
  if (!buckets.length) return [];
  return buckets
    .slice()
    .sort((a, b) => b.hoursAgo - a.hoursAgo)
    .map((b, i) => ({ hour: i, total: b.total, failed: b.failed }));
});

const ERROR_COLOR_MAP = {
  Timeout: 'accent', Http4xx: 'warning', Http5xx: 'danger', RateLimit: 'warning',
  Transient: 'accent', Logical: 'warning', LowConfidence: 'info', ConfigError: 'danger',
  Parse: 'info', Network: 'danger', Unknown: 'neutral',
};
const LATENCY_COLOR_MAP = {
  '0-500ms': 'success', '500ms-1s': 'success',
  '1-2s': 'warning', '2-3s': 'warning',
  '3-5s': 'danger', '>5s': 'danger',
};

const histogram = computed(() => {
  const buckets = stats.value?.latencyHistogram || [];
  return buckets.map((b) => ({ label: b.label, count: b.count, color: LATENCY_COLOR_MAP[b.label] || 'info' }));
});
const histMax = computed(() => Math.max(...histogram.value.map((b) => b.count), 1));

const errorBreakdown = computed(() => {
  const buckets = stats.value?.errorBreakdown || [];
  return buckets.map((b) => ({ label: b.errorType, count: b.count, color: ERROR_COLOR_MAP[b.errorType] || 'neutral' }));
});
const errorTotal = computed(() => errorBreakdown.value.reduce((a, b) => a + b.count, 0));

const errorSegments = computed(() => {
  let cum = 0;
  const out = [];
  for (const b of errorBreakdown.value) {
    const start = cum / Math.max(1, errorTotal.value);
    cum += b.count;
    const end = cum / Math.max(1, errorTotal.value);
    const a1 = -Math.PI / 2 + start * 2 * Math.PI;
    const a2 = -Math.PI / 2 + end * 2 * Math.PI;
    const r = 44;
    const x1 = 60 + r * Math.cos(a1);
    const y1 = 60 + r * Math.sin(a1);
    const x2 = 60 + r * Math.cos(a2);
    const y2 = 60 + r * Math.sin(a2);
    const large = end - start > 0.5 ? 1 : 0;
    out.push({ ...b, d: `M ${x1} ${y1} A ${r} ${r} 0 ${large} 1 ${x2} ${y2}`, pct: Math.round((b.count / errorTotal.value) * 100) });
  }
  return out;
});

// 按模型分布（成本视角）
const modelBreakdown = computed(() => stats.value?.modelBreakdown || []);

const callSeriesMax = computed(() => Math.max(...callSeries.value.map((s) => s.total), 1) * 1.15);

const linePoints = computed(() => {
  const W = 600, H = 200, padL = 32, padR = 12, padT = 16, padB = 24;
  const iw = W - padL - padR;
  const ih = H - padT - padB;
  const data = callSeries.value;
  if (!data.length) return { total: '', failed: '', area: '', W, H };
  const path = (key) =>
    data
      .map((s, i) => {
        const x = padL + (i / Math.max(1, data.length - 1)) * iw;
        const y = padT + ih - (s[key] / callSeriesMax.value) * ih;
        return (i === 0 ? 'M' : 'L') + ' ' + x.toFixed(1) + ' ' + y.toFixed(1);
      })
      .join(' ');
  const totalPath = path('total');
  return {
    total: totalPath,
    failed: path('failed'),
    area: totalPath + ` L ${padL + iw} ${padT + ih} L ${padL} ${padT + ih} Z`,
    W, H, padL, padR, padT, padB, iw, ih,
  };
});

const successRate = computed(() => {
  const t = stats.value?.totalCalls || 0;
  if (t === 0) return 0;
  return Math.round((stats.value.successCount / t) * 100);
});
const avgLatencyDisplay = computed(() => {
  const v = stats.value?.avgLatencyMs;
  return v ? Math.round(v) : '—';
});
const p95LatencyDisplay = computed(() => {
  const v = stats.value?.p95LatencyMs;
  return v ? v : '—';
});
const tokenTotal = computed(() => (stats.value?.totalPromptTokens || 0) + (stats.value?.totalCompletionTokens || 0));
const avgConfidenceDisplay = computed(() => {
  const v = stats.value?.avgConfidence;
  return v != null ? Math.round(v * 100) + '%' : '—';
});

const disableHistory = computed(() => {
  const dUntil = provider.value?.disabledUntil;
  if (!dUntil) return [];
  return [{ kind: 'auto', ts: dUntil, reason: '自动禁用至该时间点（健康追踪触发）', durationMin: null }];
});

// ── 套餐配额（provider 行数据随初始加载 / 启停刷新，节奏对齐现状） ──
/** 配置了任一限额或到期日才显示配额卡片 */
const quotaConfigured = computed(() => {
  const p = provider.value;
  return !!p && (p.quotaCallLimit != null || p.quotaTokenLimit != null || !!p.quotaExpiresAt);
});
/** 套餐超限（后端写标记自动禁用） */
const quotaExceeded = computed(() => !!provider.value?.quotaExceededAt);
/** 套餐已到期（纯时间判断） */
const quotaExpired = computed(() => {
  const at = provider.value?.quotaExpiresAt;
  if (!at) return false;
  const t = new Date(at).getTime();
  return !Number.isNaN(t) && t <= Date.now();
});
/** 配额进度条：已用/上限 + 百分比；≥80% 警示色、≥100% 危险色 */
const quotaBars = computed(() => {
  const p = provider.value;
  if (!p) return [];
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
  if (p.quotaCallLimit != null) push('调用次数', p.quotaUsedCalls, p.quotaCallLimit, (v) => Number(v).toLocaleString());
  if (p.quotaTokenLimit != null) push('Token 总量', p.quotaUsedTokens, p.quotaTokenLimit, formatQuota);
  return bars;
});
/** 套餐到期日：「套餐至 YYYY-MM-DD」（本地时区） */
const quotaExpiryText = computed(() => {
  const at = provider.value?.quotaExpiresAt;
  if (!at) return '';
  const d = new Date(at);
  if (Number.isNaN(d.getTime())) return '';
  return `套餐至 ${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
});

const maxPage = computed(() => Math.max(1, Math.ceil((logs.value.total || 0) / (logs.value.pageSize || 20))));

const testing = ref(false);
const toggling = ref(false);

async function handleTest() {
  if (!provider.value || testing.value) return;
  testing.value = true;
  try {
    const r = await api.parseAiProviders.test(provider.value.id);
    if (r?.success) ElMessage.success(`连接成功（${Math.round(r.elapsedMilliseconds)}ms）`);
    else ElMessage.error(`连接失败：${r?.errorMessage || 'HTTP ' + (r?.httpStatus ?? '—')}`);
  } catch (e) {
    ElMessage.error(`测试异常：${e?.message || e}`);
  } finally {
    testing.value = false;
  }
}

function handleEdit() {
  router.push('/settings/parse-ai-providers');
}

async function handleToggleEnabled() {
  if (!provider.value || toggling.value) return;
  const p = provider.value;
  const next = !p.enabled;
  try {
    await ElMessageBox.confirm(`确认${next ? '启用' : '禁用'}提供商 "${p.name}"？`, '提示', { type: 'warning' });
  } catch {
    return;
  }
  toggling.value = true;
  try {
    // Update 为全量语义：缺省字段会被后端默认值覆盖，必须原样回传全部字段
    await api.parseAiProviders.update(p.id, {
      name: p.name, type: p.type, baseUrl: p.baseUrl, apiKey: null, model: p.model,
      isPrimary: p.isPrimary, priority: p.priority, enabled: next,
      timeoutSeconds: p.timeoutSeconds, extraOptions: p.extraOptions,
      useProxy: p.useProxy ?? false,
      costTier: p.costTier ?? 'Paid',
      structuredJson: p.structuredJson ?? true,
      confidenceThreshold: p.confidenceThreshold ?? null,
      quotaCallLimit: p.quotaCallLimit ?? null,
      quotaTokenLimit: p.quotaTokenLimit ?? null,
      quotaExpiresAt: p.quotaExpiresAt ?? null,
    });
    if (next) await api.parseAiProviders.enable(p.id);
    ElMessage.success(next ? '已启用' : '已禁用');
    await load();
  } catch (e) {
    ElMessage.error(`切换失败：${e?.message || e}`);
  } finally {
    toggling.value = false;
  }
}

onMounted(() => {
  load();
  // 停留轮询：每 10s 刷新统计 + 当前页日志（页面隐藏 / 正在看原文时跳过，避免打断）
  pollTimer = setInterval(() => {
    if (typeof document !== 'undefined' && document.hidden) return;
    loadStats();
    if (!detailVisible.value) loadLogs();
  }, 10000);
});
onUnmounted(() => { if (pollTimer) clearInterval(pollTimer); });
</script>

<template>
  <div class="page" v-loading="loading">
    <div class="back-bar">
      <button class="btn btn-ghost btn-sm" @click="router.push('/settings/parse-ai-providers')">
        <PmmIcon name="chevronLeft" :size="14" /> 返回提供商列表
      </button>
      <div class="win-switch">
        <button
          v-for="w in WINDOWS"
          :key="w.h"
          class="win-btn"
          :class="{ active: windowHours === w.h }"
          @click="switchWindow(w.h)"
        >{{ w.label }}</button>
      </div>
    </div>

    <!-- Hero -->
    <div class="card hero" v-if="provider">
      <div class="hero-bg" />
      <div class="hero-row">
        <div class="hero-icon"><PmmIcon name="brain" :size="28" /></div>
        <div class="hero-meta">
          <div class="hero-title">
            <h1 class="h1" style="font-size: 24px">{{ provider.name }}</h1>
            <span v-if="provider.isPrimary" class="tag tag-accent">主</span>
            <span v-if="provider.enabled" class="tag tag-success"><span class="tag-dot" />活跃</span>
            <span v-else class="tag tag-neutral">已禁用</span>
          </div>
          <div class="muted" style="font-size: 13px">{{ provider.type }} · 模型 <span class="font-mono">{{ provider.model || '—' }}</span></div>
          <div class="font-mono" style="font-size: 12px; color: var(--text-dim); margin-top: 6px">{{ provider.baseUrl }}</div>
        </div>
        <div class="hero-actions">
          <button class="btn btn-ghost btn-sm" :disabled="testing" @click="handleTest">
            <PmmIcon name="link" :size="14" /> {{ testing ? '测试中…' : '测试连接' }}
          </button>
          <button class="btn btn-ghost btn-sm" @click="handleEdit">
            <PmmIcon name="edit" :size="14" /> 编辑配置
          </button>
          <button class="btn btn-sm" :class="provider.enabled ? 'btn-danger' : 'btn-primary'" :disabled="toggling" @click="handleToggleEnabled">
            <PmmIcon :name="provider.enabled ? 'ban' : 'play'" :size="14" />
            {{ provider.enabled ? '禁用' : '启用' }}
          </button>
        </div>
      </div>
    </div>

    <!-- Stats（随窗口刷新） -->
    <div class="stats-row" v-if="provider">
      <div class="card stat-big">
        <PmmIcon name="brain" :size="16" class="stat-icon" />
        <div class="eyebrow xs">{{ windowLabel }}调用</div>
        <div class="font-display tabular val">{{ (stats?.totalCalls || 0).toLocaleString() }}</div>
        <div class="hint">基于 Audit_AiCall 表</div>
      </div>
      <div class="card stat-big">
        <PmmIcon name="check" :size="16" class="stat-icon" :style="{ color: 'var(--success)' }" />
        <div class="eyebrow xs">成功率</div>
        <div class="font-display tabular val" :style="{ color: successRate >= 95 ? 'var(--success)' : successRate >= 85 ? 'var(--warning)' : 'var(--danger)' }">
          {{ successRate }}%
        </div>
        <div class="hint">{{ stats?.successCount || 0 }} / {{ stats?.totalCalls || 0 }}</div>
      </div>
      <div class="card stat-big">
        <PmmIcon name="info" :size="16" class="stat-icon" />
        <div class="eyebrow xs">平均延迟</div>
        <div class="font-display tabular val">{{ avgLatencyDisplay }}{{ avgLatencyDisplay === '—' ? '' : 'ms' }}</div>
        <div class="hint">仅成功调用计入</div>
      </div>
      <div class="card stat-big">
        <PmmIcon name="warning" :size="16" class="stat-icon" />
        <div class="eyebrow xs">P95 延迟</div>
        <div class="font-display tabular val">{{ p95LatencyDisplay }}{{ p95LatencyDisplay === '—' ? '' : 'ms' }}</div>
        <div class="hint">95% 成功调用低于此值</div>
      </div>
      <div class="card stat-big">
        <PmmIcon name="layers" :size="16" class="stat-icon" />
        <div class="eyebrow xs">Token 用量</div>
        <div class="font-display tabular val">{{ tokenTotal.toLocaleString() }}</div>
        <div class="hint">入 {{ (stats?.totalPromptTokens || 0).toLocaleString() }} · 出 {{ (stats?.totalCompletionTokens || 0).toLocaleString() }}</div>
      </div>
      <div class="card stat-big">
        <PmmIcon name="shield" :size="16" class="stat-icon" />
        <div class="eyebrow xs">平均置信度</div>
        <div class="font-display tabular val">{{ avgConfidenceDisplay }}</div>
        <div class="hint">含低置信升级行</div>
      </div>
    </div>

    <!-- 趋势 + 错误分布 -->
    <div class="row-trend">
      <section class="card">
        <header class="card-head row-head">
          <div>
            <h3 class="h3">{{ windowLabel }}调用趋势</h3>
            <div class="muted small">每小时调用次数 · 成功 / 失败</div>
          </div>
          <div class="legend">
            <span><span class="leg-dot" style="background: var(--success)" /> 成功</span>
            <span><span class="leg-dot" style="background: var(--danger)" /> 失败</span>
          </div>
        </header>
        <div class="card-body">
          <svg :viewBox="`0 0 ${linePoints.W} ${linePoints.H}`" class="trend-svg">
            <line
              v-for="(p, i) in [0, 0.25, 0.5, 0.75, 1]"
              :key="i"
              :x1="linePoints.padL"
              :x2="linePoints.padL + linePoints.iw"
              :y1="linePoints.padT + linePoints.ih * p"
              :y2="linePoints.padT + linePoints.ih * p"
              stroke="var(--border-soft)"
              stroke-width="1"
            />
            <path :d="linePoints.area" fill="var(--success-soft)" opacity="0.6" />
            <path :d="linePoints.total" fill="none" stroke="var(--success)" stroke-width="2.2" />
            <path :d="linePoints.failed" fill="none" stroke="var(--danger)" stroke-width="1.8" />
          </svg>
        </div>
      </section>

      <section class="card">
        <header class="card-head"><h3 class="h3">错误类型分布</h3></header>
        <div class="card-body donut-body">
          <svg viewBox="0 0 120 120" class="err-donut">
            <circle cx="60" cy="60" r="44" fill="none" stroke="var(--surface-3)" stroke-width="14" />
            <path v-for="(seg, i) in errorSegments" :key="i" :d="seg.d" fill="none" :stroke="`var(--${seg.color})`" stroke-width="14" />
            <text x="60" y="58" text-anchor="middle" font-size="20" font-weight="700" font-family="Outfit" fill="var(--text)">{{ errorTotal }}</text>
            <text x="60" y="73" text-anchor="middle" font-size="9" fill="var(--text-dim)">失败数</text>
          </svg>
          <div class="err-legend">
            <div v-if="!errorSegments.length" class="muted small">窗口内无失败</div>
            <div v-for="seg in errorSegments" :key="seg.label" class="err-row clickable" @click="applyFilter({ success: false, errorType: seg.label })">
              <span class="leg-dot" :style="{ background: `var(--${seg.color})` }" />
              <span>{{ seg.label }}</span>
              <span class="muted tabular">{{ seg.pct }}%</span>
              <span class="tabular" style="font-weight: 600">{{ seg.count }}</span>
            </div>
          </div>
        </div>
      </section>
    </div>

    <!-- 延迟直方图 + 按模型分布 -->
    <div class="row-2">
      <section class="card">
        <header class="card-head"><h3 class="h3">延迟分布</h3></header>
        <div class="card-body">
          <div class="hist-bars">
            <div v-for="(b, i) in histogram" :key="i" class="hist-col">
              <div class="muted tabular xs">{{ b.count }}</div>
              <div class="hist-bar" :style="{ height: (b.count / histMax) * 110 + 'px', background: `var(--${b.color})` }" />
            </div>
          </div>
          <div class="hist-labels">
            <div v-for="b in histogram" :key="b.label" class="font-mono xs muted">{{ b.label }}</div>
          </div>
          <div class="hist-foot muted xs">毫秒</div>
        </div>
      </section>

      <section class="card">
        <header class="card-head row-head">
          <h3 class="h3">按模型分布</h3>
          <span class="muted small">成本视角 · Token 合计</span>
        </header>
        <div v-if="!modelBreakdown.length" class="empty" style="padding: 24px"><div class="small">窗口内无调用记录</div></div>
        <table v-else class="table">
          <thead>
            <tr><th>模型</th><th class="tar">调用</th><th class="tar">Token</th></tr>
          </thead>
          <tbody>
            <tr v-for="m in modelBreakdown" :key="m.model">
              <td class="font-mono" style="font-size: 12px">{{ m.model }}</td>
              <td class="tabular tar">{{ m.count.toLocaleString() }}</td>
              <td class="tabular tar">{{ (m.totalTokens || 0).toLocaleString() }}</td>
            </tr>
          </tbody>
        </table>
      </section>
    </div>

    <!-- 套餐配额（仅配置了限额 / 到期日时显示；累计用量，非窗口统计） -->
    <section v-if="quotaConfigured" class="card status-card">
      <header class="card-head row-head">
        <div>
          <h3 class="h3">套餐配额</h3>
          <div class="muted small">累计用量 · 超限或到期将自动禁用（剔出升级链）</div>
        </div>
        <span v-if="quotaExceeded" class="tag tag-danger">套餐超限 · 已自动禁用</span>
        <span v-else-if="quotaExpired" class="tag tag-warn">套餐已到期 · 已自动禁用</span>
      </header>
      <div class="quota-body">
        <div v-for="bar in quotaBars" :key="bar.label" class="quota-line">
          <span class="quota-label">{{ bar.label }}</span>
          <div class="progress quota-progress">
            <div class="progress-bar" :style="{ width: bar.width + '%', background: bar.color }" />
          </div>
          <span class="quota-text tabular">{{ bar.text }} · {{ bar.pct }}%</span>
        </div>
        <div v-if="quotaExpiryText" class="quota-expiry" :class="{ expired: quotaExpired }">
          {{ quotaExpiryText }}{{ quotaExpired ? '（已到期）' : '' }}
        </div>
        <div v-if="quotaExceeded" class="muted small">解除方式：在提供商编辑页调高限额，或在列表页「重置用量」清零后自动恢复。</div>
      </div>
    </section>

    <!-- 当前禁用状态 -->
    <section class="card status-card">
      <header class="card-head row-head">
        <h3 class="h3">当前禁用状态</h3>
        <span class="muted small">基于 disabledUntil 字段</span>
      </header>
      <div v-if="!disableHistory.length" class="empty" style="padding: 18px"><div class="small">当前未处于自动禁用窗口</div></div>
      <div v-else style="padding: 12px 0">
        <div v-for="(it, i) in disableHistory" :key="i" class="dh-row">
          <div class="dh-icon tone-danger"><PmmIcon name="ban" :size="13" /></div>
          <div class="dh-meta">
            <div class="dh-name">自动禁用</div>
            <div class="muted small">{{ it.reason }}</div>
          </div>
          <span class="muted tabular xs">{{ fmtTime(it.ts) }}</span>
        </div>
      </div>
    </section>

    <!-- 调用日志（分页 + 过滤 + 原文） -->
    <section class="card" v-loading="logLoading">
      <header class="card-head row-head">
        <div>
          <h3 class="h3">调用日志</h3>
          <div class="muted small">分页浏览 Audit_AiCall · 点击行看请求/响应原文</div>
        </div>
        <div class="filter-bar">
          <div class="seg">
            <button class="seg-btn" :class="{ active: logFilter.success === null }" @click="applyFilter({ success: null })">全部</button>
            <button class="seg-btn" :class="{ active: logFilter.success === true }" @click="applyFilter({ success: true })">成功</button>
            <button class="seg-btn" :class="{ active: logFilter.success === false }" @click="applyFilter({ success: false })">失败</button>
          </div>
          <select class="sel" :value="logFilter.errorType || ''" @change="applyFilter({ errorType: $event.target.value || null })">
            <option value="">全部错误类型</option>
            <option v-for="t in ERROR_TYPES" :key="t" :value="t">{{ t }}</option>
          </select>
        </div>
      </header>

      <div v-if="logFilter.chainId" class="chain-banner">
        <PmmIcon name="link" :size="13" />
        正在查看升级链 <span class="font-mono">{{ logFilter.chainId.slice(0, 12) }}…</span>
        <button class="btn btn-ghost btn-sm" @click="applyFilter({ chainId: null })">清除</button>
      </div>

      <table class="table">
        <thead>
          <tr>
            <th>时间</th>
            <th>媒体</th>
            <th>状态</th>
            <th>级/主</th>
            <th>模型</th>
            <th class="tar">延迟</th>
            <th class="tar">Token</th>
            <th class="tar">置信度</th>
            <th>错误</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="c in logs.items" :key="c.id" class="log-row" @click="openDetail(c)">
            <td class="font-mono muted" style="font-size: 12px">{{ fmtClock(c.timestamp) }}</td>
            <td>
              <a v-if="c.mediaItemId" class="cell-strong" style="color: var(--accent)" @click.stop="router.push(`/media/${c.mediaItemId}`)">#{{ c.mediaItemId }}</a>
              <span v-else class="muted">—</span>
            </td>
            <td>
              <span v-if="c.success" class="tag tag-success"><span class="tag-dot" />成功</span>
              <span v-else class="tag tag-danger"><span class="tag-dot" />失败</span>
            </td>
            <td class="tabular">
              L{{ c.attemptLevel || 0 }}<span v-if="c.isPrimary" class="tag tag-accent mini">主</span>
            </td>
            <td class="font-mono" style="font-size: 12px">{{ c.model || '—' }}</td>
            <td class="tabular tar">{{ c.latencyMs }}ms</td>
            <td class="tabular tar">{{ c.promptTokens != null || c.completionTokens != null ? ((c.promptTokens || 0) + (c.completionTokens || 0)).toLocaleString() : '—' }}</td>
            <td class="tabular tar">{{ c.confidence != null ? Math.round(c.confidence * 100) + '%' : '—' }}</td>
            <td class="muted small">{{ c.errorType || '—' }}</td>
          </tr>
          <tr v-if="!logs.items.length && !logLoading">
            <td colspan="9" class="empty">暂无调用记录</td>
          </tr>
        </tbody>
      </table>

      <el-pagination
        class="pagination"
        :current-page="logs.page"
        :page-size="logs.pageSize"
        :total="logs.total"
        :page-sizes="[20, 50, 100]"
        layout="total, sizes, prev, pager, next"
        @update:current-page="(v) => setPage(v)"
        @update:page-size="(v) => { logs.pageSize = v; logs.page = 1; loadLogs(); }"
      />
    </section>

    <!-- 原文详情对话框 -->
    <el-dialog v-model="detailVisible" title="调用详情 / 原文" width="680px" append-to-body>
      <div v-if="detail" class="detail">
        <div class="dl">
          <div class="dl-item"><span class="dl-k">时间</span><span class="dl-v font-mono">{{ fmtClock(detail.timestamp) }}</span></div>
          <div class="dl-item"><span class="dl-k">状态</span><span class="dl-v">
            <span v-if="detail.success" class="tag tag-success"><span class="tag-dot" />成功</span>
            <span v-else class="tag tag-danger"><span class="tag-dot" />失败</span>
          </span></div>
          <div class="dl-item"><span class="dl-k">级序 / 主</span><span class="dl-v">L{{ detail.attemptLevel || 0 }} {{ detail.isPrimary ? '· 主' : '' }}</span></div>
          <div class="dl-item"><span class="dl-k">模型</span><span class="dl-v font-mono">{{ detail.model || '—' }}</span></div>
          <div class="dl-item"><span class="dl-k">延迟</span><span class="dl-v tabular">{{ detail.latencyMs }}ms</span></div>
          <div class="dl-item"><span class="dl-k">HTTP</span><span class="dl-v tabular">{{ detail.httpStatus ?? '—' }}</span></div>
          <div class="dl-item"><span class="dl-k">Token（入/出）</span><span class="dl-v tabular">{{ detail.promptTokens ?? '—' }} / {{ detail.completionTokens ?? '—' }}</span></div>
          <div class="dl-item"><span class="dl-k">置信度</span><span class="dl-v tabular">{{ detail.confidence != null ? Math.round(detail.confidence * 100) + '%' : '—' }}</span></div>
          <div class="dl-item"><span class="dl-k">媒体记录</span><span class="dl-v">
            <a v-if="detail.mediaItemId" style="color: var(--accent); cursor: pointer" @click="router.push(`/media/${detail.mediaItemId}`)">#{{ detail.mediaItemId }}</a>
            <span v-else class="muted">—</span>
          </span></div>
          <div class="dl-item"><span class="dl-k">升级链</span><span class="dl-v">
            <span v-if="detail.chainId" class="font-mono" style="font-size: 12px">{{ detail.chainId.slice(0, 12) }}…</span>
            <button v-if="detail.chainId" class="btn btn-ghost btn-sm" style="margin-left: 6px" @click="viewChain(detail.chainId)">查看本链</button>
            <span v-else class="muted">—</span>
          </span></div>
        </div>

        <div v-if="detail.errorType || detail.errorDetail" class="err-block">
          <div class="blk-title">错误：<span class="tag tag-danger mini">{{ detail.errorType || 'Unknown' }}</span></div>
          <pre class="raw">{{ detail.errorDetail || '（无详情）' }}</pre>
        </div>

        <div class="blk-title">请求原文</div>
        <pre class="raw">{{ detail.requestText || '（未记录）' }}</pre>

        <div class="blk-title">响应原文</div>
        <pre class="raw">{{ detail.responseText || '（未记录）' }}</pre>
      </div>
    </el-dialog>
  </div>
</template>

<style scoped lang="scss">
.back-bar { margin-bottom: 14px; display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.win-switch { display: inline-flex; gap: 2px; background: var(--surface-3); padding: 3px; border-radius: 9px; }
.win-btn {
  border: 0; background: transparent; color: var(--text-dim);
  font-size: 12px; padding: 5px 12px; border-radius: 7px; cursor: pointer;
}
.win-btn.active { background: var(--surface-1); color: var(--text); box-shadow: 0 1px 2px rgba(0,0,0,.08); }

.hero { position: relative; overflow: hidden; padding: 24px; margin-bottom: 18px; }
.hero-bg { position: absolute; inset: 0; background: radial-gradient(circle at 90% 0%, var(--accent-soft), transparent 50%); pointer-events: none; }
.hero-row { position: relative; display: flex; align-items: flex-start; gap: 18px; }
.hero-icon { width: 56px; height: 56px; border-radius: 14px; background: var(--accent-soft); color: var(--accent); display: grid; place-items: center; flex-shrink: 0; }
.hero-meta { flex: 1; }
.hero-title { display: flex; align-items: center; gap: 10px; margin-bottom: 6px; }
.hero-actions { display: flex; gap: 8px; }

.stats-row { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 14px; margin-bottom: 20px; }
.stat-big { padding: 16px 18px; position: relative; overflow: hidden; }
.stat-icon { position: absolute; top: 14px; right: 14px; color: var(--text-mute); }
.stat-big .val { font-size: 28px; margin-top: 6px; line-height: 1.05; }
.stat-big .hint { font-size: 11px; color: var(--text-dim); margin-top: 4px; }

.row-trend { display: grid; grid-template-columns: 1.6fr 1fr; gap: 20px; margin-bottom: 20px; }
.row-head { justify-content: space-between; align-items: flex-start; }
.legend { display: flex; gap: 12px; font-size: 11px; }
.leg-dot { display: inline-block; width: 8px; height: 2px; vertical-align: middle; margin-right: 4px; background: var(--text-mute); }
.donut-body { display: flex; gap: 18px; align-items: center; }
.err-donut { width: 120px; height: 120px; flex-shrink: 0; }
.err-legend { flex: 1; display: flex; flex-direction: column; gap: 6px; }
.err-row { display: grid; grid-template-columns: 10px 1fr auto auto; gap: 8px; align-items: center; font-size: 12px; }
.err-row.clickable { cursor: pointer; padding: 2px 4px; border-radius: 6px; }
.err-row.clickable:hover { background: var(--surface-3); }
.trend-svg { width: 100%; height: auto; }

.row-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 20px; margin-bottom: 20px; }
.hist-bars { display: grid; grid-template-columns: repeat(6, 1fr); gap: 6px; align-items: end; height: 140px; }
.hist-col { display: flex; flex-direction: column; align-items: center; gap: 6px; }
.hist-bar { width: 80%; max-width: 28px; border-radius: 3px 3px 0 0; min-height: 2px; }
.hist-labels { display: grid; grid-template-columns: repeat(6, 1fr); gap: 6px; margin-top: 6px; text-align: center; }
.hist-foot { text-align: center; margin-top: 8px; }

.status-card { margin-bottom: 20px; }
.quota-body { padding: 14px 18px; display: flex; flex-direction: column; gap: 8px; max-width: 560px; }
.quota-line { display: grid; grid-template-columns: 64px 1fr auto; gap: 10px; align-items: center; font-size: 12px; }
.quota-label { color: var(--text-dim); }
.quota-progress { width: 100%; }
.quota-text { color: var(--text-dim); white-space: nowrap; }
.quota-expiry { font-size: 12px; color: var(--text-dim); }
.quota-expiry.expired { color: var(--danger); font-weight: 600; }
.dh-row { padding: 10px 18px; border-bottom: 1px solid var(--border-soft); display: grid; grid-template-columns: auto 1fr auto; gap: 12px; align-items: center; }
.dh-icon { width: 26px; height: 26px; border-radius: 999px; display: grid; place-items: center; }
.dh-icon.tone-danger { background: var(--danger-soft); color: var(--danger); }
.dh-name { font-size: 13px; font-weight: 600; }

.filter-bar { display: flex; gap: 10px; align-items: center; }
.seg { display: inline-flex; gap: 2px; background: var(--surface-3); padding: 3px; border-radius: 8px; }
.seg-btn { border: 0; background: transparent; color: var(--text-dim); font-size: 12px; padding: 4px 10px; border-radius: 6px; cursor: pointer; }
.seg-btn.active { background: var(--surface-1); color: var(--text); }
.sel { font-size: 12px; padding: 5px 8px; border-radius: 8px; border: 1px solid var(--border-soft); background: var(--surface-1); color: var(--text); }

.chain-banner { display: flex; align-items: center; gap: 8px; margin: 0 0 10px; padding: 8px 12px; border-radius: 8px; background: var(--accent-soft); color: var(--accent); font-size: 12px; }
.chain-banner .btn { margin-left: auto; }

.log-row { cursor: pointer; }
.log-row:hover { background: var(--surface-3); }
.tar { text-align: right; }
.tag.mini { padding: 0 5px; font-size: 10px; margin-left: 4px; }
.pagination { margin-top: 12px; justify-content: flex-end; }

.detail .dl { display: grid; grid-template-columns: 1fr 1fr; gap: 8px 18px; margin-bottom: 14px; }
.dl-item { display: flex; justify-content: space-between; gap: 10px; font-size: 13px; padding: 4px 0; border-bottom: 1px dashed var(--border-soft); }
.dl-k { color: var(--text-dim); }
.blk-title { font-size: 12px; font-weight: 600; color: var(--text-dim); margin: 12px 0 6px; }
.raw { background: var(--surface-3); border-radius: 8px; padding: 10px 12px; font-size: 12px; font-family: 'JetBrains Mono', monospace; white-space: pre-wrap; word-break: break-all; max-height: 220px; overflow: auto; margin: 0; }
.err-block { margin-bottom: 6px; }

.small { font-size: 11.5px; }
.xs { font-size: 10.5px; }
</style>
