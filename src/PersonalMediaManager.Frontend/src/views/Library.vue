<script setup>
/**
 * 媒体库（Library）
 *
 * 已归档作品海报墙：后端按 (TmdbId, MediaType) 聚合（剧集多集归一卡）。
 * 支持按 类型/分类/类型(genre)/国家/语言/年份/演职员/公司/电视台/关键词 过滤 + 排序；
 * 卡片展示评分与「缺失」标记。海报走 /api/library/poster/{tmdbId}，无缓存时 PmmPoster 回退占位。
 */
import { computed, onMounted, ref, watch } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { ElMessage, ElMessageBox } from 'element-plus';
import { api } from '@/api';
import { useAuthStore } from '@/stores/auth';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPoster from '@/components/PmmPoster.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';
import OrphanRecoveryDialog from '@/components/OrphanRecoveryDialog.vue';

const router = useRouter();
const route = useRoute();
const auth = useAuthStore();

const loading = ref(false);
const items = ref([]);
const total = ref(0);
const page = ref(1);
const pageSize = ref(24);

// 筛选状态（type/keyword 在页内即时；其余维度走 URL query 以便联动与回退）
const type = ref('');
const keyword = ref('');
const sort = ref('recent');
const genreId = ref('');
const country = ref('');
const language = ref('');

const facets = ref({ genres: [], countries: [], languages: [], persons: [], companies: [], networks: [], keywords: [] });
const orphanDialog = ref(false);

// 来自详情页 chip 跳转的「钉住」维度筛选（演员/公司/电视台/关键词）
const pinned = computed(() => {
  const q = route.query;
  const out = [];
  if (q.personId) out.push({ key: 'personId', val: q.personId, label: q.fl || `演职员 #${q.personId}` });
  if (q.companyId) out.push({ key: 'companyId', val: q.companyId, label: q.fl || `公司 #${q.companyId}` });
  if (q.networkId) out.push({ key: 'networkId', val: q.networkId, label: q.fl || `电视台 #${q.networkId}` });
  if (q.keywordId) out.push({ key: 'keywordId', val: q.keywordId, label: q.fl || `标签 #${q.keywordId}` });
  return out;
});

function syncFromQuery() {
  const q = route.query;
  type.value = q.type || '';
  keyword.value = q.keyword || '';
  sort.value = q.sort || 'recent';
  genreId.value = q.genreId || '';
  country.value = q.country || '';
  language.value = q.language || '';
  page.value = Number(q.page) || 1;
}

function buildQuery(extra = {}) {
  const q = {};
  if (type.value) q.type = type.value;
  if (keyword.value.trim()) q.keyword = keyword.value.trim();
  if (sort.value && sort.value !== 'recent') q.sort = sort.value;
  if (genreId.value) q.genreId = genreId.value;
  if (country.value) q.country = country.value;
  if (language.value) q.language = language.value;
  // 保留钉住的维度
  for (const p of pinned.value) {
    q[p.key] = p.val;
    if (route.query.fl) q.fl = route.query.fl;
  }
  return { ...q, ...extra };
}

async function load() {
  loading.value = true;
  try {
    const params = {
      type: type.value || undefined,
      keyword: keyword.value.trim() || undefined,
      sort: sort.value || undefined,
      genreId: genreId.value ? Number(genreId.value) : undefined,
      country: country.value || undefined,
      language: language.value || undefined,
      personId: route.query.personId ? Number(route.query.personId) : undefined,
      companyId: route.query.companyId ? Number(route.query.companyId) : undefined,
      networkId: route.query.networkId ? Number(route.query.networkId) : undefined,
      keywordId: route.query.keywordId ? Number(route.query.keywordId) : undefined,
      page: page.value,
      pageSize: pageSize.value,
    };
    const data = await api.library.list(params);
    items.value = data?.items || [];
    total.value = data?.total || 0;
  } finally {
    loading.value = false;
  }
}

async function loadFacets() {
  try {
    facets.value = await api.library.facets();
  } catch {
    /* 忽略：筛选项不可用不影响浏览 */
  }
}

// URL query 变化（页内筛选 push / 详情 chip 跳转）统一驱动重载
watch(
  () => route.query,
  () => {
    syncFromQuery();
    load();
  },
);

function applyFilters(resetPage = true) {
  if (resetPage) page.value = 1;
  router.push({ name: 'Library', query: buildQuery({ page: page.value > 1 ? page.value : undefined }) });
}
function setType(t) {
  type.value = t;
  applyFilters();
}
function onPageChange(v) {
  page.value = v;
  router.push({ name: 'Library', query: buildQuery({ page: v > 1 ? v : undefined }) });
}
function clearPinned(key) {
  const q = { ...route.query };
  delete q[key];
  delete q.fl;
  router.push({ name: 'Library', query: q });
}
function clearAll() {
  type.value = '';
  keyword.value = '';
  sort.value = 'recent';
  genreId.value = '';
  country.value = '';
  language.value = '';
  router.push({ name: 'Library', query: {} });
}

function posterUrl(it) {
  return it.hasPoster ? api.library.posterUrl(it.tmdbId) : '';
}
function openItem(it) {
  router.push({ name: 'LibraryDetail', params: { tmdbId: it.tmdbId }, query: { type: it.mediaType } });
}

async function refreshAll() {
  try {
    const n = await api.library.refreshMetadataAll();
    ElMessage.success(`已刷新 ${n} 部作品的元数据`);
    load();
  } catch {
    /* http 层已提示 */
  }
}
async function scanExistence() {
  try {
    await ElMessageBox.confirm('对全部已归档文件做存在性检查并标注缺失？', '扫描整库存在性', { type: 'warning' });
  } catch {
    return;
  }
  try {
    const r = await api.library.scanExistence();
    ElMessage.success(`已检查 ${r.checked} 个文件，缺失 ${r.missing} 个`);
    load();
  } catch {
    /* http 层已提示 */
  }
}
async function rescanAudio() {
  try {
    await ElMessageBox.confirm(
      '对已归档但未探测过音轨的文件批量检测 av3a 等不兼容音轨并打标（仅标记不改文件）。需先在「设置 → 常规 → 音频」开启总开关并配置 ffmpeg 路径。',
      '存量音频重扫',
      { type: 'warning' },
    );
  } catch {
    return;
  }
  try {
    const r = await api.library.rescanAudio();
    ElMessage.success(`已扫描 ${r.checked} 条，成功探测 ${r.probed}，不兼容 ${r.incompatible}，跳过 ${r.skipped}`);
    load();
  } catch {
    /* http 层已提示 */
  }
}

const hasActiveFilter = computed(
  () => type.value || keyword.value || genreId.value || country.value || language.value || pinned.value.length,
);

onMounted(() => {
  syncFromQuery();
  loadFacets();
  load();
});
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader eyebrow="媒体库" :title="`${total} 部已归档作品`">
      <template #actions>
        <div v-if="auth.isLoggedIn" class="hdr-actions">
          <button class="btn btn-ghost btn-sm" @click="refreshAll" title="拉取全部作品的演职员/类型/公司等富化信息">
            <PmmIcon name="refresh" :size="14" /> 整库刷新元数据
          </button>
          <button class="btn btn-ghost btn-sm" @click="scanExistence" title="检查归档文件是否仍存在">
            <PmmIcon name="search" :size="14" /> 扫描存在性
          </button>
          <button class="btn btn-ghost btn-sm" @click="rescanAudio" title="对已归档文件批量探测 av3a 等不兼容音轨并打标（需配 ffmpeg）">
            <PmmIcon name="search" :size="14" /> 扫描不兼容音轨
          </button>
          <button class="btn btn-ghost btn-sm" @click="orphanDialog = true" title="找回「删了记录但文件还留在库里」的孤儿文件，重新登记入库">
            <PmmIcon name="folder" :size="14" /> 孤儿文件找回
          </button>
        </div>
      </template>
    </PmmPageHeader>

    <OrphanRecoveryDialog v-model="orphanDialog" @claimed="load" />

    <div class="lib-toolbar">
      <div class="seg">
        <button class="seg-btn" :class="{ active: type === '' }" @click="setType('')">全部</button>
        <button class="seg-btn" :class="{ active: type === 'movie' }" @click="setType('movie')">电影</button>
        <button class="seg-btn" :class="{ active: type === 'tv' }" @click="setType('tv')">剧集</button>
      </div>

      <div class="filters">
        <el-select v-model="sort" size="small" style="width: 116px" @change="applyFilters">
          <el-option label="最近归档" value="recent" />
          <el-option label="按年份" value="year" />
          <el-option label="按评分" value="rating" />
          <el-option label="按标题" value="title" />
        </el-select>
        <el-select v-model="genreId" size="small" clearable placeholder="类型" style="width: 120px" @change="applyFilters">
          <el-option v-for="g in facets.genres" :key="g.id" :label="`${g.name} (${g.count})`" :value="g.id" />
        </el-select>
        <el-select v-model="country" size="small" clearable placeholder="国家" style="width: 110px" @change="applyFilters">
          <el-option v-for="c in facets.countries" :key="c.id" :label="`${c.name} (${c.count})`" :value="c.id" />
        </el-select>
        <el-select v-model="language" size="small" clearable placeholder="语言" style="width: 110px" @change="applyFilters">
          <el-option v-for="l in facets.languages" :key="l.id" :label="`${l.name} (${l.count})`" :value="l.id" />
        </el-select>
      </div>

      <div class="lib-search">
        <el-input v-model="keyword" placeholder="搜索标题…" clearable size="small" @keyup.enter="applyFilters" @clear="applyFilters" />
        <button class="btn btn-sm" @click="applyFilters"><PmmIcon name="search" :size="14" /> 搜索</button>
      </div>
    </div>

    <!-- 钉住维度筛选 + 清除 -->
    <div v-if="hasActiveFilter" class="active-filters">
      <span v-for="p in pinned" :key="p.key" class="tag tag-accent filter-chip">
        {{ p.label }}
        <button class="chip-x" @click="clearPinned(p.key)">×</button>
      </span>
      <button class="btn btn-ghost btn-sm" @click="clearAll">清除全部筛选</button>
    </div>

    <div v-if="items.length" class="poster-grid">
      <div v-for="it in items" :key="`${it.mediaType}-${it.tmdbId}`" class="lib-card" @click="openItem(it)">
        <div class="poster-wrap">
          <PmmPoster
            :title="it.title || '未知'"
            :year="it.year"
            :type="it.mediaType"
            :src="posterUrl(it)"
            size="lg"
            :show-text="!it.hasPoster"
          />
          <span v-if="it.rating" class="rating-badge">★ {{ it.rating.toFixed(1) }}</span>
          <span v-if="it.missingFileCount" class="missing-badge" :title="`${it.missingFileCount} 个文件缺失`">
            缺失 {{ it.missingFileCount }}
          </span>
        </div>
        <div class="lib-meta">
          <div class="lib-title" :title="it.title || '未知'">{{ it.title || '未知' }}</div>
          <div class="muted small">
            {{ it.year || '—' }}
            <span v-if="it.mediaType === 'tv'"> · {{ it.fileCount }} 集</span>
            <span v-if="it.categoryName"> · {{ it.categoryName }}</span>
          </div>
          <div v-if="it.genres?.length" class="lib-genres">{{ it.genres.join(' · ') }}</div>
        </div>
      </div>
    </div>

    <div v-else-if="!loading" class="empty">
      <PmmIcon name="film" :size="32" style="color: var(--text-dim); margin-bottom: 8px" />
      <div>{{ hasActiveFilter ? '没有符合筛选条件的作品' : '媒体库还是空的，归档完成的作品会出现在这里' }}</div>
    </div>

    <el-pagination
      v-if="total > pageSize"
      class="pagination"
      :current-page="page"
      :page-size="pageSize"
      :total="total"
      layout="prev, pager, next, total"
      @update:current-page="onPageChange"
    />
  </div>
</template>

<style scoped lang="scss">
.hdr-actions { display: flex; gap: 6px; }
.lib-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  margin-bottom: 14px;
}
.seg {
  display: inline-flex;
  border: 1px solid var(--border-soft);
  border-radius: var(--r-2);
  overflow: hidden;
}
.seg-btn {
  padding: 6px 16px;
  background: var(--surface-1);
  border: none;
  border-right: 1px solid var(--border-soft);
  color: var(--text-dim);
  cursor: pointer;
  font-size: 13px;
}
.seg-btn:last-child { border-right: none; }
.seg-btn.active { background: var(--accent, #3b6ea5); color: #fff; }
.filters { display: flex; gap: 8px; flex-wrap: wrap; }
.lib-search { display: flex; align-items: center; gap: 8px; max-width: 300px; flex: 1; min-width: 180px; }
.active-filters { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-bottom: 14px; }
.filter-chip { display: inline-flex; align-items: center; gap: 4px; }
.chip-x { background: none; border: none; color: inherit; cursor: pointer; font-size: 14px; line-height: 1; padding: 0 2px; }
.poster-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
  gap: 18px;
}
.lib-card { cursor: pointer; transition: transform 0.12s ease; }
.lib-card:hover { transform: translateY(-4px); }
.poster-wrap { position: relative; }
.rating-badge {
  position: absolute;
  top: 6px;
  left: 6px;
  background: rgba(0, 0, 0, 0.72);
  color: #ffd24a;
  font-size: 11px;
  font-weight: 600;
  padding: 2px 6px;
  border-radius: 6px;
}
.missing-badge {
  position: absolute;
  top: 6px;
  right: 6px;
  background: var(--danger, #d9534f);
  color: #fff;
  font-size: 10.5px;
  font-weight: 600;
  padding: 2px 6px;
  border-radius: 6px;
}
.lib-meta { margin-top: 8px; }
.lib-title { font-weight: 600; font-size: 13px; line-height: 1.3; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.lib-genres {
  font-size: 10.5px;
  color: var(--text-dim);
  margin-top: 2px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.small { font-size: 11px; }
.empty { text-align: center; padding: 64px 20px; color: var(--text-dim); }
.pagination { margin-top: 20px; justify-content: center; display: flex; }
</style>
