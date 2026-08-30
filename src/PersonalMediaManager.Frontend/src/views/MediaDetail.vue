<script setup>
import { computed, onMounted, ref } from 'vue';
import { useRouter } from 'vue-router';
import { ElMessage, ElMessageBox } from 'element-plus';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPoster from '@/components/PmmPoster.vue';
import SubtitleSearchDialog from '@/components/SubtitleSearchDialog.vue';
import { timeAgo, fmtTime, fmtClock, stripExt, STATUS_MAP, SOURCE_MAP, COUNTRY_FLAGS, fmtSize } from '@/utils/format';

const props = defineProps({ id: { type: [String, Number], required: true } });
const router = useRouter();

const record = ref(null);
const loading = ref(false);
const expanded = ref({});
const posterFailed = ref(false);

// 真实海报端点（匿名，<img> 直接加载）；加载失败 @error 置 posterFailed → 降级 PmmPoster 占位
const posterUrl = computed(() => (record.value?.lib?.tmdbId ? `/api/history/${record.value.id}/poster` : ''));

async function load() {
  loading.value = true;
  try {
    // 真实接入：单条获取 (api.history.detail)
    // P3 #12 类型示范（IDE 会按 OpenAPI schema 校验 detail 字段名/类型）：
    /** @type {import('@/api/schema').paths['/api/history/{id}']['get']['responses']['200']['content']['application/json']} */
    const detail = await api.history.detail(props.id);
    record.value = buildStory(detail);
    posterFailed.value = false;
    // 默认展开关键节点
    expanded.value = {
      Parsing: true,
      AiParsing: true,
      TmdbRematching: true,
      Archiving: true,
    };
  } finally {
    loading.value = false;
  }
}

// 从 parsedInfo JSON 字符串提取常用字段；解析失败返回空对象
function parseInfo(raw) {
  if (!raw) return {};
  try { return JSON.parse(raw); } catch { return {}; }
}

// 阶段 → 中文标签 / 图标（与原 mock 保持一致）
const STAGE_META = {
  Detected: { label: '文件发现', icon: 'folder' },
  Queued: { label: '入队', icon: 'inbox' },
  Parsing: { label: '规则引擎解析', icon: 'filter' },
  TmdbMatching: { label: 'TMDB 第一次查询', icon: 'film' },
  AiParsing: { label: 'AI 兜底解析', icon: 'brain' },
  TmdbRematching: { label: 'TMDB 二次查询', icon: 'film' },
  Classifying: { label: '分类判断', icon: 'layers' },
  Archiving: { label: '归档落地', icon: 'archive' },
  AwaitingReview: { label: '人工确认', icon: 'inbox' },
  Completed: { label: '完成', icon: 'check' },
  Skipped: { label: '跳过', icon: 'x' },
  Ignored: { label: '已忽略', icon: 'x' },
  Cancelled: { label: '已取消', icon: 'x' },
  Failed: { label: '失败', icon: 'x' },
};

// TMDB 步骤数据来源 → 标签文案 / 配色（与后端 ProcessFileService.TmdbSourceLabel 取值一一对应）
// remote=真的打了 TMDB 远端；cache=命中本地搜索缓存(DB)；reuse=命中本地剧集映射(内存/持久，未发搜索)
const TMDB_SOURCE_META = {
  remote: { label: '🌐 TMDB 远端', variant: 'info' },
  cache: { label: '⚡ 本地缓存', variant: 'success' },
  reuse: { label: '⚡ 本地剧集映射', variant: 'success' },
};

function buildStory(d) {
  const info = parseInfo(d?.parsedInfo);
  const tmdb = d?.tmdb || null;
  const lib = {
    title: tmdb?.title || info.title || d?.fileName || '未知标题',
    origTitle: tmdb?.originalTitle || info.originalTitle || '',
    year: tmdb?.year || info.year || null,
    tmdbId: d?.tmdbId || tmdb?.tmdbId || null,
    country: (tmdb?.originCountry?.[0]) || info.originCountry || 'CN',
    lang: tmdb?.originalLanguage || info.originalLanguage || 'zh',
    type: d?.tmdbMediaType || info.type || 'tv',
    // genres / rating 后端尚未持久化，UI 隐藏对应区块；plot 走 tmdb?.overview 兜底
    plot: tmdb?.overview || '',
    category: d?.category || '未分类',
  };
  // 真实时间线步骤（来自 Process_Step 表；空时降级为单条 Detected 占位让前端不空白）
  const rawSteps = Array.isArray(d?.steps) ? d.steps : [];
  const steps = rawSteps.length > 0
    ? rawSteps.map((s) => {
        const meta = STAGE_META[s.stage] || { label: s.stage, icon: 'history' };
        let detail = {};
        try { detail = s.detail ? JSON.parse(s.detail) : {}; } catch { detail = {}; }
        return {
          stage: s.stage,
          label: meta.label,
          icon: meta.icon,
          ts: s.startedAt,
          durMs: s.durMs || 0,
          branched: s.stage === 'AiParsing',
          detail,
        };
      })
    : [{ stage: d?.status || 'Detected', label: STAGE_META[d?.status || 'Detected']?.label || '处理中', icon: 'history',
         ts: d?.createdAt || new Date().toISOString(), durMs: 0, detail: { note: '处理时间线尚未生成（旧记录或当前正在处理）' } }];

  // 详情时间锚点（用于 hero stats）
  const detectedAt = steps[0]?.ts || d?.createdAt || new Date().toISOString();
  const completedAt = steps[steps.length - 1]?.ts || d?.archivedAt || d?.updatedAt || detectedAt;
  const durationMs = Math.max(0, new Date(completedAt).getTime() - new Date(detectedAt).getTime());

  return {
    id: d?.id || props.id,
    lib,
    fileName: d?.fileName || '',
    sourcePath: d?.sourcePath || '',
    targetPath: d?.targetPath || '',
    // r1 P2-8：fileSize / confidence 保留原值 null，让模板用 v-if 兜底；
    // 旧实现 fmtSize() || '—' 看起来兜底了，但 confidence ?? 0 会让 v-if="record.confidence != null" 永远命中显示「置信度 0%」
    fileSize: d?.fileSize ?? null,
    fileHash: d?.fileHash || '',
    // 第 0 季（特别篇）/ 第 0 集（SxxE00）为合法值：?? 保留 0；缺失时置 null 由模板 v-if 隐藏（不再伪造 S01E01）
    season: info.season ?? null,
    episode: info.episode ?? null,
    episodeEnd: info.episodeEnd ?? null,
    status: d?.status || 'Completed',
    source: d?.parseSource || 'Hybrid',
    confidence: d?.confidence ?? null,
    detectedAt,
    completedAt,
    durationMs,
    aiCalls: d?.aiCalls || [],
    steps,
  };
}

function toggle(stage) {
  expanded.value = { ...expanded.value, [stage]: !expanded.value[stage] };
}

function isOpen(stage) {
  return !!expanded.value[stage];
}

const archiveSteps = computed(() => {
  const arch = record.value?.steps?.find((s) => s.stage === 'Archiving');
  const subs = arch?.detail?.steps;
  if (!Array.isArray(subs) || !subs.length) return { steps: [], total: 1 };
  const total = subs.reduce((a, s) => a + (s.durMs || 0), 0);
  return { steps: subs, total: Math.max(1, total) };
});

const rescanning = ref(false);
const cancelling = ref(false);
// Archiving 可能已经完成同盘文件移动；此时取消会让数据库与磁盘不一致，因此只允许归档开始前取消。
const IN_FLIGHT_STATES = ['Detected', 'Queued', 'Parsing', 'TmdbMatching', 'AiParsing', 'TmdbRematching', 'Classifying'];
const canCancel = computed(() => IN_FLIGHT_STATES.includes(record.value?.status));

function handleViewLogs() {
  if (!record.value) return;
  // 跳日志页并带关键字过滤：原文件名做为最稳定的 grep 键
  const key = record.value.fileName || `#${record.value.id}`;
  router.push({ path: '/logs', query: { keyword: key } });
}

async function handleRescan() {
  if (!record.value || rescanning.value) return;
  try {
    await ElMessageBox.confirm(`确认重新处理 "${record.value.fileName}"？将重置状态并重新进入解析队列。`, '提示', { type: 'warning' });
  } catch {
    return;
  }
  rescanning.value = true;
  try {
    await api.history.rescan(record.value.id);
    ElMessage.success('已重投入解析队列');
    await load();
  } catch (e) {
    ElMessage.error(`重投失败：${e?.message || e}`);
  } finally {
    rescanning.value = false;
  }
}

async function handleCancel() {
  if (!record.value || cancelling.value) return;
  try {
    await ElMessageBox.confirm(
      `确认取消“${record.value.fileName}”的处理任务？已完成的处理步骤不会回退，源文件不会被删除。`,
      '取消任务',
      { type: 'warning', confirmButtonText: '确认取消', cancelButtonText: '继续处理' },
    );
  } catch {
    return;
  }

  cancelling.value = true;
  try {
    await api.history.cancel(record.value.id);
    ElMessage.success('任务已取消');
    await load();
  } catch (e) {
    ElMessage.error(`取消失败：${e?.message || e}`);
    await load();
  } finally {
    cancelling.value = false;
  }
}

async function handleDownloadNfo() {
  if (!record.value) return;
  try {
    const blob = await api.history.nfo(record.value.id);
    // 后端业务错误时 ExceptionHandler 会返回 application/json + code=1000；
    // axios `responseType: blob` 仍会把 JSON 包成 Blob，type 暴露原 Content-Type
    if (blob.type && blob.type.includes('json')) {
      const text = await blob.text();
      let msg = '下载 .nfo 失败';
      try { msg = JSON.parse(text)?.message || msg; } catch { /* JSON 解析失败保持兜底文案 */ }
      ElMessage.error(msg);
      return;
    }
    const baseName = (record.value.fileName || `media-${record.value.id}`).replace(/\.[^/.]+$/, '');
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${baseName}.nfo`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
    ElMessage.success('已下载 .nfo');
  } catch {
    /* axios 拦截器已 ElMessage.error */
  }
}

async function handleCopyTmdbLink() {
  const tid = record.value?.lib?.tmdbId;
  if (!tid) {
    ElMessage.warning('当前记录无 TmdbId');
    return;
  }
  const kind = record.value?.lib?.type === 'movie' ? 'movie' : 'tv';
  const url = `https://www.themoviedb.org/${kind}/${tid}`;
  try {
    await navigator.clipboard.writeText(url);
    ElMessage.success('已复制 TMDB 链接');
  } catch {
    ElMessage.error('复制失败：浏览器拒绝访问剪贴板');
  }
}

// ---- 归档去向测试 / 手动移动 ----
const archiveDialog = ref(false);
const archivePreview = ref(null);
const archiveLoading = ref(false);
const archiveMoving = ref(false);

// 仅失败 / 跳过 / 待确认记录可手动移动（与后端 HistoryService.ManualArchiveAsync 守门一致）
const MANUAL_ARCHIVE_STATES = ['Failed', 'Skipped', 'AwaitingReview'];
const canManualArchive = computed(() => MANUAL_ARCHIVE_STATES.includes(record.value?.status));

// 退回重改：已确认归档（Completed/Skipped）可一键退回「待人工确认」队列重新填资料（区别于仅 Failed 的「重新处理」）
const canReopen = computed(() => ['Completed', 'Skipped'].includes(record.value?.status));
const reopening = ref(false);
async function handleReopen() {
  if (!record.value || reopening.value) return;
  try {
    await ElMessageBox.confirm(
      `确认将《${record.value.fileName}》退回到「待人工确认」队列重新填资料？已归档文件会移回源位置。`,
      '退回重改', { type: 'warning' });
  } catch {
    return;
  }
  reopening.value = true;
  try {
    await api.history.reopen(record.value.id);
    ElMessage.success('已退回待确认队列，可在「人工确认」重新填资料');
    await load();
  } catch (e) {
    ElMessage.error(`退回失败：${e?.message || e}`);
  } finally {
    reopening.value = false;
  }
}

// ---- 字幕下载（仅已归档 Completed 记录；defaultKeyword 用原始文件名去扩展名，stripExt 收口在 utils/format） ----
const subtitleDialog = ref(false);

// 打开诊断对话框：只读演算归档去向并逐项体检（测试按钮 + 手动移动按钮共用入口）
async function openArchiveDialog() {
  if (!record.value) return;
  archiveDialog.value = true;
  archivePreview.value = null;
  archiveLoading.value = true;
  try {
    archivePreview.value = await api.history.previewArchive(record.value.id);
  } catch {
    /* 拦截器已 ElMessage.error；保留对话框，让用户看到加载失败 */
  } finally {
    archiveLoading.value = false;
  }
}

// 执行手动移动 / 复制：二次确认 → 调端点 → 成功关闭并刷新；失败时重新体检暴露问题
async function doManualArchive(mode) {
  if (!record.value || archiveMoving.value) return;
  const label = mode === 'Copy' ? '复制' : '移动';
  try {
    await ElMessageBox.confirm(
      `确认将该文件${label}到目标位置？${mode === 'Move' ? '移动会删除源文件。' : '复制会保留源文件。'}`,
      `手动${label}`,
      { type: 'warning', confirmButtonText: `确认${label}`, cancelButtonText: '取消' });
  } catch {
    return;
  }
  archiveMoving.value = true;
  try {
    const r = await api.history.manualArchive(record.value.id, mode);
    if (r?.outcome === 'Skipped') {
      ElMessage.warning('目标已存在同名文件，已按冲突策略跳过');
    } else {
      ElMessage.success(`已${label}到：${r?.targetPath || '目标位置'}`);
    }
    // 归档降级容错：后端正为 ManualArchiveResult 增加 warnings 字段（视频已落地但 nfo/海报等未写成）；
    // 当前 schema 尚无该字段，这里用可选链防御性读取——字段缺失时静默跳过，schema 重生后自然对齐
    if (Array.isArray(r?.warnings) && r.warnings.length) {
      ElMessage.warning(`视频已归档，但部分元数据未写入（待补元数据）：${r.warnings.join('；')}`);
    }
    archiveDialog.value = false;
    await load();
  } catch {
    // 失败：拦截器已弹错；记录可能已被拉回 Failed，重新体检让用户看清最新阻断项
    await openArchiveDialog();
  } finally {
    archiveMoving.value = false;
  }
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading" v-if="record">
    <!-- 返回栏 -->
    <div class="back-bar">
      <button class="btn btn-ghost btn-sm" @click="router.back()">
        <PmmIcon name="chevronLeft" :size="14" /> 返回
      </button>
      <span class="muted small">媒体记录 #{{ record.id }}</span>
      <div style="margin-left: auto; display: flex; gap: 6px">
        <button class="btn btn-ghost btn-sm" @click="handleViewLogs">
          <PmmIcon name="terminal" :size="14" /> 查看日志
        </button>
        <button v-if="record.status === 'Failed'" class="btn btn-ghost btn-sm" :disabled="rescanning" @click="handleRescan">
          <PmmIcon name="refresh" :size="14" /> {{ rescanning ? '处理中…' : '重新处理' }}
        </button>
        <button v-if="canCancel" class="btn btn-ghost btn-sm" :disabled="cancelling" @click="handleCancel">
          <PmmIcon name="x" :size="14" /> {{ cancelling ? '取消中…' : '取消任务' }}
        </button>
        <button v-if="canReopen" class="btn btn-ghost btn-sm" :disabled="reopening" @click="handleReopen">
          <PmmIcon name="refresh" :size="14" /> {{ reopening ? '处理中…' : '退回重改' }}
        </button>
        <button class="btn btn-ghost btn-sm" @click="openArchiveDialog">
          <PmmIcon name="search" :size="14" /> 测试路径
        </button>
        <button v-if="canManualArchive" class="btn btn-ghost btn-sm" :disabled="archiveMoving" @click="openArchiveDialog">
          <PmmIcon name="folder" :size="14" /> 手动移动
        </button>
        <button v-if="record.status === 'Completed'" class="btn btn-ghost btn-sm" @click="subtitleDialog = true">
          <PmmIcon name="download" :size="14" /> 下载字幕
        </button>
        <button class="btn btn-ghost btn-sm" @click="handleCopyTmdbLink">
          <PmmIcon name="link" :size="14" /> 复制 TMDB 链接
        </button>
        <button class="btn btn-ghost btn-sm" :disabled="!record?.targetPath" @click="handleDownloadNfo">
          <PmmIcon name="download" :size="14" /> 下载 .nfo
        </button>
      </div>
    </div>

    <!-- Hero -->
    <div class="card hero">
      <div class="hero-bg" :style="{ background: 'linear-gradient(135deg, #4d1f29 0%, #a53c4f 60%, #f4a4b4 130%)' }" />
      <div class="hero-overlay" />
      <div class="hero-row">
        <img
          v-if="posterUrl && !posterFailed"
          :src="posterUrl"
          class="hero-poster-img"
          alt=""
          @error="posterFailed = true"
        />
        <PmmPoster v-else :title="record.lib.title" :year="record.lib.year" :type="record.lib.type" size="lg" />
        <div class="hero-meta">
          <div class="hero-tags">
            <span class="tag" :class="`tag-${STATUS_MAP[record.status]?.variant || 'neutral'}`">
              <span class="tag-dot" />{{ STATUS_MAP[record.status]?.label || record.status }}
            </span>
            <span class="tag" :class="`tag-${SOURCE_MAP[record.source]?.variant || 'neutral'}`">{{ SOURCE_MAP[record.source]?.label || record.source }}</span>
            <span v-if="record.confidence != null" class="tag tag-info">置信度 {{ Math.round(record.confidence * 100) }}%</span>
            <span class="muted small">{{ timeAgo(record.completedAt) }}</span>
          </div>
          <h1 class="h1 hero-title">
            {{ record.lib.title }}
            <span class="muted hero-year">({{ record.lib.year }})</span>
            <span v-if="record.season != null" class="font-mono hero-se">
              · S{{ String(record.season).padStart(2, '0') }}E{{ String(record.episode ?? 1).padStart(2, '0') }}<template v-if="record.episodeEnd && record.episodeEnd > (record.episode ?? 1)">-E{{ String(record.episodeEnd).padStart(2, '0') }}</template>
            </span>
          </h1>
          <div class="muted small hero-sub">
            {{ record.lib.origTitle }} · TmdbId {{ record.lib.tmdbId }} · {{ COUNTRY_FLAGS[record.lib.country] || '' }} {{ record.lib.country }}
          </div>
          <div class="hero-stats">
            <div>
              <div class="eyebrow xs">检测于</div>
              <div class="font-display tabular val">{{ fmtTime(record.detectedAt) }}</div>
            </div>
            <div>
              <div class="eyebrow xs">总耗时</div>
              <div class="font-display tabular val">{{ (record.durationMs / 1000).toFixed(1) }}s</div>
            </div>
            <div v-if="record.fileSize != null">
              <div class="eyebrow xs">文件大小</div>
              <div class="font-display tabular val">{{ fmtSize(record.fileSize) }}</div>
            </div>
            <div>
              <div class="eyebrow xs">分类</div>
              <div class="font-display tabular val">{{ record.lib.category }}</div>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- 两栏：时间线 + 右侧元数据 -->
    <div class="md-row">
      <section class="card timeline-card">
        <header class="card-head row-head">
          <h3 class="h3">处理时间线</h3>
          <div style="display: flex; gap: 6px">
            <button class="btn btn-ghost btn-sm" @click="expanded = Object.fromEntries(record.steps.map(s => [s.stage, true]))">全部展开</button>
            <button class="btn btn-ghost btn-sm" @click="expanded = {}">仅默认</button>
          </div>
        </header>
        <div class="tl-body">
          <div v-for="(step, i) in record.steps" :key="step.stage" class="tl-step">
            <div class="tl-rail">
              <div class="tl-dot" :class="[`tone-${STATUS_MAP[step.stage]?.variant || 'neutral'}`, { opened: isOpen(step.stage) }]">
                <PmmIcon :name="step.icon" :size="15" />
              </div>
              <div v-if="i < record.steps.length - 1" class="tl-line" />
            </div>
            <div class="tl-content">
              <button class="tl-head" @click="toggle(step.stage)">
                <div class="tl-head-meta">
                  <div class="tl-head-line">
                    <span class="tl-label">{{ step.label }}</span>
                    <span class="tag" :class="`tag-${STATUS_MAP[step.stage]?.variant || 'neutral'}`">
                      {{ STATUS_MAP[step.stage]?.label || step.stage }}
                    </span>
                    <span v-if="step.detail?.skipped" class="tag tag-neutral">跳过</span>
                    <!-- 归档降级容错：视频已落地但部分元数据未写成（折叠态也可见的提示标） -->
                    <span v-if="step.detail?.metadataPending" class="tag tag-warning">待补元数据</span>
                    <span v-if="step.branched" class="tag tag-accent">分支点</span>
                  </div>
                  <div class="tl-head-meta-line muted small">
                    <span>+{{ ((new Date(step.ts).getTime() - new Date(record.detectedAt).getTime()) / 1000).toFixed(2) }}s</span>
                    <span v-if="step.durMs > 0">耗时 {{ step.durMs >= 1000 ? (step.durMs / 1000).toFixed(2) + 's' : step.durMs + 'ms' }}</span>
                    <span class="font-mono">{{ fmtClock(step.ts) }}</span>
                  </div>
                </div>
                <PmmIcon :name="isOpen(step.stage) ? 'chevronDown' : 'chevronRight'" :size="14" style="color: var(--text-mute)" />
              </button>
              <div v-if="isOpen(step.stage)" class="tl-detail">
                <!-- Detected -->
                <template v-if="step.stage === 'Detected'">
                  <div v-if="step.detail.folder" class="kv"><span class="kv-k">目录</span><span class="font-mono">{{ step.detail.folder }}</span></div>
                  <div v-if="step.detail.trigger" class="kv"><span class="kv-k">触发方式</span><span class="tag tag-accent"><span class="tag-dot" />{{ step.detail.trigger }}</span></div>
                  <div v-if="!step.detail.folder && !step.detail.trigger" class="muted small">—</div>
                </template>

                <!-- Queued -->
                <template v-else-if="step.stage === 'Queued'">
                  <div v-if="step.detail.semaphore" class="kv"><span class="kv-k">并发控制</span><span>{{ step.detail.semaphore }}</span></div>
                  <div v-if="!step.detail.semaphore" class="muted small">—</div>
                </template>

                <!-- Parsing -->
                <template v-else-if="step.stage === 'Parsing'">
                  <div v-if="step.detail.cleaned" class="kv"><span class="kv-k">清洗后</span><span class="font-mono">{{ step.detail.cleaned }}</span></div>
                  <template v-if="Array.isArray(step.detail.rules) && step.detail.rules.length">
                    <div class="eyebrow xs section-sub">规则匹配尝试</div>
                    <div class="parse-rules">
                      <div v-for="r in step.detail.rules" :key="r.name" class="parse-rule-row" :class="{ hit: r.hit }">
                        <PmmIcon :name="r.hit ? 'check' : 'x'" :size="12" :stroke="2.5" />
                        <span class="parse-rule-name">{{ r.name }}</span>
                        <span class="muted small">{{ r.reason }}</span>
                      </div>
                    </div>
                  </template>
                  <template v-if="step.detail.extracted">
                    <div class="eyebrow xs section-sub">提取结果</div>
                    <div class="parse-grid">
                      <div class="pg-cell">
                        <div class="eyebrow xs">标题</div>
                        <div class="font-mono">{{ step.detail.extracted.title || '—' }}</div>
                      </div>
                      <div class="pg-cell">
                        <div class="eyebrow xs">类型</div>
                        <div class="font-mono">{{ step.detail.extracted.type === 'tv' ? '剧集' : step.detail.extracted.type === 'movie' ? '电影' : '—' }}</div>
                      </div>
                      <div class="pg-cell">
                        <div class="eyebrow xs">季 / 集</div>
                        <!-- season=0（特别篇）为合法值：用 != null 判断，避免提取到 S00 时整体显示「—」 -->
                        <div class="font-mono tabular">{{ step.detail.extracted.season != null ? `S${String(step.detail.extracted.season).padStart(2,'0')}E${String(step.detail.extracted.episode ?? 0).padStart(2,'0')}${step.detail.extracted.episodeEnd && step.detail.extracted.episodeEnd > (step.detail.extracted.episode ?? 0) ? `-E${String(step.detail.extracted.episodeEnd).padStart(2,'0')}` : ''}` : '—' }}</div>
                      </div>
                      <div class="pg-cell">
                        <div class="eyebrow xs">年份</div>
                        <div class="font-mono tabular">{{ step.detail.extracted.year || '—' }}</div>
                      </div>
                    </div>
                  </template>
                  <div v-if="typeof step.detail.confidence === 'number'" class="kv">
                    <span class="kv-k">置信度</span>
                    <span class="conf-row">
                      <span class="conf-bar"><span :style="{ width: Math.round(step.detail.confidence * 100) + '%', background: step.detail.confidence >= 0.85 ? 'var(--success)' : step.detail.confidence >= 0.6 ? 'var(--warning)' : 'var(--danger)' }" /></span>
                      <span class="tabular" :style="{ color: step.detail.confidence >= 0.85 ? 'var(--success)' : step.detail.confidence >= 0.6 ? 'var(--warning)' : 'var(--danger)' }">{{ Math.round(step.detail.confidence * 100) }}%</span>
                    </span>
                  </div>
                  <div v-if="step.detail.decision" class="kv"><span class="kv-k">决策</span><span class="tag tag-accent">{{ step.detail.decision }}</span></div>
                </template>

                <!-- TMDB skipped or matched -->
                <template v-else-if="step.stage === 'TmdbMatching' || step.stage === 'TmdbRematching'">
                  <div v-if="step.detail.skipped" class="skipped-block">
                    <PmmIcon name="info" :size="12" style="vertical-align: middle; margin-right: 6px" />
                    {{ step.detail.reason || '已跳过' }}
                  </div>
                  <template v-else>
                    <div v-if="step.detail.query" class="kv"><span class="kv-k">查询参数</span><span class="font-mono">{{ step.detail.query }}</span></div>
                    <div v-if="step.detail.source" class="kv">
                      <span class="kv-k">数据来源</span>
                      <span class="tag" :class="`tag-${TMDB_SOURCE_META[step.detail.source]?.variant || 'neutral'}`">
                        <span class="tag-dot" />{{ TMDB_SOURCE_META[step.detail.source]?.label || step.detail.source }}
                      </span>
                    </div>
                    <div v-if="step.detail.api" class="kv"><span class="kv-k">HTTP</span><span class="font-mono" style="color: var(--info)">{{ step.detail.api }}</span></div>
                    <template v-if="Array.isArray(step.detail.candidates) && step.detail.candidates.length">
                      <div class="eyebrow xs section-sub">候选结果（{{ step.detail.candidates.length }}）</div>
                      <div class="cand-list">
                        <div v-for="(c, ci) in step.detail.candidates" :key="c.tmdbId || ci" class="cand-row" :class="{ picked: step.detail.picked && c.tmdbId === step.detail.picked.tmdbId }">
                          <PmmPoster :title="c.title" :year="c.year" size="xs" :show-text="false" />
                          <div>
                            <div class="cand-title">{{ c.title }} <span v-if="c.year" class="muted">({{ c.year }})</span></div>
                            <div class="muted font-mono xs">TmdbId {{ c.tmdbId }}<span v-if="c.mediaType"> · {{ c.mediaType }}</span></div>
                          </div>
                          <div v-if="step.detail.picked && c.tmdbId === step.detail.picked.tmdbId" class="cand-score">
                            <div class="picked-label">✓ 采用</div>
                          </div>
                        </div>
                      </div>
                    </template>
                    <div v-if="step.detail.decision" class="kv"><span class="kv-k">决策</span><span>{{ step.detail.decision }}</span></div>
                  </template>
                </template>

                <!-- AI -->
                <template v-else-if="step.stage === 'AiParsing'">
                  <div class="kv">
                    <span class="kv-k">数据来源</span>
                    <span v-if="step.detail.reusedFromFolderCache" class="tag tag-success"><span class="tag-dot" />⚡ 本地剧集映射·跳过 AI</span>
                    <span v-else class="tag tag-info"><span class="tag-dot" />🌐 AI 远端调用</span>
                  </div>
                  <template v-if="Array.isArray(step.detail.attempts) && step.detail.attempts.length">
                    <div class="eyebrow xs section-sub" style="margin-top: 0">升级链（逐级而上，共 {{ step.detail.attempts.length }} 级）</div>
                    <div class="ai-attempts">
                      <div v-for="(a, idx) in step.detail.attempts" :key="idx" class="ai-attempt" :class="{ ok: a.success, err: !a.success }">
                        <div class="ai-attempt-head">
                          <span class="muted xs" style="font-weight: 700">#{{ idx + 1 }}</span>
                          <span style="font-size: 12.5px; font-weight: 600">{{ a.provider }}</span>
                          <span v-if="a.isPrimary" class="tag tag-accent">主</span>
                          <span v-else class="tag tag-neutral">备</span>
                          <span class="tag" :class="a.success ? 'tag-success' : 'tag-danger'"><span class="tag-dot" />{{ a.success ? '成功' : '失败' }}</span>
                          <span v-if="a.latencyMs != null" class="muted tabular small" style="margin-left: auto">{{ a.latencyMs }}ms</span>
                        </div>
                        <pre v-if="a.prompt" class="ai-prompt"># prompt
{{ a.prompt }}</pre>
                        <pre v-if="a.output" class="ai-out">{{ JSON.stringify(a.output, null, 2) }}</pre>
                        <div v-if="!a.success && a.reason" class="kv">
                          <span class="kv-k">升级原因</span>
                          <span style="color: var(--danger)"><span v-if="a.errorType" class="tag tag-danger" style="margin-right: 6px">{{ a.errorType }}</span>{{ a.reason }}</span>
                        </div>
                        <div v-if="typeof a.confidence === 'number'" class="kv">
                          <span class="kv-k">置信度</span>
                          <span class="conf-row">
                            <span class="conf-bar"><span :style="{ width: Math.round(a.confidence*100) + '%', background: 'var(--success)' }" /></span>
                            <span class="tabular" style="color: var(--success)">{{ Math.round(a.confidence * 100) }}%</span>
                          </span>
                        </div>
                      </div>
                    </div>
                  </template>
                  <template v-else>
                    <div v-if="step.detail.success != null" class="kv">
                      <span class="kv-k">结果</span>
                      <span class="tag" :class="step.detail.success ? 'tag-success' : 'tag-danger'"><span class="tag-dot" />{{ step.detail.success ? '成功' : '失败' }}</span>
                    </div>
                    <div v-if="step.detail.output" class="kv">
                      <span class="kv-k">输出</span>
                      <pre class="ai-out" style="margin: 0">{{ JSON.stringify(step.detail.output, null, 2) }}</pre>
                    </div>
                    <div v-if="step.detail.failureSummary" class="kv"><span class="kv-k">失败原因</span><span style="color: var(--danger)">{{ step.detail.failureSummary }}</span></div>
                  </template>
                  <div v-if="step.detail.decision" class="kv" style="margin-top: 10px"><span class="kv-k">决策</span><span class="tag tag-success"><span class="tag-dot" />{{ step.detail.decision }}</span></div>
                </template>

                <!-- Classifying -->
                <template v-else-if="step.stage === 'Classifying'">
                  <template v-if="step.detail.inputs">
                    <div class="eyebrow xs section-sub" style="margin-top: 0">输入特征</div>
                    <div class="cls-tags">
                      <span v-if="step.detail.inputs.originCountry" class="tag tag-neutral">originCountry: {{ step.detail.inputs.originCountry }}</span>
                      <span v-if="step.detail.inputs.originalLanguage" class="tag tag-neutral">originalLanguage: {{ step.detail.inputs.originalLanguage }}</span>
                      <span v-if="step.detail.inputs.type" class="tag tag-neutral">type: {{ step.detail.inputs.type }}</span>
                    </div>
                  </template>
                  <template v-if="Array.isArray(step.detail.rules) && step.detail.rules.length">
                    <div class="eyebrow xs section-sub">规则评估</div>
                    <div v-for="r in step.detail.rules" :key="r.name" class="cls-rule">
                      <div class="cls-rule-name">
                        <PmmIcon name="check" :size="12" :stroke="2.5" style="color: var(--success); margin-right: 6px" />
                        {{ r.name }}
                      </div>
                      <div v-if="r.condition" class="muted font-mono small">{{ r.condition }}</div>
                      <div v-if="r.category" style="font-size: 12px">→ 分类: <b style="color: var(--accent)">{{ r.category }}</b></div>
                    </div>
                  </template>
                  <div v-if="step.detail.categoryId" class="kv"><span class="kv-k">命中分类</span><span class="font-mono">CategoryId {{ step.detail.categoryId }}</span></div>
                  <div v-if="step.detail.decision" class="kv"><span class="kv-k">决策</span><span>{{ step.detail.decision }}</span></div>
                </template>

                <!-- Archiving -->
                <template v-else-if="step.stage === 'Archiving'">
                  <div v-if="step.detail.operation" class="kv"><span class="kv-k">操作</span><span class="tag tag-info">{{ step.detail.operation }}</span></div>
                  <div v-if="step.detail.source" class="kv"><span class="kv-k">源路径</span><span class="font-mono" style="color: var(--text-mute); word-break: break-all">{{ step.detail.source }}</span></div>
                  <div v-if="step.detail.target" class="kv"><span class="kv-k">目标路径</span><span class="font-mono" style="color: var(--accent); word-break: break-all">{{ step.detail.target }}</span></div>
                  <!-- 归档降级容错（后端 7892db6）：视频已落地但 nfo/海报/Webhook 等未写成时，detail 带 metadataPending + warnings；
                       旧记录 / 正常归档无该字段，v-if 自然隐藏（detail 已在 buildStory 做过防御性 JSON.parse） -->
                  <el-alert
                    v-if="step.detail?.metadataPending"
                    type="warning"
                    :closable="false"
                    show-icon
                    title="视频已归档成功，但部分元数据未写入（待补元数据）"
                    class="meta-pending-alert"
                  >
                    <div v-if="Array.isArray(step.detail.warnings) && step.detail.warnings.length" class="meta-pending-list">
                      <div v-for="(w, wi) in step.detail.warnings" :key="wi" class="meta-pending-item">· {{ w }}</div>
                    </div>
                    <div v-else class="meta-pending-item">可通过「重新处理」或「刷新元数据」补齐</div>
                  </el-alert>
                  <template v-if="Array.isArray(step.detail.steps) && step.detail.steps.length">
                    <div class="eyebrow xs section-sub">归档子步骤</div>
                    <div class="arch-list">
                      <div v-for="s in archiveSteps.steps" :key="s.name" class="arch-row">
                        <PmmIcon name="check" :size="12" :stroke="2.5" style="color: var(--success)" />
                        <span class="arch-name">{{ s.name }}</span>
                        <div class="arch-bar">
                          <div :style="{ width: (s.durMs / archiveSteps.total * 100) + '%' }" />
                        </div>
                        <span class="muted tabular small arch-dur">
                          {{ s.durMs >= 1000 ? (s.durMs / 1000).toFixed(2) + 's' : s.durMs + 'ms' }}
                          <span v-if="s.throughput" style="color: var(--success); margin-left: 6px">{{ s.throughput }}</span>
                        </span>
                      </div>
                    </div>
                  </template>
                </template>

                <!-- Completed -->
                <template v-else-if="step.stage === 'Completed'">
                  <div v-if="step.detail.target" class="kv"><span class="kv-k">归档目标</span><span class="font-mono" style="color: var(--accent); word-break: break-all">{{ step.detail.target }}</span></div>
                  <template v-if="Array.isArray(step.detail.webhooks) && step.detail.webhooks.length">
                    <div class="eyebrow xs section-sub">Webhook 投递</div>
                    <div class="wh-out">
                      <div v-for="w in step.detail.webhooks" :key="w.target" class="wh-out-row">
                        <span class="tag tag-info">{{ w.event }}</span>
                        <span class="font-mono small muted wh-target">{{ w.target }}</span>
                        <span class="tag tag-success">{{ w.status }}</span>
                        <span class="muted tabular small">{{ w.latencyMs }}ms</span>
                      </div>
                    </div>
                  </template>
                </template>

                <!-- AwaitingReview / Skipped / Failed 终态：通用 reason -->
                <template v-else>
                  <div v-if="step.detail.reason" class="kv"><span class="kv-k">原因</span><span>{{ step.detail.reason }}</span></div>
                  <div v-if="step.detail.fromStage" class="kv"><span class="kv-k">来自阶段</span><span class="font-mono">{{ step.detail.fromStage }}</span></div>
                  <div v-if="step.detail.note" class="muted small">{{ step.detail.note }}</div>
                </template>
              </div>
            </div>
          </div>
        </div>
      </section>

      <aside class="rail">
        <section class="card">
          <header class="card-head"><h3 class="h3">TMDB 元数据</h3></header>
          <div class="card-body">
            <div class="kv"><span class="kv-k">TmdbId</span><span class="font-mono">{{ record.lib.tmdbId }}</span></div>
            <div class="kv"><span class="kv-k">中文标题</span><span>{{ record.lib.title }}</span></div>
            <div class="kv"><span class="kv-k">原标题</span><span>{{ record.lib.origTitle }}</span></div>
            <div class="kv"><span class="kv-k">年份</span><span class="font-mono tabular">{{ record.lib.year }}</span></div>
            <div class="kv"><span class="kv-k">原产国</span><span>{{ COUNTRY_FLAGS[record.lib.country] || '' }} {{ record.lib.country }}</span></div>
            <div class="kv"><span class="kv-k">语言</span><span class="font-mono">{{ record.lib.lang }}</span></div>
            <div v-if="record.lib.plot" class="kv"><span class="kv-k">简介</span><span style="font-size: 12px; line-height: 1.5">{{ record.lib.plot }}</span></div>
          </div>
        </section>

        <section class="card">
          <header class="card-head"><h3 class="h3">文件信息</h3></header>
          <div class="card-body">
            <div class="kv"><span class="kv-k">原文件名</span><span class="font-mono" style="word-break: break-all; color: var(--text-mute)">{{ record.fileName }}</span></div>
            <div class="kv"><span class="kv-k">大小</span><span class="font-mono">{{ record.fileSize != null ? fmtSize(record.fileSize) : '—' }}</span></div>
            <div class="kv"><span class="kv-k">SHA256</span><span class="font-mono small" style="word-break: break-all; color: var(--text-mute)">{{ record.fileHash }}</span></div>
            <div class="kv"><span class="kv-k">目标路径</span><span class="font-mono small" style="word-break: break-all; color: var(--accent)">{{ record.targetPath }}</span></div>
          </div>
        </section>

      </aside>
    </div>

    <!-- 归档去向测试 / 手动移动 -->
    <el-dialog v-model="archiveDialog" title="归档去向测试 / 手动移动" width="580px" append-to-body>
      <div v-loading="archiveLoading" class="ap-body">
        <template v-if="archivePreview">
          <div class="ap-verdict" :class="archivePreview.canArchive ? 'ok' : 'bad'">
            <PmmIcon :name="archivePreview.canArchive ? 'check' : 'warning'" :size="16" />
            <span>{{ archivePreview.canArchive ? '归档去向正常，可手动移动 / 复制' : (archivePreview.error || '存在阻断项，暂不可移动') }}</span>
          </div>

          <div v-if="archivePreview.targetPath" class="kv">
            <span class="kv-k">目标路径</span>
            <span class="font-mono" style="color: var(--accent); word-break: break-all">{{ archivePreview.targetPath }}</span>
          </div>
          <div v-if="archivePreview.targetExists" class="ap-warn">
            <PmmIcon name="warning" :size="13" /> 目标已存在同名文件，按归档冲突策略可能跳过 / 保留多版本 / 升级替换
          </div>

          <div class="eyebrow xs section-sub">逐项体检</div>
          <div class="ap-checks">
            <div v-for="(c, i) in archivePreview.checks" :key="i" class="ap-check" :class="{ bad: !c.ok }">
              <PmmIcon :name="c.ok ? 'check' : 'x'" :size="13" :stroke="2.5" />
              <span class="ap-check-name">{{ c.name }}</span>
              <span v-if="c.detail" class="ap-check-detail font-mono">{{ c.detail }}</span>
            </div>
          </div>
        </template>
        <div v-else-if="!archiveLoading" class="muted small">未能加载体检结果</div>
      </div>
      <template #footer>
        <div class="ap-footer">
          <span v-if="archivePreview && !canManualArchive" class="muted small">
            当前状态（{{ STATUS_MAP[record.status]?.label || record.status }}）不支持手动移动
          </span>
          <span style="flex: 1" />
          <button class="btn btn-ghost btn-sm" @click="archiveDialog = false">关闭</button>
          <button
            class="btn btn-sm"
            :disabled="!canManualArchive || !archivePreview?.canArchive || archiveMoving"
            @click="doManualArchive('Copy')"
          >
            <PmmIcon name="layers" :size="14" /> 复制到目标
          </button>
          <button
            class="btn btn-primary btn-sm"
            :disabled="!canManualArchive || !archivePreview?.canArchive || archiveMoving"
            @click="doManualArchive('Move')"
          >
            <PmmIcon name="upload" :size="14" /> {{ archiveMoving ? '处理中…' : '移动到目标' }}
          </button>
        </div>
      </template>
    </el-dialog>

    <!-- 字幕下载弹窗（仅 Completed 记录入口可见） -->
    <SubtitleSearchDialog
      v-model="subtitleDialog"
      :media-id="Number(record.id)"
      :default-keyword="stripExt(record.fileName)"
    />
  </div>
</template>

<style scoped lang="scss">
.back-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 18px;
}

.hero {
  padding: 0;
  overflow: hidden;
  position: relative;
}
.hero-bg {
  position: absolute;
  inset: 0;
  opacity: 0.5;
  filter: blur(40px) saturate(140%);
  transform: scale(1.2);
}
.hero-overlay {
  position: absolute;
  inset: 0;
  background: linear-gradient(180deg, color-mix(in oklab, var(--bg) 30%, transparent), var(--surface) 80%);
}
.hero-row {
  position: relative;
  padding: 28px;
  display: grid;
  grid-template-columns: 160px 1fr;
  gap: 28px;
  align-items: flex-start;
}
.hero-poster-img {
  width: 140px;
  aspect-ratio: 2 / 3;
  object-fit: cover;
  border-radius: var(--r-2);
  box-shadow: var(--shadow-poster);
  border: 1px solid var(--border-soft);
  display: block;
  flex-shrink: 0;
}
.hero-meta { min-width: 0; }
.hero-tags { display: flex; align-items: center; gap: 10px; margin-bottom: 8px; flex-wrap: wrap; }
.hero-title { display: flex; align-items: baseline; gap: 12px; flex-wrap: wrap; }
.hero-year { font-weight: 500; font-size: 18px; }
.hero-se { color: var(--accent); font-size: 16px; }
.hero-sub { margin-top: 4px; }
.hero-stats {
  display: flex;
  gap: 26px;
  margin-top: 18px;
  flex-wrap: wrap;
}
.hero-stats .val { font-size: 16px; margin-top: 4px; }

.md-row {
  margin-top: 22px;
  display: grid;
  grid-template-columns: 1.5fr 1fr;
  gap: 22px;
}
.row-head { justify-content: space-between; }

.tl-body { padding: 16px 18px 22px; }
.tl-step { display: grid; grid-template-columns: 40px 1fr; gap: 14px; }
.tl-rail { display: flex; flex-direction: column; align-items: center; }
.tl-dot {
  width: 32px;
  height: 32px;
  border-radius: 999px;
  border: 1px solid var(--border);
  background: var(--surface-2);
  display: grid;
  place-items: center;
  flex-shrink: 0;
  z-index: 1;
}
.tl-dot.opened.tone-success { background: var(--success-soft); color: var(--success); }
.tl-dot.opened.tone-info { background: var(--info-soft); color: var(--info); }
.tl-dot.opened.tone-warning { background: var(--warning-soft); color: var(--warning); }
.tl-dot.opened.tone-danger { background: var(--danger-soft); color: var(--danger); }
.tl-dot.opened.tone-accent { background: var(--accent-soft); color: var(--accent); }
.tl-dot.opened.tone-neutral { background: var(--neutral-soft); color: var(--neutral); }
.tl-line { flex: 1; width: 2px; background: var(--border-soft); margin-top: 2px; min-height: 12px; }
.tl-content { padding-bottom: 18px; min-width: 0; }
.tl-head {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  text-align: left;
  padding: 4px 0;
  background: transparent;
  border: 0;
  cursor: pointer;
  color: var(--text);
}
.tl-head-meta { flex: 1; }
.tl-head-line { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.tl-label { font-size: 13.5px; font-weight: 600; color: var(--text); }
.tl-head-meta-line { display: flex; gap: 12px; margin-top: 2px; }
.tl-detail {
  margin-top: 12px;
  padding: 14px;
  background: var(--bg-elev);
  border: 1px solid var(--border-soft);
  border-radius: 8px;
}

.kv {
  display: grid;
  grid-template-columns: 100px 1fr;
  gap: 14px;
  padding: 5px 0;
  align-items: baseline;
}
.kv-k {
  font-size: 11.5px;
  color: var(--text-mute);
  font-weight: 600;
}
.kv-tags { display: flex; gap: 4px; flex-wrap: wrap; }
.section-sub { margin: 10px 0 6px; }

.parse-rules { display: flex; flex-direction: column; gap: 4px; }
.parse-rule-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 5px 8px;
  border-radius: 4px;
}
.parse-rule-row.hit { background: var(--success-soft); }
.parse-rule-row > svg { color: var(--text-dim); }
.parse-rule-row.hit > svg { color: var(--success); }
.parse-rule-name { font-size: 12px; font-weight: 600; min-width: 200px; }

.parse-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 8px;
  margin-bottom: 10px;
}
.pg-cell {
  padding: 8px;
  background: var(--surface-2);
  border-radius: 4px;
  border: 1px solid var(--border-soft);
}
.pg-cell .font-mono { font-size: 12px; font-weight: 600; margin-top: 3px; }

.conf-row { display: flex; align-items: center; gap: 10px; }
.conf-bar {
  flex: 1;
  height: 6px;
  background: var(--surface-3);
  border-radius: 999px;
  overflow: hidden;
  max-width: 200px;
}
.conf-bar span { display: block; height: 100%; border-radius: 999px; }

.skipped-block {
  padding: 10px;
  background: var(--neutral-soft);
  border-radius: 4px;
  color: var(--text-mute);
  font-size: 12px;
}

.cand-list { display: flex; flex-direction: column; gap: 6px; }
.cand-row {
  display: grid;
  grid-template-columns: auto 1fr auto;
  gap: 10px;
  align-items: center;
  padding: 8px;
  border-radius: 4px;
  background: var(--surface-2);
  border: 1px solid var(--border-soft);
}
.cand-row.picked { background: var(--success-soft); border-color: var(--success); }
.cand-title { font-size: 12px; font-weight: 600; }
.cand-score { text-align: right; }
.cand-score .font-display { font-size: 14px; font-weight: 700; color: var(--text-mute); }
.cand-score .font-display.picked { color: var(--success); }
.picked-label { font-size: 10px; color: var(--success); font-weight: 600; }

.ai-attempts { display: flex; flex-direction: column; gap: 10px; }
.ai-attempt {
  padding: 10px;
  background: var(--surface-2);
  border-radius: 6px;
}
.ai-attempt.ok { border: 1px solid color-mix(in oklab, var(--success) 30%, transparent); }
.ai-attempt.err { border: 1px solid color-mix(in oklab, var(--danger) 30%, transparent); }
.ai-attempt-head { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; flex-wrap: wrap; }
.ai-prompt, .ai-out {
  margin: 0 0 8px;
  padding: 8px;
  background: var(--bg);
  border-radius: 4px;
  font-size: 11.5px;
  font-family: 'JetBrains Mono', monospace;
  white-space: pre-wrap;
  word-break: break-all;
}
.ai-prompt { color: var(--text-mute); }
.ai-out { color: var(--success); }

.cls-tags { display: flex; gap: 8px; margin-bottom: 12px; flex-wrap: wrap; }
.cls-rule {
  padding: 10px;
  background: var(--success-soft);
  border: 1px solid color-mix(in oklab, var(--success) 30%, transparent);
  border-radius: 6px;
  margin-bottom: 6px;
}
.cls-rule-name { font-size: 12.5px; font-weight: 700; margin-bottom: 4px; }

.arch-list { display: flex; flex-direction: column; gap: 4px; }
.arch-row {
  display: grid;
  grid-template-columns: 20px 1fr 80px auto;
  gap: 8px;
  align-items: center;
  padding: 5px 8px;
  border-radius: 4px;
}
.arch-name { font-size: 12px; }
.arch-bar { height: 4px; background: var(--surface-3); border-radius: 999px; overflow: hidden; }
.arch-bar > div { height: 100%; background: var(--success); }
.arch-dur { min-width: 80px; text-align: right; }

/* 待补元数据警示块（归档降级容错） */
.meta-pending-alert { margin: 8px 0 10px; }
.meta-pending-list { display: flex; flex-direction: column; gap: 2px; }
.meta-pending-item { font-size: 12px; line-height: 1.55; word-break: break-all; }

.wh-out { display: flex; flex-direction: column; gap: 6px; }
.wh-out-row {
  display: grid;
  grid-template-columns: auto 1fr auto auto;
  gap: 10px;
  align-items: center;
  padding: 6px 10px;
  background: var(--surface-2);
  border-radius: 4px;
}
.wh-target { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

.rail { display: flex; flex-direction: column; gap: 16px; }
.nfo {
  padding: 14px 18px;
  margin: 0;
  font-size: 11px;
  font-family: 'JetBrains Mono', monospace;
  color: var(--text-mute);
  background: var(--bg-elev);
  max-height: 240px;
  overflow: auto;
  line-height: 1.6;
  white-space: pre;
}

/* 归档去向测试 / 手动移动对话框 */
.ap-body { min-height: 90px; }
.ap-verdict {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  border-radius: 6px;
  font-size: 13px;
  font-weight: 600;
  margin-bottom: 12px;
}
.ap-verdict.ok { background: var(--success-soft); color: var(--success); }
.ap-verdict.bad { background: var(--danger-soft); color: var(--danger); }
.ap-warn {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 8px 0;
  padding: 7px 10px;
  border-radius: 4px;
  background: var(--warning-soft);
  color: var(--warning);
  font-size: 12px;
}
.ap-checks { display: flex; flex-direction: column; gap: 4px; }
.ap-check {
  display: grid;
  grid-template-columns: 16px auto 1fr;
  align-items: center;
  gap: 8px;
  padding: 5px 8px;
  border-radius: 4px;
  background: var(--success-soft);
}
.ap-check > svg { color: var(--success); }
.ap-check.bad { background: var(--danger-soft); }
.ap-check.bad > svg { color: var(--danger); }
.ap-check-name { font-size: 12px; font-weight: 600; white-space: nowrap; }
.ap-check-detail {
  font-size: 11px;
  color: var(--text-mute);
  text-align: right;
  word-break: break-all;
  overflow: hidden;
  text-overflow: ellipsis;
}
.ap-footer { display: flex; align-items: center; gap: 8px; width: 100%; }

.small { font-size: 11.5px; }
.xs { font-size: 10.5px; }
</style>
