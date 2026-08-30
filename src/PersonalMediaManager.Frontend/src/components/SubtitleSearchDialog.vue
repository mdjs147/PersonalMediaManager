<script setup>
/**
 * 字幕搜索 / 下载弹窗（Assrt 字幕源）
 *
 * 手动触发，零自动：搜索（预填原始文件名去扩展名）→ 候选列表 → 展开候选文件 → 逐文件下载。
 * 下载成功 toast 显示落地文件名并刷新「已下载」区；接口失败的错误 toast 由 unwrap 统一弹，此处不重复处理。
 */
import { computed, ref, watch } from 'vue';
import { ElMessage } from 'element-plus';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import { fmtTime, fmtSize } from '@/utils/format';

const props = defineProps({
  modelValue: { type: Boolean, default: false },
  mediaId: { type: Number, required: true },
  defaultKeyword: { type: String, default: '' },
});
const emit = defineEmits(['update:modelValue']);

const visible = ref(props.modelValue);
watch(() => props.modelValue, (v) => { visible.value = v; });
watch(visible, (v) => {
  emit('update:modelValue', v);
  if (v) onOpen();
});

const keyword = ref('');
const searching = ref(false);
const searched = ref(false);
const candidates = ref([]);

// 已下载区
const downloads = ref([]);
const downloadsLoading = ref(false);

// 文件内联展开：当前展开的 subtitleId + 各候选文件缓存（subtitleId -> { loading, items }）
const expandedId = ref(null);
const filesCache = ref({});

// 下载防重复：进行中的 `${subtitleId}:${fileIndex}`；非空时全部下载按钮禁用
const downloadingKey = ref('');

// 请求代际守门：弹窗每次打开 +1；在途异步回调写状态前比对代际，不等则丢弃。
// 场景：记录 A 的搜索 / 下载在途时关弹窗、换记录 B 重开 —— 若不守门，A 的响应会把 A 的候选写进 B 的弹窗
// （用户点下载会把 A 的字幕落到 B 的目录），A 的 finally 也会清掉 B 这一代的 loading / 防重态。
let generation = 0;

// 弹窗打开即重置状态（同一弹窗实例被不同记录复用）：代际 +1 + 预填关键词 + 拉已下载 + 自动搜一次
function onOpen() {
  generation++;
  keyword.value = props.defaultKeyword || '';
  candidates.value = [];
  searched.value = false;
  expandedId.value = null;
  filesCache.value = {};
  downloads.value = [];
  // 旧代请求的 finally 已被代际守门挡住（不会再碰新代状态），这里显式归零本代 loading / 防重态：
  // 避免「旧代搜索在途时重开」导致 searching 残留 true 把新代自动搜索吞掉、downloadingKey 残留绕过防重
  searching.value = false;
  downloadsLoading.value = false;
  downloadingKey.value = '';
  loadDownloads();
  if (keyword.value) search();
}

async function loadDownloads() {
  const gen = generation;
  downloadsLoading.value = true;
  try {
    const data = await api.subtitles.downloads(props.mediaId);
    if (gen !== generation) return; // 旧代响应，丢弃
    downloads.value = data?.items || [];
  } catch {
    if (gen !== generation) return;
    downloads.value = [];
  } finally {
    if (gen === generation) downloadsLoading.value = false;
  }
}

async function search() {
  if (searching.value) return;
  const gen = generation;
  searching.value = true;
  expandedId.value = null;
  filesCache.value = {};
  try {
    // keyword 可空：不传时后端缺省用该记录原始文件名
    const data = await api.subtitles.search(props.mediaId, { keyword: keyword.value || undefined });
    if (gen !== generation) return; // 旧代响应，丢弃（防止 A 的候选写进 B 的弹窗）
    candidates.value = data?.items || [];
    searched.value = true;
  } catch {
    // unwrap 已弹错误 toast；不置 searched，避免把「接口失败」误显示成「未找到候选」空态
    if (gen !== generation) return;
    candidates.value = [];
  } finally {
    if (gen === generation) searching.value = false;
  }
}

// 「选择文件」：展开 / 收起该候选的文件列表；首次展开才调接口，结果缓存
async function toggleFiles(c) {
  if (expandedId.value === c.subtitleId) {
    expandedId.value = null;
    return;
  }
  expandedId.value = c.subtitleId;
  const cached = filesCache.value[c.subtitleId];
  if (cached?.items || cached?.loading) return;
  const gen = generation;
  filesCache.value = { ...filesCache.value, [c.subtitleId]: { loading: true, items: null } };
  try {
    const data = await api.subtitles.files(props.mediaId, { subtitleId: c.subtitleId });
    if (gen !== generation) return; // 旧代响应，丢弃
    filesCache.value = { ...filesCache.value, [c.subtitleId]: { loading: false, items: data?.items || [] } };
  } catch {
    if (gen !== generation) return;
    filesCache.value = { ...filesCache.value, [c.subtitleId]: { loading: false, items: [] } };
  }
}

// 疑似匹配：从 defaultKeyword 提取季集，文件名命中同季集的高亮（数值比较，S1E2 与 S01E02 等价）
const seasonEpisode = computed(() => {
  const m = /S(\d{1,2})E(\d{1,3})/i.exec(props.defaultKeyword || '');
  return m ? { season: Number(m[1]), episode: Number(m[2]) } : null;
});

function isLikelyMatch(fileName) {
  if (!seasonEpisode.value) return false;
  const m = /S(\d{1,2})E(\d{1,3})/i.exec(String(fileName || ''));
  return !!m && Number(m[1]) === seasonEpisode.value.season && Number(m[2]) === seasonEpisode.value.episode;
}

async function download(c, f) {
  if (downloadingKey.value) return;
  const gen = generation;
  const key = `${c.subtitleId}:${f.index}`;
  downloadingKey.value = key;
  try {
    const data = await api.subtitles.download(props.mediaId, {
      subtitleId: c.subtitleId,
      fileIndex: f.index,
      subtitleName: c.name,
    });
    // 旧代完成回调：文件已按发起时的 mediaId 正确落地，这里只忽略 UI 更新（不弹 toast、不刷新新代的已下载区）
    if (gen !== generation) return;
    ElMessage.success(`已保存：${data?.savedFileName || '字幕文件'}`);
    loadDownloads();
  } catch {
    /* 失败 toast 由 unwrap 统一弹，无需重复处理 */
  } finally {
    // 比对代际后才清防重态：旧代的 finally 不得清掉新代正在进行的下载锁
    if (gen === generation) downloadingKey.value = '';
  }
}

// 候选 / 已下载时间显示：Assrt 返回的时间格式不保证 ISO，解析失败时原样展示兜底
function fmtMaybeTime(t) {
  if (!t) return '';
  const d = new Date(t);
  return Number.isNaN(d.getTime()) ? String(t) : fmtTime(t);
}
</script>

<template>
  <el-dialog v-model="visible" title="下载字幕" width="640px" append-to-body>
    <!-- 已下载区：无记录不显示 -->
    <div v-if="downloads.length" class="dl-done" v-loading="downloadsLoading">
      <div class="eyebrow xs dl-done-head">已下载字幕（{{ downloads.length }}）</div>
      <div v-for="d in downloads" :key="d.id" class="dl-done-row">
        <PmmIcon name="check" :size="12" :stroke="2.5" />
        <span class="font-mono dl-done-name" :title="d.fileName">{{ d.fileName }}</span>
        <span v-if="d.language" class="tag tag-success">{{ d.language }}</span>
        <span class="muted xs tabular">{{ fmtMaybeTime(d.createdAt) }}</span>
      </div>
    </div>

    <!-- 搜索行 -->
    <div class="sub-search">
      <el-input v-model="keyword" placeholder="搜索关键词（建议用发布组原始文件名）" clearable @keyup.enter="search">
        <template #prefix><PmmIcon name="search" :size="14" /></template>
      </el-input>
      <button class="btn btn-primary" :disabled="searching" @click="search">
        <PmmIcon name="search" :size="13" /> {{ searching ? '搜索中…' : '搜索' }}
      </button>
    </div>

    <!-- 候选列表 -->
    <div class="cand-wrap" v-loading="searching">
      <template v-if="candidates.length">
        <div v-for="c in candidates" :key="c.subtitleId" class="cand-block">
          <div class="card cand-card" :class="{ open: expandedId === c.subtitleId }">
            <div class="cand-meta">
              <div class="cand-title" :title="c.name">{{ c.name || '（未命名字幕）' }}</div>
              <div v-if="c.videoName" class="muted small cand-video" :title="c.videoName">{{ c.videoName }}</div>
              <div class="cand-tags">
                <span v-if="c.languageHint" class="tag tag-info">{{ c.languageHint }}</span>
                <span v-if="c.format" class="tag tag-neutral">{{ c.format }}</span>
                <span v-if="c.uploadTime" class="muted xs tabular">{{ fmtMaybeTime(c.uploadTime) }}</span>
                <span v-if="c.voteScore != null" class="muted xs">评分 {{ c.voteScore }}</span>
              </div>
            </div>
            <button class="btn btn-sm" @click="toggleFiles(c)">
              <PmmIcon :name="expandedId === c.subtitleId ? 'chevronDown' : 'chevronRight'" :size="13" />
              选择文件
            </button>
          </div>

          <!-- 该候选的文件列表（内联展开） -->
          <div v-if="expandedId === c.subtitleId" class="file-list">
            <div v-if="filesCache[c.subtitleId]?.loading" class="muted small file-hint">加载文件列表中…</div>
            <template v-else-if="filesCache[c.subtitleId]?.items?.length">
              <div
                v-for="f in filesCache[c.subtitleId].items"
                :key="f.index"
                class="file-row"
                :class="{ highlight: isLikelyMatch(f.fileName) }"
              >
                <span class="font-mono file-name" :title="f.fileName">{{ f.fileName }}</span>
                <span v-if="isLikelyMatch(f.fileName)" class="tag tag-success">疑似匹配</span>
                <span class="muted xs tabular file-size">{{ fmtSize(f.sizeBytes) }}</span>
                <button
                  class="btn btn-primary btn-sm"
                  :disabled="downloadingKey !== ''"
                  @click="download(c, f)"
                >
                  <PmmIcon name="download" :size="12" />
                  {{ downloadingKey === `${c.subtitleId}:${f.index}` ? '下载中…' : '下载' }}
                </button>
              </div>
            </template>
            <div v-else class="muted small file-hint">该候选没有可下载的字幕文件</div>
          </div>
        </div>
      </template>
      <div v-else-if="searched && !searching" class="sub-empty muted small">
        <PmmIcon name="warning" :size="14" /> 未找到候选字幕，可修改关键词重试（建议用发布组原始文件名）
      </div>
    </div>

    <template #footer>
      <button class="btn btn-ghost btn-sm" @click="visible = false">关闭</button>
    </template>
  </el-dialog>
</template>

<style scoped lang="scss">
/* 已下载区 */
.dl-done {
  margin-bottom: 14px;
  padding: 10px 12px;
  background: var(--surface-2);
  border: 1px solid var(--border-soft);
  border-radius: var(--r-2, 8px);
}
.dl-done-head { margin-bottom: 6px; }
.dl-done-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 3px 0;
  > svg { color: var(--success); flex-shrink: 0; }
}
.dl-done-name {
  flex: 1;
  min-width: 0;
  font-size: 12px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 搜索行 */
.sub-search {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 12px;
  .el-input { flex: 1; }
}

/* 候选列表 */
.cand-wrap {
  min-height: 120px;
  max-height: 480px;
  overflow: auto;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.cand-block { display: flex; flex-direction: column; }
.cand-card {
  padding: 10px 12px;
  display: grid;
  grid-template-columns: 1fr auto;
  gap: 12px;
  align-items: center;
  transition: border-color 0.15s;
  &:hover, &.open { border-color: var(--accent); }
}
.cand-meta { min-width: 0; }
.cand-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.cand-video {
  margin-top: 2px;
  font-size: 11.5px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.cand-tags {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 5px;
  flex-wrap: wrap;
}

/* 文件列表（内联展开） */
.file-list {
  margin: 4px 0 2px 14px;
  padding: 6px 10px;
  border-left: 2px solid var(--border-soft);
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.file-hint { padding: 4px 0; }
.file-row {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 5px 8px;
  border-radius: 4px;
  background: var(--surface-2);
  &.highlight {
    background: var(--success-soft);
    border: 1px solid color-mix(in oklab, var(--success) 30%, transparent);
  }
}
.file-name {
  flex: 1;
  min-width: 0;
  font-size: 12px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.file-size { white-space: nowrap; }

/* 空态 */
.sub-empty {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 24px 0;
  justify-content: center;
}

.small { font-size: 11.5px; }
.xs { font-size: 10.5px; }
</style>
