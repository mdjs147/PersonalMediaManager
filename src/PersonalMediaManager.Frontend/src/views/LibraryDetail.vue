<script setup>
/**
 * 作品详情（LibraryDetail）
 *
 * 媒体库点卡片进入：富化元数据（背景图/评分/时长/状态）+ 关联信息（演职员/制作公司/电视台/类型/标签）
 * + 分季分集（每集简介/剧照/本地映射）+ 文件记录（含实时存在性）+ 相关作品。
 * 类型/国家/语言/演职员/公司/电视台/标签 chip 可点 → 跳媒体库按该维度筛选。
 * 文件写操作（重试/重新处理/删除/整剧/批量）复用 /history/* 端点（经 useMediaRecordActions）。
 *
 * 后端契约：GET /api/library/{tmdbId}?mediaType → LibraryWorkDetailResponse
 */
import { computed, onMounted, ref, watch } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { ElMessage } from 'element-plus';
import { api } from '@/api';
import { useAuthStore } from '@/stores/auth';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPoster from '@/components/PmmPoster.vue';
import TmdbImage from '@/components/TmdbImage.vue';
import SubtitleSearchDialog from '@/components/SubtitleSearchDialog.vue';
import { useMediaRecordActions } from '@/composables/useMediaRecordActions';
import { timeAgo, fmtTime, fmtSize, stripExt, STATUS_MAP, SOURCE_MAP, COUNTRY_FLAGS } from '@/utils/format';

const props = defineProps({ tmdbId: { type: [String, Number], required: true } });
const router = useRouter();
const route = useRoute();
const auth = useAuthStore();

const loading = ref(false);
const work = ref(null);
const posterFailed = ref(false);
const related = ref([]);

const mediaType = computed(() => (route.query.type === 'movie' ? 'movie' : route.query.type || work.value?.mediaType || 'tv'));
const posterUrl = computed(() => (work.value?.hasPoster ? api.library.posterUrl(props.tmdbId) : ''));
const backdropUrl = computed(() => (work.value?.hasBackdrop ? api.library.imageUrl('w1280', work.value.backdropPath) : ''));

// ---------- 多选 ----------
const selectedIds = ref([]);
const isChecked = (id) => selectedIds.value.includes(id);
function toggleRow(id) {
  const i = selectedIds.value.indexOf(id);
  if (i >= 0) selectedIds.value.splice(i, 1);
  else selectedIds.value.push(id);
}
function clearSelection() {
  selectedIds.value = [];
}

// ---------- 分季分集（惰性） ----------
const seasonCache = ref({}); // seasonNumber -> { loading, data }
const activeSeason = ref(null);
const displaySeasons = computed(() => (work.value?.seasons || []).filter((s) => s.seasonNumber != null));

async function selectSeason(n) {
  activeSeason.value = n;
  if (seasonCache.value[n]?.data || seasonCache.value[n]?.loading) return;
  seasonCache.value[n] = { loading: true, data: null };
  try {
    const data = await api.library.season(props.tmdbId, n, { mediaType: mediaType.value });
    seasonCache.value[n] = { loading: false, data };
  } catch {
    seasonCache.value[n] = { loading: false, data: { episodes: [] } };
  }
}
const activeEpisodes = computed(() => seasonCache.value[activeSeason.value]?.data?.episodes || []);
const activeSeasonLoading = computed(() => !!seasonCache.value[activeSeason.value]?.loading);

async function load() {
  loading.value = true;
  clearSelection();
  seasonCache.value = {};
  activeSeason.value = null;
  try {
    work.value = await api.library.detail(props.tmdbId, { mediaType: mediaType.value });
    posterFailed.value = false;
    // 自动选中首个有意义的季（优先非特别篇）
    const seasons = displaySeasons.value;
    if (seasons.length) {
      const first = seasons.find((s) => s.seasonNumber > 0) || seasons[0];
      selectSeason(first.seasonNumber);
    }
    loadRelated();
  } finally {
    loading.value = false;
  }
}

async function loadRelated() {
  try {
    related.value = await api.library.related(props.tmdbId, { mediaType: mediaType.value });
  } catch {
    related.value = [];
  }
}

// 整剧删除/重投后该作品终态记录清零，详情页会变「作品不存在或无记录」——离开本页：
// 删整剧 → 回媒体库列表（作品已消失）；重处理整剧 → 去处理队列看重投进度
const actions = useMediaRecordActions({
  reload: load,
  onSeriesGone: (reason) =>
    router.push(reason === 'reprocessed' ? { name: 'Pending' } : { name: 'Library' }),
});

const files = computed(() => work.value?.files || []);
const allChecked = computed(() => files.value.length > 0 && files.value.every((r) => isChecked(r.id)));
const someChecked = computed(() => files.value.some((r) => isChecked(r.id)) && !allChecked.value);
function toggleAll() {
  if (allChecked.value) selectedIds.value = [];
  else selectedIds.value = files.value.map((r) => r.id);
}

// ---------- 按季快捷选择（剧集：一键选中某季全部文件 → 复用批量删除/重处理实现「删一季」） ----------
// 按 row.season 分组全量文件记录；season=null（解析失败未识别）归「未识别」组，仍可整组选中删除。
const fileSeasonGroups = computed(() => {
  const map = new Map(); // season:number|null -> ids[]
  for (const r of files.value) {
    const key = r.season == null ? null : r.season;
    if (!map.has(key)) map.set(key, []);
    map.get(key).push(r.id);
  }
  const groups = [...map.entries()].map(([season, ids]) => ({
    season,
    ids,
    count: ids.length,
    // 第 0 季 = 特别篇；null = 未识别（解析失败/无季号）
    label: season == null ? '未识别' : season === 0 ? '特别篇' : `第 ${season} 季`,
  }));
  // 特别篇(0)/正常季按季号升序，未识别(null) 殿后
  groups.sort((a, b) => (a.season == null ? 1 : b.season == null ? -1 : a.season - b.season));
  return groups;
});
// 仅剧集且分组 ≥2 才有「按季选」意义（单季/电影直接用整剧删或逐条勾选）
const showSeasonPicker = computed(() => mediaType.value === 'tv' && fileSeasonGroups.value.length > 1);
const isSeasonSelected = (g) => g.ids.length > 0 && g.ids.every((id) => isChecked(id));
// 切换整季：已全选则取消该季，否则并入当前选择（支持跨季组合，亦可选后再手动取消个别集）
function toggleSeasonFiles(g) {
  const set = new Set(selectedIds.value);
  const allSel = g.ids.every((id) => set.has(id));
  g.ids.forEach((id) => (allSel ? set.delete(id) : set.add(id)));
  selectedIds.value = [...set];
}

const workTitle = computed(() => work.value?.title || `TMDB ${props.tmdbId}`);
const seriesPayload = computed(() => ({
  tmdbId: Number(props.tmdbId),
  tmdbMediaType: work.value?.mediaType,
  title: workTitle.value,
}));

function se(row) {
  // 第 0 季（特别篇）/ 第 0 集（SxxE00）为合法值：用空值判断（!= null / ??），避免真值兜底把 0 显示成 1 或整体隐藏
  if (row.season == null) return '';
  let s = `S${String(row.season).padStart(2, '0')}E${String(row.episode ?? 1).padStart(2, '0')}`;
  if (row.episodeEnd && row.episodeEnd > (row.episode ?? 1)) s += `-E${String(row.episodeEnd).padStart(2, '0')}`;
  return s;
}
// 字幕下载弹窗：记录当前行，defaultKeyword 用原始文件名去扩展名（stripExt 收口在 utils/format）
const subtitleDialog = ref(false);
const subtitleRow = ref(null);

function openSubtitleDialog(row) {
  subtitleRow.value = row;
  subtitleDialog.value = true;
}

function onCommand(cmd, row) {
  if (cmd === 'subtitle') return openSubtitleDialog(row);
  if (cmd === 'rescan') return actions.rescan(row);
  if (cmd === 'undoArchive') return actions.undoArchive(row);
  if (cmd === 'reopen') return actions.reopen(row);
  if (cmd === 'reprocess') return actions.reprocess(row);
  if (cmd === 'delete') return actions.del(row);
}
function openFile(row) {
  router.push(`/media/${row.id}`);
}

// chip 跳转：按维度筛选媒体库
function goFilter(query) {
  router.push({ name: 'Library', query });
}

async function copyTmdbLink() {
  const kind = work.value?.mediaType === 'movie' ? 'movie' : 'tv';
  const url = `https://www.themoviedb.org/${kind}/${props.tmdbId}`;
  try {
    await navigator.clipboard.writeText(url);
    ElMessage.success('已复制 TMDB 链接');
  } catch {
    ElMessage.error('复制失败：浏览器拒绝访问剪贴板');
  }
}
async function refreshMetadata() {
  try {
    await api.library.refreshMetadata(props.tmdbId, { mediaType: mediaType.value });
    ElMessage.success('已刷新元数据');
    load();
  } catch {
    /* http 层已提示 */
  }
}

function epStatusTag(ep) {
  if (!ep.hasLocalFile) return null;
  if (ep.localFileExists === false) return { label: '文件缺失', variant: 'danger' };
  return STATUS_MAP[ep.localStatus] || { label: '已入库', variant: 'success' };
}

watch(() => props.tmdbId, load);
onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading" v-if="work">
    <div class="back-bar">
      <button class="btn btn-ghost btn-sm" @click="router.back()">
        <PmmIcon name="chevronLeft" :size="14" /> 返回
      </button>
      <span class="muted small">媒体库 / 作品</span>
      <div style="margin-left: auto; display: flex; gap: 6px">
        <button class="btn btn-ghost btn-sm" @click="copyTmdbLink">
          <PmmIcon name="link" :size="14" /> 复制 TMDB 链接
        </button>
        <button v-if="auth.isLoggedIn" class="btn btn-ghost btn-sm" @click="refreshMetadata">
          <PmmIcon name="refresh" :size="14" /> 刷新元数据
        </button>
        <button class="btn btn-ghost btn-sm" @click="actions.reprocessSeries(seriesPayload)">
          <PmmIcon name="refresh" :size="14" /> 重新处理整剧
        </button>
        <button class="btn btn-ghost btn-sm danger-text" @click="actions.deleteSeries(seriesPayload)">
          <PmmIcon name="trash" :size="14" /> 删除整剧
        </button>
      </div>
    </div>

    <!-- Hero -->
    <div class="card hero">
      <TmdbImage v-if="backdropUrl" :path="work.backdropPath" size="w1280" class="hero-bg-img" />
      <div v-else class="hero-bg" />
      <div class="hero-overlay" />
      <div class="hero-row">
        <img v-if="posterUrl && !posterFailed" :src="posterUrl" class="hero-poster-img" alt="" @error="posterFailed = true" />
        <PmmPoster v-else :title="work.title || '未知'" :year="work.year" :type="work.mediaType" size="lg" />
        <div class="hero-meta">
          <div class="hero-tags">
            <span class="tag tag-info">{{ work.mediaType === 'movie' ? '电影' : '剧集' }}</span>
            <span v-if="work.categoryName" class="tag tag-accent">{{ work.categoryName }}</span>
            <span v-if="work.voteAverage" class="tag rating-tag">★ {{ work.voteAverage.toFixed(1) }}</span>
            <span v-if="work.tmdbStatus" class="muted small">{{ work.tmdbStatus }}</span>
            <span v-if="work.latestArchivedAt" class="muted small">{{ timeAgo(work.latestArchivedAt) }}归档</span>
          </div>
          <h1 class="h1 hero-title">
            {{ work.title || '未知标题' }}
            <span v-if="work.year" class="muted hero-year">({{ work.year }})</span>
          </h1>
          <div v-if="work.tagline" class="muted hero-tagline">{{ work.tagline }}</div>
          <div class="muted small hero-sub">
            <span v-if="work.originalTitle">{{ work.originalTitle }} · </span>TmdbId {{ work.tmdbId }}
            <template v-if="work.originCountry?.length"> · {{ (COUNTRY_FLAGS[work.originCountry[0]] || '') + ' ' + work.originCountry[0] }}</template>
            <template v-if="work.originalLanguage"> · {{ work.originalLanguage }}</template>
            <template v-if="work.runtime"> · {{ work.runtime }} 分钟</template>
            <template v-if="work.totalSeasons"> · {{ work.totalSeasons }} 季</template>
            <template v-if="work.totalEpisodes"> · 共 {{ work.totalEpisodes }} 集</template>
          </div>

          <!-- 类型 / 国家 / 语言 chips（可点筛选） -->
          <div class="chip-row">
            <span
              v-for="g in work.genres"
              :key="`g${g.id}`"
              class="chip clickable"
              @click="goFilter({ genreId: g.id, fl: g.name })"
            >{{ g.name }}</span>
            <span
              v-for="c in work.originCountry || []"
              :key="`c${c}`"
              class="chip clickable subtle"
              @click="goFilter({ country: c, fl: c })"
            >{{ (COUNTRY_FLAGS[c] || '') + ' ' + c }}</span>
            <span
              v-if="work.originalLanguage"
              class="chip clickable subtle"
              @click="goFilter({ language: work.originalLanguage, fl: work.originalLanguage })"
            >{{ work.originalLanguage }}</span>
          </div>

          <div class="hero-stats">
            <div><div class="eyebrow xs">文件数</div><div class="font-display tabular val">{{ work.fileCount }}</div></div>
            <div><div class="eyebrow xs">已完成</div><div class="font-display tabular val">{{ work.completedCount }}</div></div>
            <div v-if="work.failedCount"><div class="eyebrow xs">失败</div><div class="font-display tabular val" style="color: var(--danger)">{{ work.failedCount }}</div></div>
            <div v-if="work.awaitingReviewCount"><div class="eyebrow xs">待确认</div><div class="font-display tabular val" style="color: var(--warning)">{{ work.awaitingReviewCount }}</div></div>
            <div><div class="eyebrow xs">总大小</div><div class="font-display tabular val">{{ fmtSize(work.totalFileSize) }}</div></div>
          </div>
          <div v-if="work.overview" class="muted small hero-plot">{{ work.overview }}</div>
        </div>
      </div>
    </div>

    <!-- 演职员 -->
    <section v-if="work.cast?.length || work.crew?.length" class="card sec">
      <h3 class="h3">演职员</h3>
      <div v-if="work.cast?.length" class="cast-row">
        <div v-for="c in work.cast" :key="`cast${c.personId}`" class="cast-card clickable" @click="goFilter({ personId: c.personId, fl: c.name })">
          <div class="cast-photo"><TmdbImage :path="c.profilePath" size="w185" :alt="c.name">{{ c.name.slice(0, 1) }}</TmdbImage></div>
          <div class="cast-name" :title="c.name">{{ c.name }}</div>
          <div v-if="c.character" class="cast-char muted" :title="c.character">{{ c.character }}</div>
        </div>
      </div>
      <div v-if="work.crew?.length" class="crew-row">
        <span
          v-for="(c, i) in work.crew"
          :key="`crew${c.personId}-${i}`"
          class="chip clickable subtle"
          @click="goFilter({ personId: c.personId, fl: c.name })"
        >{{ c.name }}<span class="muted"> · {{ c.job }}</span></span>
      </div>
    </section>

    <!-- 分季分集（剧集） -->
    <section v-if="displaySeasons.length" class="card sec">
      <h3 class="h3">分季分集</h3>
      <div class="season-tabs">
        <button
          v-for="s in displaySeasons"
          :key="`s${s.seasonNumber}`"
          class="season-tab"
          :class="{ active: activeSeason === s.seasonNumber }"
          @click="selectSeason(s.seasonNumber)"
        >
          {{ s.seasonNumber === 0 ? '特别篇' : `第 ${s.seasonNumber} 季` }}
          <span class="muted xs">{{ s.localFileCount }}/{{ s.episodeCount }}</span>
        </button>
      </div>
      <div v-if="activeSeasonLoading" class="muted small sec-pad">加载分集中…</div>
      <div v-else-if="activeEpisodes.length" class="ep-list">
        <div v-for="ep in activeEpisodes" :key="`e${ep.episodeNumber}`" class="ep-row" :class="{ absent: !ep.hasLocalFile }">
          <div class="ep-still"><TmdbImage :path="ep.stillPath" size="w300" :alt="ep.name || ''">E{{ ep.episodeNumber }}</TmdbImage></div>
          <div class="ep-body">
            <div class="ep-head">
              <span class="ep-no font-mono">E{{ String(ep.episodeNumber).padStart(2, '0') }}</span>
              <span class="ep-name">{{ ep.name || `第 ${ep.episodeNumber} 集` }}</span>
              <span v-if="epStatusTag(ep)" class="tag" :class="`tag-${epStatusTag(ep).variant}`">{{ epStatusTag(ep).label }}</span>
              <span v-else class="tag tag-neutral">未入库</span>
              <span v-if="ep.airDate" class="muted xs">{{ fmtTime(ep.airDate) }}</span>
            </div>
            <div v-if="ep.overview" class="ep-plot muted small">{{ ep.overview }}</div>
          </div>
        </div>
      </div>
      <div v-else class="muted small sec-pad">该季暂无分集信息</div>
    </section>

    <!-- 制作公司 / 电视台 -->
    <section v-if="work.companies?.length || work.networks?.length" class="card sec">
      <h3 class="h3">制作 / 出品</h3>
      <div class="logo-row">
        <span
          v-for="c in work.companies"
          :key="`co${c.id}`"
          class="logo-chip clickable"
          @click="goFilter({ companyId: c.id, fl: c.name })"
        >
          <TmdbImage v-if="c.logoPath" :path="c.logoPath" size="w185" class="logo-img" />
          <span>{{ c.name }}</span>
        </span>
        <span
          v-for="n in work.networks"
          :key="`nw${n.id}`"
          class="logo-chip clickable net"
          @click="goFilter({ networkId: n.id, fl: n.name })"
        >
          <TmdbImage v-if="n.logoPath" :path="n.logoPath" size="w185" class="logo-img" />
          <span>{{ n.name }}</span>
        </span>
      </div>
    </section>

    <!-- 标签 -->
    <section v-if="work.keywords?.length" class="card sec">
      <h3 class="h3">标签</h3>
      <div class="chip-row">
        <span
          v-for="k in work.keywords"
          :key="`k${k.id}`"
          class="chip clickable subtle"
          @click="goFilter({ keywordId: k.id, fl: k.name })"
        >{{ k.name }}</span>
      </div>
    </section>

    <!-- 文件记录 -->
    <section class="card table-card files-card">
      <header class="card-head row-head">
        <h3 class="h3">文件记录 <span class="muted">（{{ files.length }}）</span></h3>
        <div v-if="selectedIds.length" class="batch-actions">
          <span class="muted small">已选 {{ selectedIds.length }} 条</span>
          <button class="btn btn-sm" @click="actions.batchReprocess(selectedIds)"><PmmIcon name="refresh" :size="13" /> 重新处理选中</button>
          <button class="btn btn-sm danger-text" @click="actions.batchDelete(selectedIds)"><PmmIcon name="trash" :size="13" /> 删除选中</button>
          <button class="btn btn-sm" @click="clearSelection">清除</button>
        </div>
      </header>
      <div v-if="showSeasonPicker" class="season-picker">
        <span class="muted small">按季选择：</span>
        <button
          v-for="g in fileSeasonGroups"
          :key="`fsg${g.season ?? 'na'}`"
          class="chip clickable season-chip"
          :class="{ active: isSeasonSelected(g) }"
          @click="toggleSeasonFiles(g)"
        >{{ g.label }} <span class="muted xs">{{ g.count }}</span></button>
        <span class="muted xs picker-hint">点季号选中该季全部文件 → 上方「删除选中 / 重新处理选中」按季操作（仅删记录，磁盘文件保留）</span>
      </div>
      <table class="table">
        <thead>
          <tr>
            <th class="sel-col">
              <input type="checkbox" class="pmm-check" :checked="allChecked" :indeterminate.prop="someChecked" @change="toggleAll" @click.stop />
            </th>
            <th style="width: 110px">季 / 集</th>
            <th>文件名</th>
            <th>来源</th>
            <th>大小</th>
            <th>状态</th>
            <th style="width: 64px">存在</th>
            <th>归档路径</th>
            <th>处理时间</th>
            <th style="width: 60px"></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in files" :key="row.id" class="file-row" @click="openFile(row)">
            <td class="sel-col" @click.stop>
              <input type="checkbox" class="pmm-check" :checked="isChecked(row.id)" @change="toggleRow(row.id)" />
            </td>
            <td class="font-mono tabular se-cell">{{ se(row) || '—' }}</td>
            <td><div class="font-mono path-cell file-name">{{ row.fileName }}</div></td>
            <td>
              <span v-if="row.parseSource && SOURCE_MAP[row.parseSource]" class="tag" :class="`tag-${SOURCE_MAP[row.parseSource].variant}`">
                {{ SOURCE_MAP[row.parseSource].label }}
              </span>
              <span v-else class="muted small">—</span>
            </td>
            <td class="tabular muted">{{ fmtSize(row.fileSize) }}</td>
            <td>
              <span v-if="STATUS_MAP[row.status]" class="tag" :class="`tag-${STATUS_MAP[row.status].variant}`"><span class="tag-dot" />{{ STATUS_MAP[row.status].label }}</span>
              <span v-else class="tag tag-neutral">{{ row.status }}</span>
            </td>
            <td>
              <span v-if="row.fileExists === true" class="exist-ok" title="文件存在">✓</span>
              <span v-else-if="row.fileExists === false" class="exist-bad" title="文件缺失">缺失</span>
              <span v-else class="muted">—</span>
            </td>
            <td><div class="font-mono path-cell">{{ row.targetPath || '—' }}</div></td>
            <td class="muted tabular small">{{ fmtTime(row.updatedAt) }}</td>
            <td class="action-cell" @click.stop>
              <el-dropdown trigger="click" @command="(cmd) => onCommand(cmd, row)">
                <button class="icon-btn"><PmmIcon name="more" :size="16" /></button>
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item v-if="row.status === 'Failed'" command="rescan">重试（保留记录）</el-dropdown-item>
                    <el-dropdown-item v-if="row.status === 'Completed'" command="subtitle">下载字幕</el-dropdown-item>
                    <el-dropdown-item v-if="row.status === 'Completed'" command="undoArchive">撤销归档</el-dropdown-item>
                    <el-dropdown-item v-if="['Completed', 'Skipped'].includes(row.status)" command="reopen">退回重改</el-dropdown-item>
                    <el-dropdown-item command="reprocess">重新处理（重新解析）</el-dropdown-item>
                    <el-dropdown-item command="delete" divided>删除记录</el-dropdown-item>
                  </el-dropdown-menu>
                </template>
              </el-dropdown>
            </td>
          </tr>
          <tr v-if="!files.length && !loading"><td colspan="10" class="empty">该作品暂无文件记录</td></tr>
        </tbody>
      </table>
    </section>

    <!-- 相关作品 -->
    <section v-if="related.length" class="card sec">
      <h3 class="h3">相关作品</h3>
      <div class="related-rail">
        <div
          v-for="r in related"
          :key="`r${r.mediaType}-${r.tmdbId}`"
          class="related-card"
          @click="router.push({ name: 'LibraryDetail', params: { tmdbId: r.tmdbId }, query: { type: r.mediaType } })"
        >
          <PmmPoster :title="r.title || '未知'" :year="r.year" :type="r.mediaType" :src="r.hasPoster ? api.library.posterUrl(r.tmdbId) : ''" size="md" :show-text="!r.hasPoster" />
          <div class="related-title" :title="r.title || '未知'">{{ r.title || '未知' }}</div>
        </div>
      </div>
    </section>

    <!-- 字幕下载弹窗（文件记录行操作的手动触发入口） -->
    <SubtitleSearchDialog
      v-model="subtitleDialog"
      :media-id="subtitleRow?.id || 0"
      :default-keyword="stripExt(subtitleRow?.fileName)"
    />
  </div>
</template>

<style scoped lang="scss">
.back-bar { display: flex; align-items: center; gap: 10px; margin-bottom: 18px; }
.danger-text { color: var(--danger); }

.hero { padding: 0; overflow: hidden; position: relative; }
.hero-bg, .hero-bg-img {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: cover;
  opacity: 0.5;
  filter: blur(8px) saturate(130%);
  transform: scale(1.1);
}
.hero-bg { background: linear-gradient(135deg, #1f3a4d 0%, #3c6ea5 60%, #a4c4f4 130%); }
.hero-overlay { position: absolute; inset: 0; background: linear-gradient(180deg, color-mix(in oklab, var(--bg) 40%, transparent), var(--surface) 86%); }
.hero-row { position: relative; padding: 28px; display: grid; grid-template-columns: 160px 1fr; gap: 28px; align-items: flex-start; }
.hero-poster-img { width: 140px; aspect-ratio: 2 / 3; object-fit: cover; border-radius: var(--r-2); box-shadow: var(--shadow-poster); border: 1px solid var(--border-soft); display: block; flex-shrink: 0; }
.hero-meta { min-width: 0; }
.hero-tags { display: flex; align-items: center; gap: 10px; margin-bottom: 8px; flex-wrap: wrap; }
.rating-tag { background: rgba(0,0,0,0.5); color: #ffd24a; }
.hero-title { display: flex; align-items: baseline; gap: 12px; flex-wrap: wrap; }
.hero-year { font-weight: 500; font-size: 18px; }
.hero-tagline { font-style: italic; margin-top: 4px; }
.hero-sub { margin-top: 4px; }
.chip-row { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 12px; }
.hero-stats { display: flex; gap: 26px; margin-top: 18px; flex-wrap: wrap; }
.hero-stats .val { font-size: 16px; margin-top: 4px; }
.hero-plot { margin-top: 16px; line-height: 1.6; max-width: 760px; }

.sec { margin-top: 18px; padding: 20px 22px; }
.sec .h3 { margin-bottom: 14px; }
.sec-pad { padding: 8px 2px; }

.chip { padding: 3px 10px; border-radius: 999px; background: var(--surface-2); border: 1px solid var(--border); font-size: 12px; transition: background 0.12s, border-color 0.12s, color 0.12s; }
.chip.subtle { color: var(--text-mute); }
.chip.clickable, .clickable { cursor: pointer; }
.chip.clickable:hover { border-color: var(--accent); color: var(--accent); background: var(--surface-3); }

.cast-row { display: grid; grid-template-columns: repeat(auto-fill, minmax(96px, 1fr)); gap: 14px; }
.cast-card { text-align: center; }
.cast-photo { aspect-ratio: 2 / 3; border-radius: var(--r-2); overflow: hidden; border: 1px solid var(--border-soft); margin-bottom: 6px; }
.cast-name { font-size: 12px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.cast-char { font-size: 11px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.crew-row { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 14px; }

.season-tabs { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 14px; }
.season-tab { display: inline-flex; align-items: center; gap: 6px; padding: 5px 12px; border: 1px solid var(--border); border-radius: var(--r-2); background: var(--surface-2); color: var(--text-mute); cursor: pointer; font-size: 12.5px; transition: background 0.12s, border-color 0.12s, color 0.12s; }
.season-tab:hover { background: var(--surface-3); border-color: var(--border-strong); color: var(--text); }
.season-tab.active { background: var(--accent); color: var(--on-accent); border-color: var(--accent); }
.season-tab.active .muted { color: var(--on-accent); opacity: 0.7; }
.ep-list { display: flex; flex-direction: column; gap: 12px; }
.ep-row { display: grid; grid-template-columns: 150px 1fr; gap: 14px; }
.ep-row.absent { opacity: 0.62; }
.ep-still { aspect-ratio: 16 / 9; border-radius: var(--r-2); overflow: hidden; border: 1px solid var(--border-soft); background: var(--surface-1); }
.ep-head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.ep-no { color: var(--text-dim); font-size: 12px; }
.ep-name { font-weight: 600; font-size: 13.5px; }
.ep-plot { margin-top: 6px; line-height: 1.55; }

.logo-row { display: flex; flex-wrap: wrap; gap: 10px; }
.logo-chip { display: inline-flex; align-items: center; gap: 8px; padding: 6px 12px; border: 1px solid var(--border); border-radius: var(--r-2); background: var(--surface-2); font-size: 12.5px; transition: background 0.12s, border-color 0.12s; }
.logo-chip:hover { border-color: var(--accent); background: var(--surface-3); }
.logo-img { width: 28px; height: 20px; object-fit: contain; }

.files-card { margin-top: 18px; overflow: hidden; }
.row-head { justify-content: space-between; align-items: center; }
.batch-actions { display: flex; align-items: center; gap: 8px; }
.season-picker { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; padding: 2px 0 12px; }
// active 用三级选择器（含 :hover 变体）压过继承自 .chip.clickable:hover 的悬停态，避免选中季悬停时颜色回弹
.season-chip.active, .season-chip.active:hover { background: var(--accent); color: var(--on-accent); border-color: var(--accent); }
.season-chip.active .muted { color: var(--on-accent); opacity: 0.78; }
.picker-hint { margin-left: 4px; }
.file-row { cursor: pointer; }
.se-cell { font-weight: 600; }
.sel-col { width: 40px; text-align: center; }
.action-cell { text-align: right; }
.pmm-check { cursor: pointer; width: 15px; height: 15px; accent-color: var(--accent); }
.path-cell { font-size: 11.5px; color: var(--text-dim); max-width: 320px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.file-name { color: var(--text); font-size: 12.5px; }
.exist-ok { color: var(--success, #3fa45b); font-weight: 700; }
.exist-bad { color: var(--danger, #d9534f); font-weight: 600; font-size: 11px; }

.related-rail { display: grid; grid-template-columns: repeat(auto-fill, minmax(110px, 1fr)); gap: 14px; }
.related-card { cursor: pointer; transition: transform 0.12s ease; }
.related-card:hover { transform: translateY(-3px); }
.related-title { margin-top: 6px; font-size: 12px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

.small { font-size: 11px; }
.xs { font-size: 10.5px; }
.empty { text-align: center; padding: 40px 20px; color: var(--text-dim); }
</style>
