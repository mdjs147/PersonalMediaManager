<script setup>
/**
 * 处理历史（History）
 *
 * 定位：处理记录日志 + 失败兜底。按 UpdatedAt 倒序回看全部终态记录，支持筛选 / 分页。
 * 业务管理（重新处理 / 整剧 / 批量）已迁到「作品详情页」；本页仅保留轻量「重试 / 删除」兜底，
 * 尤其供无 TmdbId 的孤儿失败记录（在媒体库无落点）做最小管理。
 * 行点击：有 TmdbId → 作品详情（看该作品的文件记录）；否则 → 该文件的处理详情。
 */
import { computed, onMounted, ref } from 'vue';
import { useRouter } from 'vue-router';
import { api } from '@/api';
import { useAuthStore } from '@/stores/auth';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPoster from '@/components/PmmPoster.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';
import SubtitleSearchDialog from '@/components/SubtitleSearchDialog.vue';
import { useMediaRecordActions } from '@/composables/useMediaRecordActions';
import { fmtTime, fmtSize, stripExt, STATUS_MAP, SOURCE_MAP } from '@/utils/format';

const router = useRouter();

// 权限：批量 / 单条写操作仅 Admin 可见（后端全局 FallbackPolicy 同样强制 Admin，前端隐藏只是减少无效点击）。
// Viewer 角色页面保持只读，仅留「查看处理详情 / 刷新」。
const auth = useAuthStore();
const canWrite = computed(() => auth.user?.role === 'Admin');

const loading = ref(false);
const items = ref([]);
const total = ref(0);
const page = ref(1);
const pageSize = ref(20);
const filterForm = ref({ keyword: '', status: '', parseSource: '', categoryId: '' });

async function load() {
  loading.value = true;
  selected.value = new Set(); // 选择只作用于当前视图：翻页 / 改筛选 / 操作后刷新都清空，避免跨页误删
  try {
    const data = await api.history.list({
      page: page.value,
      pageSize: pageSize.value,
      ...filterForm.value,
    });
    items.value = data?.items || [];
    total.value = data?.total || 0;
  } finally {
    loading.value = false;
  }
}

const actions = useMediaRecordActions({ reload: load });

// ---- 多选 + 批量操作（选择仅限当前页；后端单批上限 500，单页 ≤100 远低于）----
const selected = ref(new Set());
const allOnPageSelected = computed(() => items.value.length > 0 && items.value.every((r) => selected.value.has(r.id)));
const someOnPageSelected = computed(() => items.value.some((r) => selected.value.has(r.id)) && !allOnPageSelected.value);

function toggleRow(id) {
  const s = new Set(selected.value);
  if (s.has(id)) s.delete(id);
  else s.add(id);
  selected.value = s;
}
function toggleAll() {
  const s = new Set(selected.value);
  if (allOnPageSelected.value) items.value.forEach((r) => s.delete(r.id));
  else items.value.forEach((r) => s.add(r.id));
  selected.value = s;
}
function clearSelection() {
  selected.value = new Set();
}
// 批量操作复用 composable（确认 → 批量接口 → 按 succeeded/failed 分级汇报 → reload，reload 内会清空选择）
async function runBatch(fn) {
  if (!selected.value.size) return;
  await fn([...selected.value]);
}

// 字幕下载弹窗：记录当前行，defaultKeyword 用原始文件名去扩展名（stripExt 收口在 utils/format）
const subtitleDialog = ref(false);
const subtitleRow = ref(null);

function openSubtitleDialog(row) {
  subtitleRow.value = row;
  subtitleDialog.value = true;
}

function onCommand(cmd, row) {
  if (cmd === 'detail') return router.push(`/media/${row.id}`);
  if (cmd === 'subtitle') return openSubtitleDialog(row);
  if (cmd === 'rescan') return actions.rescan(row);
  if (cmd === 'undoArchive') return actions.undoArchive(row);
  if (cmd === 'reopen') return actions.reopen(row);
  if (cmd === 'delete') return actions.del(row);
}

// 行点击：有 TmdbId → 作品详情（该作品的全部文件记录）；否则 → 该文件处理详情
function openRow(row) {
  if (row.tmdbId) {
    router.push({ name: 'LibraryDetail', params: { tmdbId: row.tmdbId }, query: { type: row.tmdbMediaType || 'tv' } });
  } else {
    router.push(`/media/${row.id}`);
  }
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader eyebrow="处理历史" :title="`${total} 条处理记录`">
      <template #actions>
        <button v-if="canWrite" class="btn btn-sm" @click="actions.rescanFailed" :disabled="loading">
          <PmmIcon name="refresh" :size="14" /> 重试全部失败
        </button>
        <button class="btn btn-sm" @click="load" :disabled="loading">
          <PmmIcon name="refresh" :size="14" /> 刷新
        </button>
      </template>
    </PmmPageHeader>

    <!-- 过滤条 -->
    <div class="card filter-bar">
      <div class="search-input">
        <PmmIcon name="search" :size="14" />
        <input
          v-model="filterForm.keyword"
          placeholder="搜索标题、TMDB ID、文件名…"
          @keyup.enter="page = 1; load()"
        />
      </div>
      <label class="filter-select">
        <span class="muted small">状态</span>
        <select class="select" v-model="filterForm.status" @change="page = 1; load()">
          <option value="">全部</option>
          <option value="Completed">已完成</option>
          <option value="Skipped">已跳过</option>
          <option value="Ignored">已忽略</option>
          <option value="Cancelled">已取消</option>
          <option value="Failed">失败</option>
        </select>
      </label>
      <label class="filter-select">
        <span class="muted small">来源</span>
        <select class="select" v-model="filterForm.parseSource" @change="page = 1; load()">
          <option value="">全部</option>
          <option value="Rule">规则</option>
          <option value="Ai">AI</option>
          <option value="Hybrid">混合</option>
          <option value="Manual">强制匹配</option>
        </select>
      </label>
      <button class="btn btn-primary btn-sm" @click="page = 1; load()">
        <PmmIcon name="search" :size="13" /> 查询
      </button>
    </div>

    <!-- 批量操作栏（仅 Admin、且有勾选时出现）：作用于当前页已勾选的记录 -->
    <div v-if="canWrite && selected.size" class="card batch-bar">
      <span class="batch-count">已选 <b>{{ selected.size }}</b> 条</span>
      <span class="muted small">批量操作仅作用于已勾选记录</span>
      <div class="spacer" />
      <button class="btn btn-sm" @click="runBatch(actions.batchReopen)" :disabled="loading">
        <PmmIcon name="edit" :size="13" /> 批量退回重改
      </button>
      <button class="btn btn-sm" @click="runBatch(actions.batchUndoArchive)" :disabled="loading">
        <PmmIcon name="archive" :size="13" /> 批量撤销归档
      </button>
      <button class="btn btn-sm" @click="runBatch(actions.batchReprocess)" :disabled="loading">
        <PmmIcon name="refresh" :size="13" /> 批量重新处理
      </button>
      <button class="btn btn-danger btn-sm" @click="runBatch(actions.batchDelete)" :disabled="loading">
        <PmmIcon name="trash" :size="13" /> 批量删除
      </button>
      <button class="btn btn-ghost btn-sm" @click="clearSelection">取消选择</button>
    </div>

    <!-- 表格 -->
    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th v-if="canWrite" class="sel-col" @click.stop>
              <el-checkbox
                :model-value="allOnPageSelected"
                :indeterminate="someOnPageSelected"
                @change="toggleAll"
              />
            </th>
            <th style="width: 50px"></th>
            <th>媒体</th>
            <th>来源</th>
            <th>大小</th>
            <th>状态</th>
            <th>归档路径</th>
            <th>处理时间</th>
            <th style="width: 60px"></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in items" :key="row.id" class="hist-row" @click="openRow(row)">
            <td v-if="canWrite" class="sel-col" @click.stop>
              <el-checkbox
                :model-value="selected.has(row.id)"
                @change="() => toggleRow(row.id)"
              />
            </td>
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
            <td class="tabular muted">{{ fmtSize(row.fileSize) }}</td>
            <td>
              <span
                v-if="STATUS_MAP[row.status]"
                class="tag"
                :class="`tag-${STATUS_MAP[row.status].variant}`"
              >
                <span class="tag-dot" />{{ STATUS_MAP[row.status].label }}
              </span>
              <span v-else class="tag tag-neutral">{{ row.status }}</span>
              <!-- 待补元数据徽标（归档降级容错 7892db6 的列表透出）：后端按最后一条 Archiving 时间线步骤判定。
                   防御性真值访问：schema.d.ts 由主会话统一重生，旧类型上 metadataPending 可能不存在，缺字段时按 falsy 不渲染 -->
              <el-tooltip
                v-if="row.metadataPending"
                content="视频已归档，但部分元数据（nfo / 海报）未写入，可在详情页查看明细"
                placement="top"
              >
                <el-tag type="warning" size="small" class="meta-pending-tag">待补元数据</el-tag>
              </el-tooltip>
              <!-- 不兼容音轨徽标（av3a Audio Vivid 等，Plex 多数客户端无法解码）：后端 HasIncompatibleAudio 字段透出。
                   防御性真值访问：schema.d.ts 由主会话统一重生，旧类型上 hasIncompatibleAudio 可能不存在，缺字段时按 falsy 不渲染 -->
              <el-tooltip
                v-if="row.hasIncompatibleAudio"
                content="含不兼容音轨（如 av3a 菁彩声），Plex 等多数客户端无法解码音频，建议重混丢轨或更换片源"
                placement="top"
              >
                <el-tag type="danger" size="small" class="meta-pending-tag">不兼容音轨</el-tag>
              </el-tooltip>
            </td>
            <td>
              <div class="font-mono path-cell">{{ row.targetPath || '—' }}</div>
            </td>
            <td class="muted tabular small">{{ fmtTime(row.updatedAt) }}</td>
            <td class="action-cell" @click.stop>
              <el-dropdown trigger="click" @command="(cmd) => onCommand(cmd, row)">
                <button class="icon-btn"><PmmIcon name="more" :size="16" /></button>
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item command="detail">查看处理详情</el-dropdown-item>
                    <el-dropdown-item v-if="canWrite && row.status === 'Completed'" command="subtitle">下载字幕</el-dropdown-item>
                    <el-dropdown-item v-if="canWrite && row.status === 'Failed'" command="rescan" divided>
                      重试（保留记录）
                    </el-dropdown-item>
                    <el-dropdown-item v-if="canWrite && row.status === 'Completed'" command="undoArchive" divided>
                      撤销归档
                    </el-dropdown-item>
                    <el-dropdown-item v-if="canWrite && ['Completed', 'Skipped'].includes(row.status)" command="reopen" :divided="row.status === 'Skipped'">
                      退回重改
                    </el-dropdown-item>
                    <el-dropdown-item v-if="canWrite" command="delete" :divided="row.status !== 'Failed' && row.status !== 'Completed' && row.status !== 'Skipped'">
                      删除记录
                    </el-dropdown-item>
                  </el-dropdown-menu>
                </template>
              </el-dropdown>
            </td>
          </tr>
          <tr v-if="!items.length && !loading">
            <td :colspan="canWrite ? 9 : 8" class="empty">暂无记录</td>
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
      @update:current-page="(v) => { page = v; load(); }"
      @update:page-size="(v) => { pageSize = v; load(); }"
    />

    <!-- 字幕下载弹窗（已归档记录的手动触发入口） -->
    <SubtitleSearchDialog
      v-model="subtitleDialog"
      :media-id="subtitleRow?.id || 0"
      :default-keyword="stripExt(subtitleRow?.fileName)"
    />
  </div>
</template>

<style scoped lang="scss">
.batch-bar {
  padding: 10px 14px;
  margin-bottom: 16px;
  display: flex;
  align-items: center;
  gap: 12px;
}
.batch-bar .spacer { flex: 1; }
.batch-count { font-size: 13px; color: var(--text); }
.sel-col { width: 40px; text-align: center; }
.filter-bar {
  padding: 14px;
  margin-bottom: 16px;
  display: grid;
  grid-template-columns: 1fr auto auto auto;
  gap: 12px;
  align-items: center;
}
.search-input {
  display: flex;
  align-items: center;
  gap: 8px;
  background: var(--surface-2);
  border: 1px solid var(--border);
  border-radius: var(--r-2);
  padding: 6px 10px;
  color: var(--text-mute);
}
.search-input input {
  background: transparent;
  border: 0;
  outline: 0;
  flex: 1;
  font: inherit;
  font-size: 13px;
  color: var(--text);
}
.search-input input::placeholder { color: var(--text-dim); }
.filter-select {
  display: flex;
  align-items: center;
  gap: 8px;
}
.filter-select .select {
  width: auto;
  padding: 6px 10px;
}
.small { font-size: 11px; }
.table-card { overflow: hidden; }
.hist-row { cursor: pointer; }
/* 待补元数据徽标与状态标签同列并排，留出小间距 */
.meta-pending-tag { margin-left: 6px; }
.s-e { margin-left: 8px; font-weight: 500; }
.action-cell { text-align: right; }
.path-cell {
  font-size: 11.5px;
  color: var(--text-dim);
  max-width: 380px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  margin-top: 2px;
}
.pagination {
  margin-top: 14px;
  justify-content: flex-end;
  display: flex;
}
</style>
