<script setup>
import { computed, onMounted, ref } from 'vue';
import { useRouter } from 'vue-router';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPoster from '@/components/PmmPoster.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';
import { useScanActions } from '@/composables/useScanActions';
import { timeAgo, fmtClock, fmtUptime, fmtSize } from '@/utils/format';

const router = useRouter();
const loading = ref(false);
const stats = ref(null);
const recents = ref([]);
const watchFolders = ref([]);
const taskRuns = ref([]);
const healthData = ref([]);
const aiProviders = ref([]);
const systemInfo = ref(null);

const heatmapData = ref({ from: null, to: null, grid: [] });
const folderActivity = ref([]);

async function load() {
  loading.value = true;
  try {
    const [s, h, w, t, hc, hm, fa, providers, sys] = await Promise.all([
      api.dashboard.stats(),
      api.history.list({ page: 1, pageSize: 12 }),
      api.watchFolders.list(),
      api.dashboard.tasks({ windowHours: 24, limit: 50 }),
      api.dashboard.health(),
      api.dashboard.heatmap({}),
      api.dashboard.watchFolderActivity(),
      // AI 提供商 / 系统信息为附属面板：单独 catch 降级，避免拖垮主聚合（与 Proxy.vue 同范式）
      api.parseAiProviders.list().catch(() => []),
      api.system.info().catch(() => null),
    ]);
    stats.value = s || {};
    recents.value = (h?.items || []).slice(0, 12);
    // api.watchFolders.list() 返回直接数组（无 Items 包装）；history.list / dashboard.tasks 是真分页结构（h?.items / t?.items 保留）
    watchFolders.value = (w || []).map((f) => ({
      ...f,
      count: f.mediaCount ?? 0,
      lastScan: f.lastScanAt || null,
      status: f.enabled ? 'ok' : 'paused',
    }));
    healthData.value = (hc?.checks || []).map((c) => ({
      name: c.name,
      status: c.status,
      detail: c.detail || '—',
    }));
    aiProviders.value = providers?.items || providers || [];
    systemInfo.value = sys;
    taskRuns.value = (t?.items || []).map((r) => ({
      ts: r.startedAt,
      // JobKey 形如 "scan.full-scan" → 展示用中文标签
      kind: JOB_LABELS[r.jobKey] || r.jobKey,
      target: r.detailJson ? safeShortenDetail(r.detailJson) : '—',
      duration: r.durationMs != null ? (r.durationMs >= 1000 ? (r.durationMs / 1000).toFixed(1) + 's' : r.durationMs + 'ms') : null,
      processed: r.processedCount,
      status: OUTCOME_TO_STATUS[r.outcome] || 'neutral',
      note: r.errorMessage,
    }));
    heatmapData.value = hm || { from: null, to: null, grid: [] };
    folderActivity.value = fa?.items || [];
  } finally {
    loading.value = false;
  }
}

const JOB_LABELS = {
  'scan.full-scan': '定时扫描',
  'webhook.webhook-retry': 'Webhook 重试',
  'maintenance.log-retention': '日志清理',
};

const OUTCOME_TO_STATUS = {
  Succeeded: 'done',
  Failed: 'fail',
  Canceled: 'neutral',
  // r1 P2-3：Running 单独走 running 分支以渲染旋转 spinner，避免与 Canceled 混在 neutral 灰圈
  Running: 'running',
};

function safeShortenDetail(json) {
  try {
    const obj = JSON.parse(json);
    return obj.target || obj.note || JSON.stringify(obj).slice(0, 60);
  } catch {
    return json.slice(0, 60);
  }
}

// 7d × 24h 活动热图（真实数据：来自后端 GET /dashboard/heatmap，按 MediaItem.ArchivedAt 桶聚合）
// 后端 grid 行约定 .NET DayOfWeek：0=Sun..6=Sat；前端把周日从首位调到末尾以贴合中文「周一~周日」顺序
const days = ['周一', '周二', '周三', '周四', '周五', '周六', '周日'];
const heatmap = computed(() => {
  const raw = heatmapData.value?.grid;
  if (!Array.isArray(raw) || raw.length < 7) {
    // 后端空响应或首次未加载完：返回 7×24 全 0 网格避免模板炸
    return Array.from({ length: 7 }, () => new Array(24).fill(0));
  }
  // 后端顺序：[Sun, Mon, Tue, Wed, Thu, Fri, Sat]
  // 前端展示顺序：周一..周日，所以重排
  const [sun, mon, tue, wed, thu, fri, sat] = raw;
  return [mon, tue, wed, thu, fri, sat, sun].map((row) => Array.isArray(row) ? row : new Array(24).fill(0));
});
const heatmapMax = computed(() => Math.max(...heatmap.value.flat(), 1));

// 热图洞察（真实计算，替代旧写死文案）：最热单元格 + 7 天交付总量
const heatmapStats = computed(() => {
  const g = heatmap.value;
  let peak = { d: 0, h: 0, v: 0 };
  let sum = 0;
  for (let d = 0; d < g.length; d++) {
    for (let h = 0; h < g[d].length; h++) {
      const v = g[d][h] || 0;
      sum += v;
      if (v > peak.v) peak = { d, h, v };
    }
  }
  return { peak, sum };
});

// 分类 donut（基于真实分类调用）
const categories = ref([]);
const donutColors = ['#e5a00d', '#3b82f6', '#22c55e', '#a855f7', '#ec4899'];
const donutTotal = computed(() => categories.value.reduce((a, c) => a + (c.count || 0), 0) || 1);
async function loadCategories() {
  const c = (await api.categories.list()) || {};
  // 后端 CategoryDefinitionResponse 现已带 mediaItemCount（单查询子查询统计）
  categories.value = (c.items || c || []).map((x) => ({
    ...x,
    count: x.mediaItemCount ?? 0,
  }));
}

const donutSegments = computed(() => {
  let cum = 0;
  const out = [];
  for (let i = 0; i < categories.value.length; i++) {
    const c = categories.value[i];
    const start = cum / donutTotal.value;
    cum += c.count;
    const end = cum / donutTotal.value;
    const a1 = -Math.PI / 2 + start * 2 * Math.PI;
    const a2 = -Math.PI / 2 + end * 2 * Math.PI;
    const r = 60;
    const x1 = 80 + r * Math.cos(a1);
    const y1 = 80 + r * Math.sin(a1);
    const x2 = 80 + r * Math.cos(a2);
    const y2 = 80 + r * Math.sin(a2);
    const large = end - start > 0.5 ? 1 : 0;
    out.push({
      d: `M ${x1} ${y1} A ${r} ${r} 0 ${large} 1 ${x2} ${y2}`,
      color: donutColors[i % donutColors.length],
      name: c.name,
      count: c.count,
      pct: Math.round((c.count / donutTotal.value) * 100),
    });
  }
  return out;
});

// 监控目录 sparkline（真实数据：来自后端 GET /dashboard/watch-folder-activity，每目录 24 元素 series）
// 第二参数 status 保留兼容旧 template 调用签名，但实际仅用 folderId 查 folderActivity
function sparkPath(folderId, _status) {
  const entry = folderActivity.value.find((e) => e.folderId === folderId);
  const series = Array.isArray(entry?.series) && entry.series.length === 24
    ? entry.series
    : new Array(24).fill(0);
  const max = Math.max(...series, 1);
  return {
    pts: series.map((v, i) => `${(i / 23) * 200},${32 - (v / max) * 28}`).join(' '),
    series,
    max,
  };
}

// 任务调度（真实数据：来自 Audit_ScheduledTaskRun，DESC by StartedAt，最近 24h）
const tasks = computed(() => taskRuns.value);

// 服务健康（真实数据：4 项并行 + 各项独立 3s 超时；空态降级到提示）
const healthChecks = computed(() => healthData.value);

// 顶部 5 块：今日口径 + 实时队列，标签如实区分（旧版把累计 parseSource 当今日命中率，已修正）
const tiles = computed(() => {
  const s = stats.value || {};
  const today = s.today ?? {};
  const queue = s.queue ?? {};
  return [
    { label: '今日处理', value: today.processed ?? 0, hint: '今日已完成归档', color: 'accent' },
    { label: '今日跳过', value: today.skipped ?? 0, hint: '规则忽略 / 重复', color: 'neutral' },
    { label: '今日失败', value: today.failed ?? 0, hint: '解析或归档出错', color: 'danger' },
    { label: '待确认', value: queue.review ?? 0, hint: '需人工确认（含历史积压）', color: 'warning', to: '/review' },
    { label: '队列在途', value: (queue.running ?? 0) + (queue.pending ?? 0), hint: `${queue.running ?? 0} 处理中 · ${queue.pending ?? 0} 排队`, color: 'info', to: '/pending' },
  ];
});

// 服务运行状态（stats.service：running 恒 true + uptimeSeconds）
const service = computed(() => stats.value?.service ?? { running: false, uptimeSeconds: null });

// 累计概览（stats.total，全历史口径）：成功率分母 = 完成 + 失败，跳过不计入成败
const totals = computed(() => {
  const t = stats.value?.total ?? {};
  const processed = t.processed ?? 0;
  const skipped = t.skipped ?? 0;
  const failed = t.failed ?? 0;
  const denom = processed + failed;
  const successRate = denom > 0 ? Math.round((processed / denom) * 100) : 0;
  return { processed, skipped, failed, successRate };
});

// 解析来源占比（stats.parseSource，全历史口径）：规则 / AI / 混合 堆叠条
const parseSource = computed(() => {
  const ps = stats.value?.parseSource ?? {};
  const rule = ps.rule ?? 0;
  const ai = ps.ai ?? 0;
  const hybrid = ps.hybrid ?? 0;
  const total = rule + ai + hybrid;
  const pct = (n) => (total > 0 ? Math.round((n / total) * 100) : 0);
  return {
    total,
    segs: [
      { key: 'rule', label: '规则引擎', value: rule, pct: pct(rule), color: 'var(--success)' },
      { key: 'ai', label: 'AI 解析', value: ai, pct: pct(ai), color: 'var(--accent)' },
      { key: 'hybrid', label: '规则 + AI 混合', value: hybrid, pct: pct(hybrid), color: 'var(--info)' },
    ],
  };
});

const AI_TYPE_LABEL = { Ollama: 'Ollama', OpenAiCompatible: 'OpenAI 兼容', Anthropic: 'Anthropic' };
const COST_TIER_LABEL = { Paid: '付费', Free: '免费' };

// 系统存储合计（库 + 日志 + 海报缓存）
const storageTotal = computed(() => {
  const i = systemInfo.value;
  if (!i) return null;
  return (i.dbSizeBytes ?? 0) + (i.logDirSizeBytes ?? 0) + (i.postersCacheSizeBytes ?? 0);
});

function dotColor(status) {
  return status === 'ok' ? 'success' : status === 'warn' ? 'warning' : status === 'fail' ? 'danger' : 'neutral';
}

// 手动刷新：主聚合 + 分类一并重拉
function reloadAll() {
  return Promise.all([load(), loadCategories()]);
}

// 扫描动作复用 useScanActions：扫完回调 reloadAll 刷新「队列在途」等实时数字
const { scanning, scanAll, scanForceAll, scanOne } = useScanActions(reloadAll);

// 初始化配置完成度：向导里跳过的必需项在此持续提醒（配齐后横幅自动消失）
const setupGaps = ref([]);
async function loadChecklist() {
  const c = await api.setup.checklist().catch(() => null);
  if (!c) { setupGaps.value = []; return; }
  const gaps = [];
  if (!c.tmdbHasApiKey) gaps.push({ label: '配置 TMDB API Token', to: 'SettingsTmdb' });
  if (c.watchFolderCount === 0) gaps.push({ label: '添加监控目录', to: 'SettingsWatchFolders' });
  if (c.categoryUnsetCount > 0) gaps.push({ label: `设置 ${c.categoryUnsetCount} 个分类的归档落点`, to: 'SettingsCategories' });
  setupGaps.value = gaps;
}

onMounted(async () => {
  await reloadAll();
  loadChecklist();
});
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader eyebrow="仪表盘" title="实时运行视图">
      <template #actions>
        <span class="status-pill" :class="{ live: service.running }">
          <span class="pulse" />
          {{ service.running ? '运行中' : '已停止' }}
          <span v-if="service.uptimeSeconds != null" class="up">· 已运行 {{ fmtUptime(service.uptimeSeconds) }}</span>
        </span>
        <button class="btn" :disabled="scanning" @click="scanAll">
          <PmmIcon name="search" :size="14" />
          {{ scanning ? '扫描中…' : '立即扫描全部' }}
        </button>
        <button class="btn btn-danger" :disabled="scanning" title="重新枚举全部目录 + 自动重投 Failed 历史记录" @click="scanForceAll">
          <PmmIcon name="power" :size="14" />
          强制全扫
        </button>
        <button class="btn" @click="reloadAll" :disabled="loading">
          <PmmIcon name="refresh" :size="14" />
          刷新
        </button>
      </template>
    </PmmPageHeader>

    <!-- 初始化配置完成度：有未配齐的必需项时提醒（向导可跳过，这里兜底引导补全；配齐后自动消失） -->
    <div v-if="setupGaps.length" class="setup-gap-card">
      <span class="gap-mark">⚠</span>
      <div class="gap-text">
        <b>初始化未完成</b> · 还有 {{ setupGaps.length }} 项必需配置未完成，否则媒体处理可能失败：
        <button v-for="g in setupGaps" :key="g.to" class="gap-link" @click="router.push({ name: g.to })">{{ g.label }}</button>
      </div>
    </div>

    <!-- Tiles -->
    <div class="tiles">
      <div
        v-for="t in tiles"
        :key="t.label"
        class="card tile"
        :class="{ clickable: t.to }"
        @click="t.to && router.push(t.to)"
      >
        <div class="eyebrow xs">{{ t.label }}</div>
        <div class="font-display tabular tile-val" :style="{ color: `var(--${t.color})` }">{{ t.value }}</div>
        <div class="tile-hint">{{ t.hint }}</div>
      </div>
    </div>

    <!-- 累计概览 + 解析来源 -->
    <div class="row-2">
      <section class="card">
        <header class="card-head row-head">
          <h3 class="h3">累计概览</h3>
          <span class="muted small">全历史</span>
        </header>
        <div class="card-body">
          <div class="totals-grid">
            <div class="total-cell">
              <div class="total-val font-display tabular" style="color: var(--accent)">{{ totals.processed.toLocaleString() }}</div>
              <div class="total-label">累计处理</div>
            </div>
            <div class="total-cell">
              <div class="total-val font-display tabular" style="color: var(--success)">{{ totals.successRate }}%</div>
              <div class="total-label">成功率</div>
            </div>
            <div class="total-cell">
              <div class="total-val font-display tabular" style="color: var(--warning)">{{ totals.skipped.toLocaleString() }}</div>
              <div class="total-label">累计跳过</div>
            </div>
            <div class="total-cell">
              <div class="total-val font-display tabular" style="color: var(--danger)">{{ totals.failed.toLocaleString() }}</div>
              <div class="total-label">累计失败</div>
            </div>
          </div>
        </div>
      </section>

      <section class="card">
        <header class="card-head row-head">
          <h3 class="h3">解析来源占比</h3>
          <span class="muted small">共 {{ parseSource.total.toLocaleString() }} 项</span>
        </header>
        <div class="card-body">
          <div v-if="parseSource.total > 0" class="src-bar">
            <span
              v-for="seg in parseSource.segs.filter((x) => x.value > 0)"
              :key="seg.key"
              class="src-seg"
              :style="{ width: seg.pct + '%', background: seg.color }"
              :title="`${seg.label} ${seg.value}（${seg.pct}%）`"
            />
          </div>
          <div v-else class="empty" style="padding: 18px 0">暂无解析记录</div>
          <div class="src-legend">
            <div v-for="seg in parseSource.segs" :key="seg.key" class="src-row">
              <span class="src-dot" :style="{ background: seg.color }" />
              <span class="src-name">{{ seg.label }}</span>
              <span class="src-pct muted tabular">{{ seg.pct }}%</span>
              <span class="src-cnt tabular">{{ seg.value.toLocaleString() }}</span>
            </div>
          </div>
        </div>
      </section>
    </div>

    <!-- Heatmap + Donut -->
    <div class="row-2">
      <section class="card">
        <header class="card-head row-head">
          <div>
            <h3 class="h3">活动热图</h3>
            <div class="muted small">过去 7 天 × 24 小时 · 文件交付密度</div>
          </div>
          <div class="legend">
            <span class="muted">少</span>
            <span v-for="p in [0.1, 0.3, 0.55, 0.8, 1]" :key="p" class="legend-cell" :style="{ background: `color-mix(in oklab, var(--accent) ${p*100}%, var(--surface-2))` }" />
            <span class="muted">多</span>
          </div>
        </header>
        <div class="card-body">
          <div class="heatmap">
            <div /><!-- 空角 -->
            <div v-for="h in 24" :key="h" class="heatmap-h">{{ (h - 1) % 3 === 0 ? String(h - 1).padStart(2, '0') : '' }}</div>
            <template v-for="(row, d) in heatmap" :key="d">
              <div class="heatmap-d">{{ days[d] }}</div>
              <div
                v-for="(v, h) in row"
                :key="h"
                class="heatmap-cell"
                :style="{ background: v > 0 ? `color-mix(in oklab, var(--accent) ${Math.round(v / heatmapMax * 95) + 5}%, var(--surface-2))` : 'var(--surface-2)' }"
                :title="`${days[d]} ${String(h).padStart(2, '0')}:00 · ${v} 文件`"
              />
            </template>
          </div>
          <div class="heatmap-foot">
            <template v-if="heatmapStats.sum > 0">
              <span>💡 峰值 <b style="color: var(--accent)">{{ days[heatmapStats.peak.d] }} {{ String(heatmapStats.peak.h).padStart(2, '0') }}:00</b> · {{ heatmapStats.peak.v }} 文件</span>
              <span>近 7 天共 <b style="color: var(--text)">{{ heatmapStats.sum }}</b> 个文件交付</span>
            </template>
            <span v-else>近 7 天暂无归档活动</span>
          </div>
        </div>
      </section>

      <section class="card">
        <header class="card-head"><h3 class="h3">媒体库分类分布</h3></header>
        <div class="card-body donut-body">
          <svg viewBox="0 0 160 160" class="donut">
            <circle cx="80" cy="80" r="60" fill="none" stroke="var(--surface-3)" stroke-width="18" />
            <path v-for="(seg, i) in donutSegments" :key="i" :d="seg.d" fill="none" :stroke="seg.color" stroke-width="18" />
            <text x="80" y="76" text-anchor="middle" font-size="22" font-weight="700" font-family="Outfit" fill="var(--text)">
              {{ donutTotal.toLocaleString() }}
            </text>
            <text x="80" y="92" text-anchor="middle" font-size="10" fill="var(--text-dim)">总条目</text>
          </svg>
          <div class="donut-legend">
            <div v-for="(seg, i) in donutSegments" :key="i" class="donut-row">
              <span class="dl-color" :style="{ background: seg.color }" />
              <span class="dl-name">{{ seg.name }}</span>
              <span class="dl-pct muted tabular">{{ seg.pct }}%</span>
              <span class="dl-cnt tabular">{{ seg.count.toLocaleString() }}</span>
            </div>
          </div>
        </div>
      </section>
    </div>

    <!-- Folder health -->
    <section class="card">
      <header class="card-head row-head">
        <h3 class="h3">监控目录健康度</h3>
        <a class="muted small" href="#" @click.prevent="router.push('/settings/watch-folders')">管理目录 →</a>
      </header>
      <div class="folder-grid">
        <div v-for="f in watchFolders" :key="f.id" class="folder-tile" :class="`tone-${dotColor(f.status === 'ok' ? 'ok' : f.enabled ? 'ok' : 'off')}`">
          <div class="ft-head">
            <PmmIcon name="folder" :size="16" />
            <div class="ft-meta">
              <div class="font-mono ft-path">{{ f.path }}</div>
              <div class="ft-tags">
                <span v-if="!f.enabled" class="tag tag-neutral">暂停</span>
                <span v-else class="tag tag-success"><span class="tag-dot" />监控</span>
              </div>
            </div>
            <button
              class="icon-btn ft-scan"
              :disabled="scanning || !f.enabled"
              :title="f.enabled ? '扫描此目录（含仅手动目录）' : '目录已暂停，启用后才能扫描'"
              @click="scanOne(f)"
            >
              <PmmIcon name="play" :size="15" />
            </button>
          </div>
          <svg viewBox="0 0 200 32" preserveAspectRatio="none" class="spark">
            <polyline fill="none" :stroke="`var(--${dotColor(f.enabled ? 'ok' : 'off')})`" stroke-width="1.5" :points="sparkPath(f.id, f.status).pts" />
            <polygon :fill="`var(--${dotColor(f.enabled ? 'ok' : 'off')}-soft)`" :points="`0,32 ${sparkPath(f.id, f.status).pts} 200,32`" />
          </svg>
          <div class="ft-foot">
            <span>{{ f.count.toLocaleString() }} 文件</span>
            <span>{{ f.lastScan ? timeAgo(f.lastScan) : '尚未扫描' }}</span>
          </div>
        </div>
        <div v-if="!watchFolders.length" class="empty">尚未配置监控目录</div>
      </div>
    </section>

    <!-- Schedule + Health -->
    <div class="row-2">
      <section class="card">
        <header class="card-head row-head">
          <h3 class="h3">任务调度</h3>
          <span class="tag tag-info">Quartz.NET</span>
        </header>
        <div class="task-list">
          <div v-for="(t, i) in tasks" :key="i" class="task-row">
            <div class="task-icon" :class="`tone-${t.status === 'done' ? 'success' : t.status === 'fail' ? 'danger' : t.status === 'running' ? 'info' : 'neutral'}`">
              <span v-if="t.status === 'running'" class="task-spinner" aria-label="进行中" />
              <PmmIcon v-else :name="t.status === 'done' ? 'check' : t.status === 'fail' ? 'x' : 'history'" :size="13" :stroke="2.5" />
            </div>
            <div class="task-meta">
              <div class="task-name">
                {{ t.kind }}
                <span v-if="t.status === 'running'" class="tag tag-info" style="margin-left:6px"><span class="tag-dot" />进行中</span>
              </div>
              <div class="muted small">
                {{ t.target }}
                <span v-if="t.duration"> · 耗时 {{ t.duration }}</span>
                <span v-if="t.processed != null"> · 处理 {{ t.processed }} 个</span>
                <span v-if="t.note" style="color: var(--danger)"> · {{ t.note }}</span>
              </div>
            </div>
            <div class="task-time">
              <div class="tabular">{{ fmtClock(t.ts) }}</div>
              <div class="muted xs">{{ timeAgo(t.ts) }}</div>
            </div>
          </div>
          <div v-if="!tasks.length" class="empty">最近 24 小时无定时任务执行记录</div>
        </div>
      </section>

      <section class="card">
        <header class="card-head row-head">
          <h3 class="h3">服务健康度</h3>
          <span class="tag tag-success"><span class="tag-dot" />{{ healthChecks.filter(c => c.status === 'ok').length }} / {{ healthChecks.length }} 正常</span>
        </header>
        <div class="health-list">
          <div v-for="(c, i) in healthChecks" :key="i" class="health-row">
            <span class="health-dot" :class="`tone-${dotColor(c.status)}`" />
            <span class="health-name">{{ c.name }}</span>
            <span class="muted small health-detail">{{ c.detail }}</span>
          </div>
          <div v-if="!healthChecks.length" class="empty">健康检查加载中…</div>
        </div>
      </section>
    </div>

    <!-- AI 提供商 + 系统信息 -->
    <div class="row-2">
      <section class="card">
        <header class="card-head row-head">
          <h3 class="h3">AI 提供商</h3>
          <a class="muted small" href="#" @click.prevent="router.push('/settings/parse-ai-providers')">配置 →</a>
        </header>
        <div class="provider-list">
          <div v-for="p in aiProviders" :key="p.id" class="provider-row" :class="{ disabled: !p.enabled }">
            <span class="provider-dot" :class="p.enabled ? 'on' : 'off'" />
            <div class="provider-meta">
              <div class="provider-name">
                {{ p.name }}
                <span v-if="p.isPrimary" class="tag tag-accent" style="margin-left: 6px">主</span>
              </div>
              <div class="muted small font-mono">{{ AI_TYPE_LABEL[p.type] || p.type }} · {{ p.model }}</div>
            </div>
            <div class="provider-tags">
              <span class="tag" :class="p.costTier === 'Free' ? 'tag-success' : 'tag-neutral'">{{ COST_TIER_LABEL[p.costTier] || p.costTier }}</span>
              <span class="tag" :class="p.enabled ? 'tag-info' : 'tag-neutral'">{{ p.enabled ? '启用' : '停用' }}</span>
            </div>
          </div>
          <div v-if="!aiProviders.length" class="empty">尚未配置 AI 提供商</div>
        </div>
      </section>

      <section class="card">
        <header class="card-head row-head">
          <h3 class="h3">系统信息</h3>
          <a class="muted small" href="#" @click.prevent="router.push('/settings/system')">系统管理 →</a>
        </header>
        <div class="sysinfo-body">
          <template v-if="systemInfo">
            <div class="sys-kv"><span class="k">版本</span><span class="v font-mono tabular">{{ systemInfo.version }}</span></div>
            <div class="sys-kv"><span class="k">运行时</span><span class="v font-mono">{{ systemInfo.runtimeVersion }}</span></div>
            <div class="sys-kv"><span class="k">系统</span><span class="v font-mono">{{ systemInfo.os }} · {{ systemInfo.architecture }}</span></div>
            <div class="sys-kv"><span class="k">数据目录</span><span class="v font-mono sys-path" :title="systemInfo.dataRoot">{{ systemInfo.dataRoot }}</span></div>
            <div class="sys-kv"><span class="k">数据库</span><span class="v tabular">{{ fmtSize(systemInfo.dbSizeBytes) }}</span></div>
            <div class="sys-kv"><span class="k">日志</span><span class="v tabular">{{ fmtSize(systemInfo.logDirSizeBytes) }}</span></div>
            <div class="sys-kv"><span class="k">海报缓存</span><span class="v tabular">{{ fmtSize(systemInfo.postersCacheSizeBytes) }}</span></div>
            <div class="sys-kv total"><span class="k">存储合计</span><span class="v tabular">{{ fmtSize(storageTotal) }}</span></div>
          </template>
          <div v-else class="empty">系统信息加载中…</div>
        </div>
      </section>
    </div>

    <!-- Recent posters strip -->
    <section class="card recent-card">
      <header class="card-head row-head">
        <h3 class="h3">最近归档</h3>
        <a class="muted small" href="#" @click.prevent="router.push('/history')">全部历史 →</a>
      </header>
      <div class="poster-strip">
        <a v-for="r in recents" :key="r.id" class="poster-link" @click="router.push(`/media/${r.id}`)">
          <PmmPoster :title="r.title || r.fileName" :year="r.year" size="md" />
          <div class="poster-name">{{ r.title || r.fileName }}</div>
          <div class="muted xs">{{ timeAgo(r.updatedAt) }}</div>
        </a>
        <div v-if="!recents.length" class="empty">暂无归档记录</div>
      </div>
    </section>
  </div>
</template>

<style scoped lang="scss">
.tiles {
  display: grid;
  grid-template-columns: repeat(5, 1fr);
  gap: 14px;
  margin-bottom: 18px;
}
.tile {
  padding: 16px;
}
.tile-val {
  font-size: 30px;
  margin-top: 4px;
  line-height: 1.1;
}
.tile-hint {
  font-size: 11px;
  color: var(--text-dim);
  margin-top: 4px;
}
.tile.clickable {
  cursor: pointer;
  transition: border-color 0.15s, transform 0.15s;
}
.tile.clickable:hover {
  border-color: var(--accent);
  transform: translateY(-2px);
}

// 服务状态徽标
.status-pill {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  height: 30px;
  padding: 0 12px;
  border-radius: 999px;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-mute);
  background: var(--surface-2);
  border: 1px solid var(--border-soft);
}
.status-pill.live {
  color: var(--success);
  background: var(--success-soft);
  border-color: transparent;
}
.status-pill .pulse {
  width: 8px;
  height: 8px;
  border-radius: 999px;
  background: currentColor;
}
.status-pill.live .pulse {
  box-shadow: 0 0 0 0 var(--success);
  animation: pmm-pulse 1.8s ease-out infinite;
}
.status-pill .up { color: inherit; opacity: 0.85; font-weight: 500; }
@keyframes pmm-pulse {
  0% { box-shadow: 0 0 0 0 color-mix(in oklab, var(--success) 60%, transparent); }
  70% { box-shadow: 0 0 0 6px transparent; }
  100% { box-shadow: 0 0 0 0 transparent; }
}

// 累计概览
.totals-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 12px;
}
.total-cell {
  text-align: center;
  padding: 6px 4px;
}
.total-val { font-size: 26px; line-height: 1.1; }
.total-label { font-size: 11.5px; color: var(--text-mute); margin-top: 6px; }

// 解析来源堆叠条
.src-bar {
  display: flex;
  height: 14px;
  border-radius: 7px;
  overflow: hidden;
  background: var(--surface-2);
  margin-bottom: 14px;
}
.src-seg { height: 100%; transition: width 0.3s; }
.src-seg:not(:last-child) { border-right: 1px solid var(--bg-elev); }
.src-legend { display: flex; flex-direction: column; gap: 9px; }
.src-row { display: grid; grid-template-columns: 10px 1fr auto auto; gap: 10px; align-items: center; font-size: 12.5px; }
.src-dot { width: 8px; height: 8px; border-radius: 2px; }
.src-name { font-weight: 600; }
.src-pct { min-width: 38px; text-align: right; }
.src-cnt { min-width: 56px; text-align: right; font-weight: 600; }

// AI 提供商列表
.provider-list { padding: 8px 0; }
.provider-row {
  padding: 10px 18px;
  display: grid;
  grid-template-columns: 10px 1fr auto;
  gap: 12px;
  align-items: center;
  border-bottom: 1px solid var(--border-soft);
}
.provider-row:last-child { border-bottom: 0; }
.provider-row.disabled { opacity: 0.6; }
.provider-dot { width: 8px; height: 8px; border-radius: 999px; }
.provider-dot.on { background: var(--success); box-shadow: 0 0 0 3px var(--success-soft); }
.provider-dot.off { background: var(--neutral); }
.provider-meta { min-width: 0; }
.provider-name { font-size: 13px; font-weight: 600; }
.provider-tags { display: flex; gap: 6px; }

// 系统信息
.sysinfo-body { padding: 14px 18px; display: flex; flex-direction: column; gap: 9px; }
.sys-kv { display: flex; align-items: center; justify-content: space-between; gap: 12px; font-size: 12.5px; }
.sys-kv .k { color: var(--text-mute); flex-shrink: 0; }
.sys-kv .v { color: var(--text); text-align: right; min-width: 0; }
.sys-path { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.sys-kv.total { border-top: 1px solid var(--border-soft); padding-top: 9px; margin-top: 2px; }
.sys-kv.total .k, .sys-kv.total .v { font-weight: 700; color: var(--text); }

.row-2 {
  display: grid;
  grid-template-columns: 1.6fr 1fr;
  gap: 18px;
  margin-bottom: 18px;
}
.row-head { justify-content: space-between; }
.legend {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
}
.legend-cell { width: 11px; height: 11px; border-radius: 2px; }
.heatmap {
  display: grid;
  grid-template-columns: 32px repeat(24, 1fr);
  gap: 3px;
  align-items: center;
}
.heatmap-h { font-size: 9px; color: var(--text-dim); text-align: center; font-variant-numeric: tabular-nums; }
.heatmap-d { font-size: 11px; color: var(--text-mute); text-align: right; padding-right: 6px; }
.heatmap-cell { aspect-ratio: 1; border-radius: 3px; }
.heatmap-foot {
  margin-top: 14px;
  padding-top: 12px;
  border-top: 1px solid var(--border-soft);
  display: flex;
  gap: 20px;
  font-size: 12px;
  color: var(--text-mute);
}

.donut-body { display: flex; flex-direction: column; align-items: center; gap: 18px; }
.donut { width: 180px; height: 180px; }
.donut-legend { width: 100%; display: flex; flex-direction: column; gap: 8px; }
.donut-row { display: grid; grid-template-columns: 10px 1fr auto auto; gap: 10px; align-items: center; font-size: 12px; }
.dl-color { width: 8px; height: 8px; border-radius: 2px; }
.dl-name { font-weight: 600; }

.folder-grid {
  padding: 18px;
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 14px;
}
.folder-tile {
  padding: 14px;
  background: var(--bg-elev);
  border: 1px solid var(--border-soft);
  border-radius: 8px;
  border-left: 3px solid var(--success);
}
.folder-tile.tone-warning { border-left-color: var(--warning); }
.folder-tile.tone-danger { border-left-color: var(--danger); }
.folder-tile.tone-neutral { border-left-color: var(--neutral); }
.folder-tile.tone-off { border-left-color: var(--text-faint); }
.ft-head { display: flex; gap: 10px; align-items: flex-start; margin-bottom: 8px; }
.ft-meta { flex: 1; min-width: 0; }
.ft-scan { flex-shrink: 0; margin-top: -2px; }
.ft-path { font-size: 12px; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.ft-tags { display: flex; gap: 4px; margin-top: 4px; }
.spark { width: 100%; height: 32px; }
.ft-foot { display: flex; justify-content: space-between; margin-top: 6px; font-size: 11px; color: var(--text-mute); }

.task-list { padding: 8px 0; }
.task-row {
  padding: 10px 18px;
  border-bottom: 1px solid var(--border-soft);
  display: grid;
  grid-template-columns: auto 1fr auto;
  gap: 12px;
  align-items: center;
}
.task-row:last-child { border-bottom: 0; }
.task-row.future { opacity: 0.7; }
.task-icon {
  width: 26px;
  height: 26px;
  border-radius: 999px;
  display: grid;
  place-items: center;
}
.task-icon.tone-success { background: var(--success-soft); color: var(--success); }
.task-icon.tone-danger { background: var(--danger-soft); color: var(--danger); }
.task-icon.tone-neutral { background: var(--surface-2); color: var(--text-mute); }
.task-name { font-size: 13px; font-weight: 600; }
.task-time { text-align: right; font-size: 11px; font-variant-numeric: tabular-nums; font-weight: 600; color: var(--text-mute); }

.health-list { padding: 8px 0; }
.health-row {
  padding: 8px 18px;
  display: grid;
  grid-template-columns: 10px 1fr auto;
  gap: 12px;
  align-items: center;
  font-size: 12.5px;
}
.health-dot {
  width: 8px;
  height: 8px;
  border-radius: 999px;
}
.health-dot.tone-success { background: var(--success); box-shadow: 0 0 0 3px var(--success-soft); }
.health-dot.tone-warning { background: var(--warning); box-shadow: 0 0 0 3px var(--warning-soft); }
.health-dot.tone-danger { background: var(--danger); box-shadow: 0 0 0 3px var(--danger-soft); }
.health-dot.tone-neutral { background: var(--neutral); }
.health-name { font-weight: 500; }
.health-detail { text-align: right; }

.recent-card { margin-top: 18px; }
.poster-strip {
  display: grid;
  grid-auto-flow: column;
  grid-auto-columns: 120px;
  gap: 14px;
  padding: 18px;
  overflow-x: auto;
}
.poster-link { display: flex; flex-direction: column; gap: 6px; cursor: pointer; min-width: 120px; }
.poster-name { font-size: 11.5px; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }

.small { font-size: 11.5px; }
.xs { font-size: 10.5px; }
.empty { padding: 30px; text-align: center; color: var(--text-mute); }

// r1 P2-3：进行中任务旋转 spinner
.task-spinner {
  display: inline-block;
  width: 13px;
  height: 13px;
  border: 2px solid currentColor;
  border-top-color: transparent;
  border-radius: 50%;
  animation: pmm-spin 0.9s linear infinite;
}
@keyframes pmm-spin {
  to { transform: rotate(360deg); }
}

/* 初始化配置完成度提醒条 */
.setup-gap-card {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 12px 16px;
  margin-bottom: 4px;
  border: 1px solid color-mix(in oklab, var(--warning, #c97a0e) 35%, transparent);
  background: color-mix(in oklab, var(--warning, #c97a0e) 10%, transparent);
  border-radius: var(--r-3, 12px);
  font-size: 13.5px;
}
.setup-gap-card .gap-mark { font-size: 18px; color: var(--warning, #c97a0e); flex: none; }
.setup-gap-card .gap-text b { color: var(--text); }
.setup-gap-card .gap-link {
  background: none;
  border: none;
  color: var(--accent);
  cursor: pointer;
  font-size: 13.5px;
  padding: 0 6px;
  text-decoration: underline;
}
.setup-gap-card .gap-link:hover { opacity: 0.8; }
</style>
