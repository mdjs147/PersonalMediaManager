<!--
  顶栏统一全局搜索（GlobalSearch）

  一个输入框 + 下拉分组结果（历史 / 媒体库 / 待确认），每域 topN，组底「查看全部 (N)」。
  - debounce 250ms 调 api.search.query；空串不发请求、直接清空。
  - 键盘：↑/↓ 在扁平化命中列表移动高亮，Enter 跳转高亮项，Esc 关闭并失焦。
  - 跳转：历史 / 待确认命中 → /media/{id}；媒体库命中 → LibraryDetail（带 type）。
    「查看全部(历史)」→ History 列表带 keyword；「(媒体库)」→ Library 列表带 keyword。
  - 下拉**不**用 backdrop-filter:blur（项目记录模糊致卡顿），用半透明实色 + 阴影。
  - defineExpose({ focus }) 供 MainLayout 全局快捷键（Ctrl/Cmd+K、非输入态 /）聚焦。
-->
<script setup>
import { ref, computed, onBeforeUnmount, nextTick } from 'vue';
import { useRouter } from 'vue-router';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPoster from '@/components/PmmPoster.vue';
import { STATUS_MAP } from '@/utils/format';

const router = useRouter();

const keyword = ref('');
const open = ref(false);
const loading = ref(false);
const result = ref(null); // { query, history, library, review, counts }
const activeIndex = ref(-1); // 扁平命中列表的高亮下标
const inputEl = ref(null);
const rootEl = ref(null);

let debounceTimer = null;
let reqSeq = 0; // 请求序号：丢弃过期响应，避免慢响应覆盖新结果

const LIMIT_PER_GROUP = 5;

// 扁平化命中（按 历史 → 媒体库 → 待确认 顺序）供键盘上下移动统一索引
const flatHits = computed(() => {
  const r = result.value;
  if (!r) return [];
  const out = [];
  (r.history || []).forEach((h) => out.push({ kind: 'history', data: h }));
  (r.library || []).forEach((l) => out.push({ kind: 'library', data: l }));
  (r.review || []).forEach((v) => out.push({ kind: 'review', data: v }));
  return out;
});

const hasAnyHit = computed(() => flatHits.value.length > 0);
const counts = computed(() => result.value?.counts || { history: 0, library: 0, review: 0 });

function onInput() {
  const q = keyword.value.trim();
  activeIndex.value = -1;
  if (debounceTimer) clearTimeout(debounceTimer);
  if (!q) {
    // 空串：不发请求，清空结果并关闭
    result.value = null;
    open.value = false;
    loading.value = false;
    return;
  }
  open.value = true;
  loading.value = true;
  debounceTimer = setTimeout(runSearch, 250);
}

async function runSearch() {
  const q = keyword.value.trim();
  if (!q) {
    result.value = null;
    loading.value = false;
    return;
  }
  const seq = ++reqSeq;
  try {
    const data = await api.search.query({ q, limitPerGroup: LIMIT_PER_GROUP });
    if (seq !== reqSeq) return; // 过期响应丢弃
    result.value = data || null;
  } catch {
    if (seq !== reqSeq) return;
    result.value = null;
  } finally {
    if (seq === reqSeq) loading.value = false;
  }
}

// ---- 键盘导航 ----
function onKeydown(e) {
  if (e.key === 'Escape') {
    closeAndBlur();
    return;
  }
  if (e.key === 'ArrowDown') {
    e.preventDefault();
    if (!open.value) open.value = true;
    if (flatHits.value.length) activeIndex.value = (activeIndex.value + 1) % flatHits.value.length;
    return;
  }
  if (e.key === 'ArrowUp') {
    e.preventDefault();
    if (flatHits.value.length) {
      activeIndex.value = activeIndex.value <= 0 ? flatHits.value.length - 1 : activeIndex.value - 1;
    }
    return;
  }
  if (e.key === 'Enter') {
    e.preventDefault();
    if (activeIndex.value >= 0 && flatHits.value[activeIndex.value]) {
      const hit = flatHits.value[activeIndex.value];
      gotoHit(hit.kind, hit.data);
    } else if (keyword.value.trim()) {
      // 无高亮项时 Enter：跳到历史列表带 keyword（最常用的「看全部」默认）
      gotoAllHistory();
    }
  }
}

// ---- 跳转 ----
function gotoHit(kind, data) {
  if (kind === 'library') {
    router.push({ name: 'LibraryDetail', params: { tmdbId: data.tmdbId }, query: { type: data.mediaType } });
  } else {
    // history / review 都跳到该文件的处理详情
    router.push({ name: 'MediaDetail', params: { id: data.id } });
  }
  closeAfterNavigate();
}

function gotoAllHistory() {
  router.push({ name: 'History', query: { keyword: keyword.value.trim() } });
  closeAfterNavigate();
}
function gotoAllLibrary() {
  router.push({ name: 'Library', query: { keyword: keyword.value.trim() } });
  closeAfterNavigate();
}
function gotoAllReview() {
  // 待确认列表页无 keyword 过滤，仅导航过去（用户在该页自行处理）
  router.push({ name: 'Review' });
  closeAfterNavigate();
}

function closeAfterNavigate() {
  open.value = false;
  activeIndex.value = -1;
  if (inputEl.value) inputEl.value.blur();
}

// ---- 开合控制 ----
function onFocus() {
  if (keyword.value.trim() && result.value) open.value = true;
}
function onBlur() {
  // 延迟关闭，给下拉项 mousedown/click 机会先触发（否则点击命中前下拉已 unmount）
  setTimeout(() => {
    open.value = false;
    activeIndex.value = -1;
  }, 150);
}
function closeAndBlur() {
  open.value = false;
  activeIndex.value = -1;
  if (inputEl.value) inputEl.value.blur();
}
function clearKeyword() {
  keyword.value = '';
  result.value = null;
  open.value = false;
  activeIndex.value = -1;
  focus();
}

// 扁平索引 → 判断某命中是否高亮（模板里按 kind + 局部 index 反查全局索引）
function isActive(kind, localIndex) {
  const r = result.value;
  if (!r) return false;
  let base = 0;
  if (kind === 'library') base = (r.history || []).length;
  else if (kind === 'review') base = (r.history || []).length + (r.library || []).length;
  return activeIndex.value === base + localIndex;
}

function focus() {
  nextTick(() => inputEl.value?.focus());
}
defineExpose({ focus });

onBeforeUnmount(() => {
  if (debounceTimer) clearTimeout(debounceTimer);
});
</script>

<template>
  <div ref="rootEl" class="global-search">
    <div class="gs-field">
      <PmmIcon name="search" :size="15" class="gs-lead" />
      <input
        ref="inputEl"
        v-model="keyword"
        class="gs-input"
        type="text"
        placeholder="搜索历史 / 媒体库 / 待确认…"
        spellcheck="false"
        autocomplete="off"
        @input="onInput"
        @keydown="onKeydown"
        @focus="onFocus"
        @blur="onBlur"
      />
      <button v-if="keyword" class="gs-clear" type="button" title="清空" @mousedown.prevent="clearKeyword">
        <PmmIcon name="x" :size="13" />
      </button>
      <span v-else class="gs-kbd">Ctrl K</span>
    </div>

    <div v-if="open" class="gs-pop" @mousedown.prevent>
      <div v-if="loading" class="gs-state">搜索中…</div>

      <template v-else-if="hasAnyHit">
        <!-- 历史 -->
        <div v-if="result.history && result.history.length" class="gs-group">
          <div class="gs-group-head">
            <PmmIcon name="history" :size="13" /><span>处理历史</span>
          </div>
          <button
            v-for="(h, i) in result.history"
            :key="'h' + h.id"
            class="gs-row"
            :class="{ active: isActive('history', i) }"
            type="button"
            @click="gotoHit('history', h)"
          >
            <span class="gs-row-main">
              <span class="gs-row-title">{{ h.title || h.fileName }}</span>
              <span class="gs-row-sub">{{ h.fileName }}</span>
            </span>
            <span class="gs-row-meta">
              <span v-if="h.year" class="gs-year">{{ h.year }}</span>
              <span class="gs-status" :data-variant="(STATUS_MAP[h.status] || {}).variant">
                {{ (STATUS_MAP[h.status] || { label: h.status }).label }}
              </span>
            </span>
          </button>
          <button v-if="counts.history > result.history.length" class="gs-all" type="button" @click="gotoAllHistory">
            查看全部历史结果（{{ counts.history }}）
          </button>
        </div>

        <!-- 媒体库 -->
        <div v-if="result.library && result.library.length" class="gs-group">
          <div class="gs-group-head">
            <PmmIcon name="film" :size="13" /><span>媒体库</span>
          </div>
          <button
            v-for="(l, i) in result.library"
            :key="'l' + l.tmdbId + l.mediaType"
            class="gs-row gs-row-lib"
            :class="{ active: isActive('library', i) }"
            type="button"
            @click="gotoHit('library', l)"
          >
            <PmmPoster
              class="gs-poster"
              size="xs"
              :show-text="false"
              :title="l.title || String(l.tmdbId)"
              :type="l.mediaType"
              :src="l.hasPoster ? api.library.posterUrl(l.tmdbId) : ''"
            />
            <span class="gs-row-main">
              <span class="gs-row-title">{{ l.title || ('#' + l.tmdbId) }}</span>
              <span class="gs-row-sub">{{ l.mediaType === 'movie' ? '电影' : '剧集' }} · {{ l.fileCount }} 个文件</span>
            </span>
            <span class="gs-row-meta">
              <span v-if="l.year" class="gs-year">{{ l.year }}</span>
            </span>
          </button>
          <button v-if="counts.library > result.library.length" class="gs-all" type="button" @click="gotoAllLibrary">
            查看全部媒体库结果（{{ counts.library }}）
          </button>
        </div>

        <!-- 待确认 -->
        <div v-if="result.review && result.review.length" class="gs-group">
          <div class="gs-group-head">
            <PmmIcon name="inbox" :size="13" /><span>待确认队列</span>
          </div>
          <button
            v-for="(v, i) in result.review"
            :key="'v' + v.id"
            class="gs-row"
            :class="{ active: isActive('review', i) }"
            type="button"
            @click="gotoHit('review', v)"
          >
            <span class="gs-row-main">
              <span class="gs-row-title">{{ v.title || v.fileName }}</span>
              <span class="gs-row-sub">{{ v.fileName }}</span>
            </span>
            <span class="gs-row-meta">
              <span v-if="v.year" class="gs-year">{{ v.year }}</span>
            </span>
          </button>
          <button v-if="counts.review > result.review.length" class="gs-all" type="button" @click="gotoAllReview">
            前往待确认队列（共 {{ counts.review }}）
          </button>
        </div>
      </template>

      <div v-else class="gs-state">无匹配结果</div>
    </div>
  </div>
</template>

<style scoped lang="scss">
.global-search {
  position: relative;
  margin-left: 24px;
  margin-right: auto;
  width: clamp(220px, 30vw, 420px);
}

.gs-field {
  display: flex;
  align-items: center;
  gap: 8px;
  height: 34px;
  padding: 0 10px;
  background: var(--surface-2);
  border: 1px solid var(--border);
  border-radius: var(--r-2);
  transition: border-color 0.15s, background 0.15s;
}
.gs-field:focus-within {
  border-color: var(--accent);
  background: var(--surface-1);
}
.gs-lead {
  color: var(--text-dim);
  flex-shrink: 0;
}
.gs-input {
  flex: 1;
  min-width: 0;
  background: transparent;
  border: none;
  outline: none;
  color: var(--text);
  font-size: 13px;
}
.gs-input::placeholder {
  color: var(--text-dim);
}
.gs-clear {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  padding: 0;
  border: none;
  background: transparent;
  color: var(--text-dim);
  cursor: pointer;
  border-radius: var(--r-1);
}
.gs-clear:hover {
  color: var(--text);
  background: var(--surface-hover);
}
.gs-kbd {
  flex-shrink: 0;
  font-size: 10px;
  letter-spacing: 0.04em;
  color: var(--text-faint);
  border: 1px solid var(--border);
  border-radius: var(--r-1);
  padding: 1px 5px;
  white-space: nowrap;
}

/* 下拉浮层：半透明实色（无 backdrop blur），阴影 + 描边 */
.gs-pop {
  position: absolute;
  top: calc(100% + 6px);
  left: 0;
  right: 0;
  max-height: 70vh;
  overflow-y: auto;
  background: var(--surface-1);
  border: 1px solid var(--border-strong);
  border-radius: var(--r-3);
  box-shadow: var(--shadow-3);
  padding: 6px;
  z-index: 50;
}

.gs-state {
  padding: 18px 12px;
  text-align: center;
  color: var(--text-dim);
  font-size: 12.5px;
}

.gs-group + .gs-group {
  margin-top: 4px;
  border-top: 1px solid var(--border-soft);
  padding-top: 4px;
}
.gs-group-head {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px 4px;
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.03em;
  text-transform: uppercase;
  color: var(--text-dim);
}

.gs-row {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  padding: 7px 8px;
  border: none;
  background: transparent;
  border-radius: var(--r-2);
  cursor: pointer;
  text-align: left;
  color: var(--text);
}
.gs-row:hover,
.gs-row.active {
  background: var(--accent-soft);
}
.gs-row-lib {
  align-items: center;
}
.gs-poster {
  width: 28px;
  flex-shrink: 0;
}
.gs-row-main {
  display: flex;
  flex-direction: column;
  gap: 1px;
  min-width: 0;
  flex: 1;
}
.gs-row-title {
  font-size: 13px;
  font-weight: 500;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.gs-row-sub {
  font-size: 11px;
  color: var(--text-dim);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.gs-row-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
}
.gs-year {
  font-size: 11px;
  color: var(--text-mute);
}
.gs-status {
  font-size: 10px;
  padding: 1px 6px;
  border-radius: 999px;
  white-space: nowrap;
  background: var(--surface-3);
  color: var(--text-mute);
}
.gs-status[data-variant='success'] {
  background: rgba(70, 180, 110, 0.16);
  color: #5ec07e;
}
.gs-status[data-variant='warning'] {
  background: rgba(229, 160, 13, 0.16);
  color: var(--accent);
}
.gs-status[data-variant='danger'] {
  background: rgba(220, 90, 90, 0.16);
  color: #e06b6b;
}
.gs-status[data-variant='info'] {
  background: rgba(90, 140, 220, 0.16);
  color: #6f9be0;
}

.gs-all {
  width: 100%;
  margin-top: 2px;
  padding: 6px 8px;
  border: none;
  background: transparent;
  border-radius: var(--r-2);
  color: var(--accent);
  font-size: 12px;
  cursor: pointer;
  text-align: left;
}
.gs-all:hover {
  background: var(--accent-soft);
}

@media (max-width: 720px) {
  .global-search {
    width: clamp(140px, 40vw, 240px);
    margin-left: 12px;
  }
  .gs-kbd {
    display: none;
  }
}
</style>
