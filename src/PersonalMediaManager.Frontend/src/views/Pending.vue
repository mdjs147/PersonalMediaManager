<script setup>
/**
 * 处理队列（Pending）
 *
 * 只读视图：展示全部「在途任务」（排队 + 处理中），不含待确认队列与终态。
 * 与 Dashboard「队列长度」同口径（后端 MediaItemStatusGroups.InFlight）。
 *
 * 后端契约：
 * - GET /api/history/pending?page&pageSize → HistoryListPage（出参结构同处理历史 list）
 *   items 按 Id 正序（先进先出）：串行管线下队首即「当前/下一个」处理项。
 *
 * 自动刷新：仅第 1 页静默轮询（5s），翻页后停轮询避免打断浏览；组件卸载清定时器。
 */
import { onMounted, onUnmounted, ref } from 'vue';
import { useRouter } from 'vue-router';
import { ElMessage } from 'element-plus';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPoster from '@/components/PmmPoster.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';
import { STATUS_MAP, SOURCE_MAP, fmtSize, fmtTime, timeAgo } from '@/utils/format';

const POLL_INTERVAL_MS = 5000;

const router = useRouter();

const loading = ref(false);
const items = ref([]);
const total = ref(0);
const page = ref(1);
const pageSize = ref(20);
let pollTimer = null;

/** 拉取队列；silent=true 时不切换整页 loading（用于自动刷新，避免闪烁） */
async function load(silent = false) {
  if (!silent) loading.value = true;
  try {
    const data = await api.history.pending({ page: page.value, pageSize: pageSize.value });
    items.value = data?.items || [];
    total.value = data?.total || 0;
  } finally {
    if (!silent) loading.value = false;
  }
}

function onPageChange(v) {
  page.value = v;
  load();
}
function onPageSizeChange(v) {
  pageSize.value = v;
  page.value = 1;
  load();
}

// ---------- 手动整理 + 演练预览 ----------
const manualVisible = ref(false);
const manualPath = ref('');
const manualBusy = ref(false);
const previewBusy = ref(false);
const preview = ref(null);

// 演练判定 → 中文标签 + 配色
const OUTCOME_MAP = {
  WouldArchive: { label: '将直接归档', variant: 'success' },
  WouldCallAi: { label: '将触发 AI 兜底', variant: 'info' },
  WouldReview: { label: '将转人工补全', variant: 'warning' },
};

function openManual() {
  manualPath.value = '';
  preview.value = null;
  manualVisible.value = true;
}

async function doPreview() {
  const p = manualPath.value.trim();
  if (!p) {
    ElMessage.warning('请输入文件或目录路径');
    return;
  }
  previewBusy.value = true;
  try {
    preview.value = await api.scan.dryRun(p);
  } finally {
    previewBusy.value = false;
  }
}

async function doOrganize() {
  const p = manualPath.value.trim();
  if (!p) {
    ElMessage.warning('请输入文件或目录路径');
    return;
  }
  manualBusy.value = true;
  try {
    const r = await api.scan.path(p);
    const msg = r.isDirectory
      ? `已入队 ${r.filesEnqueued} 个文件${r.skipped ? `，跳过 ${r.skipped} 个已在库` : ''}`
      : r.filesEnqueued
        ? '已入队 1 个文件'
        : r.skipped
          ? '该文件已在库中'
          : '未入队';
    ElMessage.success(msg);
    manualVisible.value = false;
    page.value = 1;
    load();
  } finally {
    manualBusy.value = false;
  }
}

// 仅第 1 页静默轮询：翻页时不刷新，避免用户翻看历史页时被拉回
onMounted(() => {
  load();
  pollTimer = setInterval(() => {
    if (page.value === 1 && !loading.value) load(true);
  }, POLL_INTERVAL_MS);
});
onUnmounted(() => {
  if (pollTimer) clearInterval(pollTimer);
});
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader eyebrow="处理队列" :title="`${total} 个任务在途`">
      <template #actions>
        <span v-if="page === 1" class="muted small auto-hint">
          <PmmIcon name="refresh" :size="12" /> 每 {{ POLL_INTERVAL_MS / 1000 }} 秒自动刷新
        </span>
        <button class="btn btn-sm" @click="openManual">
          <PmmIcon name="folder" :size="14" /> 手动整理
        </button>
        <button class="btn btn-sm" @click="load()" :disabled="loading">
          <PmmIcon name="refresh" :size="14" /> 刷新
        </button>
      </template>
    </PmmPageHeader>

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th style="width: 50px"></th>
            <th>媒体</th>
            <th>来源</th>
            <th>当前阶段</th>
            <th>大小</th>
            <th>最后更新</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in items" :key="row.id" class="queue-row" @click="router.push(`/media/${row.id}`)">
            <td>
              <PmmPoster
                :title="row.title || row.fileName"
                :year="row.year"
                size="xs"
                :show-text="false"
              />
            </td>
            <td>
              <div class="cell-strong">
                {{ row.title || row.fileName }}
                <!-- 第 0 季（特别篇）/ 第 0 集（SxxE00）为合法值：用 != null 守卫与 ?? 兜底，避免真值判断把 0 吞成 1 或整体隐藏 -->
                <span v-if="row.season != null" class="muted small s-e">
                  S{{ String(row.season).padStart(2, '0') }}E{{ String(row.episode ?? 1).padStart(2, '0') }}<template v-if="row.episodeEnd && row.episodeEnd > (row.episode ?? 1)">-E{{ String(row.episodeEnd).padStart(2, '0') }}</template>
                </span>
              </div>
              <div class="font-mono path-cell">{{ row.fileName }}</div>
            </td>
            <td>
              <span
                v-if="row.parseSource && SOURCE_MAP[row.parseSource]"
                class="tag"
                :class="`tag-${SOURCE_MAP[row.parseSource].variant}`"
              >
                {{ SOURCE_MAP[row.parseSource].label }}
              </span>
              <span v-else class="muted small">—</span>
            </td>
            <td>
              <span
                v-if="STATUS_MAP[row.status]"
                class="tag"
                :class="`tag-${STATUS_MAP[row.status].variant}`"
              >
                <span class="tag-dot" />{{ STATUS_MAP[row.status].label }}
              </span>
              <span v-else class="tag tag-neutral">{{ row.status }}</span>
            </td>
            <td class="tabular muted">{{ fmtSize(row.fileSize) }}</td>
            <td class="muted tabular small">{{ fmtTime(row.updatedAt) }} · {{ timeAgo(row.updatedAt) }}</td>
          </tr>
          <tr v-if="!items.length && !loading">
            <td colspan="6" class="empty">
              <PmmIcon name="check" :size="32" style="color: var(--success); margin-bottom: 8px" />
              <div>队列已清空，没有在途任务</div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <el-pagination
      class="pagination"
      :current-page="page"
      :page-size="pageSize"
      :total="total"
      :page-sizes="[20, 50, 100]"
      layout="total, sizes, prev, pager, next"
      @update:current-page="onPageChange"
      @update:page-size="onPageSizeChange"
    />

    <el-dialog v-model="manualVisible" title="手动整理" width="620px">
      <el-input
        v-model="manualPath"
        placeholder="输入视频文件或目录的绝对路径，如 F:\Downloads\盗梦空间.2010.mkv"
        clearable
        @keyup.enter="doPreview"
      />
      <div class="manual-hint muted small">
        支持单个视频文件或整个目录（目录会递归扫描视频）；不必是已配置的监控目录。可先「预览」确认解析结果再整理。
      </div>

      <div v-if="preview" class="preview-box">
        <div class="preview-head">
          <span class="tag" :class="`tag-${OUTCOME_MAP[preview.outcome]?.variant || 'neutral'}`">
            <span class="tag-dot" />{{ OUTCOME_MAP[preview.outcome]?.label || preview.outcome }}
          </span>
          <span class="muted small">{{ preview.outcomeReason }}</span>
        </div>

        <div class="preview-grid">
          <div>
            <span class="muted small">标题</span>
            <div>{{ preview.title || '—' }}<span v-if="preview.year" class="muted"> ({{ preview.year }})</span></div>
          </div>
          <div>
            <span class="muted small">类型</span>
            <div>{{ preview.mediaType || '—' }}</div>
          </div>
          <!-- 与归档预览路径保持一致：episode=0 原样显示 E00（…S01E00.mkv），不再被真值兜底显示成 E01 -->
          <div v-if="preview.season != null">
            <span class="muted small">季 / 集</span>
            <div>S{{ String(preview.season).padStart(2, '0') }}E{{ String(preview.episode ?? 1).padStart(2, '0') }}</div>
          </div>
          <div>
            <span class="muted small">规则置信度</span>
            <div>{{ (preview.confidence ?? 0).toFixed(2) }}</div>
          </div>
        </div>

        <div v-if="preview.previewRelativePath" class="preview-path">
          <span class="muted small">归档预览路径</span>
          <div class="font-mono">{{ preview.previewRelativePath }}</div>
          <div v-if="preview.previewNote" class="muted small">{{ preview.previewNote }}</div>
        </div>

        <div v-if="preview.candidates && preview.candidates.length" class="preview-cands">
          <span class="muted small">TMDB 候选（{{ preview.candidates.length }}）</span>
          <div
            v-for="c in preview.candidates"
            :key="c.tmdbId"
            class="cand-row"
            :class="{ picked: preview.picked && preview.picked.tmdbId === c.tmdbId }"
          >
            {{ c.title || '—' }}<span v-if="c.year" class="muted"> ({{ c.year }})</span>
            <span class="muted small">#{{ c.tmdbId }}</span>
          </div>
        </div>
      </div>

      <template #footer>
        <button class="btn" @click="doPreview" :disabled="previewBusy || manualBusy">
          <PmmIcon name="search" :size="14" /> {{ previewBusy ? '预览中…' : '预览' }}
        </button>
        <button class="btn btn-primary" @click="doOrganize" :disabled="manualBusy || previewBusy">
          {{ manualBusy ? '整理中…' : '开始整理' }}
        </button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped lang="scss">
.auto-hint {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-right: 4px;
}
.small { font-size: 11px; }
.table-card { overflow: hidden; }
.queue-row { cursor: pointer; }
.s-e { margin-left: 8px; font-weight: 500; }
.path-cell {
  font-size: 11.5px;
  color: var(--text-dim);
  max-width: 380px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  margin-top: 2px;
}
.empty {
  text-align: center;
  padding: 48px 20px;
  color: var(--text-dim);
}
.pagination {
  margin-top: 14px;
  justify-content: flex-end;
  display: flex;
}
.manual-hint {
  margin-top: 8px;
  line-height: 1.5;
}
.preview-box {
  margin-top: 16px;
  padding: 14px;
  border: 1px solid var(--border-soft);
  border-radius: var(--r-2);
  background: var(--surface-2);
}
.preview-head {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  margin-bottom: 12px;
}
.preview-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
  gap: 12px;
}
.preview-grid > div span.muted {
  display: block;
  margin-bottom: 2px;
}
.preview-path {
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px dashed var(--border-soft);
}
.preview-path .font-mono {
  margin: 4px 0;
  word-break: break-all;
}
.preview-cands {
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px dashed var(--border-soft);
}
.cand-row {
  padding: 4px 0;
}
.cand-row.picked {
  font-weight: 600;
  color: var(--success);
}
</style>
