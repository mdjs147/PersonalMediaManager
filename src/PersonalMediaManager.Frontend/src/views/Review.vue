<script setup>
/**
 * 待确认队列（Review）— 分组流水线版
 *
 * 取代旧「列表 + 单条抽屉 + 两条批量（列表盲批 / 抽屉兄弟）」：
 * - 工作单元 = 作品组：剧集按「同目录 +（同首候选 tmdbId / 同解析标题）」聚为一组；电影 / 单文件各自成组。
 *   一次定作品 + 分类，组内逐集核对季 / 集后整组确认 —— 批量天然含每集理解，杜绝盲批偏差。
 * - 组处理面固定决策顺序：①作品（候选单选，搜索 / 填 ID 折叠）→ ②文件 / 剧集（逐行季集可改 + 去向预览）
 *   → ③分类（按媒体类型过滤，chips）→ 底部 sticky 确认（实时去向）。媒体类型由所选候选派生，无独立单选。
 *
 * 后端契约（与 Application.Dtos.Review 对齐）：
 * - GET  /api/review                  → ReviewListPage
 * - POST /api/review/batch-confirm    → BatchConfirmRequest（整组逐集确认；单文件组即 1 项，按 5 分片带进度）
 * - POST /api/review/batch-ignore     → BatchIgnoreRequest（整组 / 列表多选忽略）
 * - GET  /api/review/{id}/tmdb-search → 关键词重搜（零候选兜底）
 * - GET  /api/review/{id}/tmdb-detail → 按 ID 取详情（季数 + 逐季集数，供绝对集号换算）
 * - POST /api/review/preview-paths    → 去向预览（只读，逐项算 Plex 落点）
 *
 * parsedInfo 是 JSON 字符串，统一经 parseInfo() 解析为 { type, title, year, season, episode, episodeEnd }。
 */
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { ElMessage, ElMessageBox, ElTag, ElSelect, ElOption, ElInput, ElInputNumber } from 'element-plus';

import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPoster from '@/components/PmmPoster.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';
import { SOURCE_MAP, timeAgo, fmtTime } from '@/utils/format';

const loading = ref(false);
const allItems = ref([]); // 后端原始 ReviewItemResponse[]
const categories = ref([]); // 分类下拉数据

// 过滤条件
const filterName = ref('');
const filterReasons = ref([]);
const filterSources = ref([]);

// 列表多选（item id 集合，供批量忽略；按组勾选时展开为组内全部 id）
const selected = ref(new Set());

const submitting = ref(false);

// 批量归档进度遮罩：confirm 同步归档（逐条移动文件 + 查 TMDB），分片实时推进进度条避免「点了没反应」误判
const batchProgress = ref({ visible: false, done: 0, total: 0 });
const batchProgressPercent = computed(() =>
  batchProgress.value.total > 0
    ? Math.round((batchProgress.value.done / batchProgress.value.total) * 100)
    : 0);

/** 入队原因 → 标签元数据（label + ElTag type） */
const REASON_VARIANT = {
  TmdbMultiCandidate: { label: 'TMDB 多候选', type: 'warning' },
  TmdbZeroResult: { label: 'TMDB 零结果', type: 'danger' },
  AiLowConfidence: { label: 'AI 低置信度', type: 'info' },
  CategoryUnresolved: { label: '分类未定', type: 'warning' },
  NameCollision: { label: '名称冲突', type: 'danger' },
  ParseIncomplete: { label: '解析不全', type: 'warning' },
  ManualReopen: { label: '人工退回重改', type: 'info' },
  HoldBeforeArchive: { label: '归档前拦截', type: 'info' },
};
const REASON_OPTIONS = Object.entries(REASON_VARIANT).map(([k, v]) => ({ value: k, label: v.label }));
const SOURCE_OPTIONS = Object.entries(SOURCE_MAP).map(([k, v]) => ({ value: k, label: v.label }));

// ---------- parsedInfo / 媒体类型 工具 ----------

/** 解析 parsedInfo JSON 字符串为统一结构 */
function parseInfo(raw) {
  if (!raw) return { type: null, title: null, year: null, season: null, episode: null, episodeEnd: null, seasonTitle: null };
  try {
    const o = typeof raw === 'string' ? JSON.parse(raw) : raw;
    return {
      type: o?.type ?? o?.Type ?? null,
      title: o?.title ?? o?.Title ?? null,
      year: o?.year ?? o?.Year ?? null,
      season: o?.season ?? o?.Season ?? null,
      episode: o?.episode ?? o?.Episode ?? null,
      episodeEnd: o?.episodeEnd ?? o?.EpisodeEnd ?? null,
      seasonTitle: o?.seasonTitle ?? o?.SeasonTitle ?? null,
    };
  } catch {
    return { type: null, title: null, year: null, season: null, episode: null, episodeEnd: null, seasonTitle: null };
  }
}

/** 候选优先推断默认 mediaType（候选 → parsedInfo.type → 默认电影），统一返回 'Movie' | 'Tv' */
function inferMediaType(item) {
  const m = item?.tmdbCandidates?.[0]?.mediaType;
  if (m) return normalizeMediaType(m);
  const t = parseInfo(item?.parsedInfo).type;
  if (t === 'tv' || t === 'Tv') return 'Tv';
  return 'Movie';
}

/** 归一 mediaType 为 'Movie' | 'Tv'（后端候选 / 搜索结果可能返回小写 movie/tv） */
function normalizeMediaType(m) {
  return String(m || '').toLowerCase() === 'tv' ? 'Tv' : 'Movie';
}

/** 文件名去扩展名（保留主名）；无扩展名原样返回 */
function fileStem(name) {
  if (!name) return '';
  const i = name.lastIndexOf('.');
  return i > 0 ? name.slice(0, i) : name;
}

/** 取文件扩展名（含点，小写显示原样保留）；无扩展名返回空串。多重后缀取末段（.mp4.bt.xltd → .xltd） */
function fileExt(name) {
  if (!name) return '';
  const i = name.lastIndexOf('.');
  return i > 0 ? name.slice(i) : '';
}

/** 该扩展名是否受支持视频格式（与后端 MediaFileExtensions 白名单一致；非视频在列表/详情高亮，便于识别下载残留） */
const VIDEO_EXTS = new Set(['.mkv', '.mp4', '.avi', '.mov', '.wmv', '.flv', '.webm', '.m4v', '.ts', '.mpg', '.mpeg', '.m2ts']);
function isVideoExt(name) {
  const e = fileExt(name).toLowerCase();
  return e !== '' && VIDEO_EXTS.has(e);
}

// ---------- 列表加载 + 过滤 ----------

async function load() {
  loading.value = true;
  try {
    const [page, cats] = await Promise.all([
      api.review.list({ page: 1, pageSize: 100 }),
      categories.value.length ? Promise.resolve(categories.value) : api.categories.list(),
    ]);
    allItems.value = page?.items || [];
    if (Array.isArray(cats)) categories.value = cats;
  } finally {
    loading.value = false;
  }
}

/** 前端过滤：fileName 关键字 + reason 多选 + source 多选 */
const filteredItems = computed(() => {
  let list = allItems.value;
  const kw = filterName.value.trim().toLowerCase();
  if (kw) list = list.filter((i) => (i.fileName || '').toLowerCase().includes(kw));
  if (filterReasons.value.length) list = list.filter((i) => filterReasons.value.includes(i.reason));
  if (filterSources.value.length) list = list.filter((i) => filterSources.value.includes(i.parseSource));
  return list;
});
const filteredCount = computed(() => filteredItems.value.length);

// ---------- 分组 ----------

/** 取父目录（兼容 Windows \ 与 POSIX /） */
function dirOf(p) {
  if (!p) return '';
  const idx = Math.max(p.lastIndexOf('\\'), p.lastIndexOf('/'));
  return idx >= 0 ? p.slice(0, idx) : '';
}

/** 是否「剧集向」：有集号 / 明确 tv 纳入；明确 movie 排除；类型未知也按剧集向（同目录大概率同剧集） */
function looksTv(it) {
  const info = parseInfo(it.parsedInfo);
  if (info.episode != null) return true;
  if (info.type === 'tv' || info.type === 'Tv') return true;
  if (info.type === 'movie' || info.type === 'Movie') return false;
  return true;
}

/**
 * 组键：电影 / 单文件各自成组（避免多版本同名归档冲突）；剧集按「所在目录」聚合。
 * 同目录大概率同剧（与后端文件夹级 series 复用的 folderKey 同一假设），同剧一锅端的整季落同一组。
 *
 * 仅按目录、不再附加 tmdbId / 标题：旧实现第三段在「数字 tmdbId / 字符串 title / itemId」间漂移，
 * 同目录下「已查到 TMDB」与「解析失败（TMDB 零结果）」的兄弟文件键不同 → 同一合集被裂成两组。
 * 目录缺失（手动单文件等无目录上下文）时回退到 itemId 各自成组，避免空目录把无关文件混聚一锅。
 */
function groupKey(it) {
  if (!looksTv(it)) return `m|${it.id}`;
  const dir = dirOf(it.sourcePath || it.fileName || '');
  return dir ? `t|${dir}` : `t|${it.id}`;
}

/** 组内排序：季 → 集 → 文件名 */
function sortBySE(a, b) {
  const ia = parseInfo(a.parsedInfo);
  const ib = parseInfo(b.parsedInfo);
  return (ia.season ?? 9e4) - (ib.season ?? 9e4)
    || (ia.episode ?? 9e4) - (ib.episode ?? 9e4)
    || (a.fileName || '').localeCompare(b.fileName || '');
}

const groups = computed(() => {
  const map = new Map();
  for (const it of filteredItems.value) {
    const k = groupKey(it);
    if (!map.has(k)) map.set(k, []);
    map.get(k).push(it);
  }
  const out = [];
  for (const [key, raw] of map) {
    const items = raw.slice().sort(sortBySE);
    const first = items[0];
    const info = parseInfo(first.parsedInfo);
    const top = first.tmdbCandidates?.[0] || null;
    const reasons = [...new Set(items.map((i) => i.reason))].filter((r) => r && REASON_VARIANT[r]);
    const sources = [...new Set(items.map((i) => i.parseSource))].filter((s) => s && SOURCE_MAP[s]);
    const isTv = inferMediaType(first) === 'Tv' || items.some((i) => parseInfo(i.parsedInfo).episode != null);
    const exts = [...new Set(items.map((i) => fileExt(i.fileName)).filter(Boolean))];
    const hasNonVideo = items.some((i) => !isVideoExt(i.fileName));
    // 组内已解析出的季号集合（升序去重）：列表行据此区分 S01 / S02；缺季号的组不显示季徽标
    const seasons = [...new Set(items.map((i) => parseInfo(i.parsedInfo).season).filter((s) => s != null))].sort((a, b) => a - b);
    const seasonLabel = seasons.length === 0
      ? null
      : seasons.length === 1
        ? `S${String(seasons[0]).padStart(2, '0')}`
        : `S${String(seasons[0]).padStart(2, '0')}-S${String(seasons[seasons.length - 1]).padStart(2, '0')}`;
    // 篇章季标题（如「锻刀村篇」）：以篇章名标识季的番剧，列表 / 详情显示出来供人工对照 TMDB 季名选季
    const seasonTitles = [...new Set(items.map((i) => parseInfo(i.parsedInfo).seasonTitle).filter(Boolean))];
    // TMDB 未收录候补：零结果且未动用 AI（规则高置信多查询全零）的记录由后端每日自动重投，收录后自动归档
    const autoRetry = items.some((i) => i.reason === 'TmdbZeroResult' && !i.aiInvolved);
    out.push({
      key,
      items,
      count: items.length,
      isTv,
      seasonLabel,
      seasonTitles,
      title: info.title || top?.title || first.fileName,
      year: info.year ?? top?.year ?? null,
      dir: dirOf(first.sourcePath || first.fileName || ''),
      reasons,
      sources,
      exts,
      hasNonVideo,
      autoRetry,
      candCount: first.tmdbCandidates?.length || 0,
      topCandidate: top,
      maxId: Math.max(...items.map((i) => i.id)),
      createdAt: first.createdAt,
    });
  }
  out.sort((a, b) => b.maxId - a.maxId);
  return out;
});

// ---------- 列表多选（批量忽略用） ----------

const allSelected = computed(() => filteredCount.value > 0 && filteredItems.value.every((i) => selected.value.has(i.id)));
const someSelected = computed(() => !allSelected.value && filteredItems.value.some((i) => selected.value.has(i.id)));
function toggleSelectAll() {
  const next = new Set(selected.value);
  if (allSelected.value) for (const i of filteredItems.value) next.delete(i.id);
  else for (const i of filteredItems.value) next.add(i.id);
  selected.value = next;
}
function groupAllChecked(g) { return g.items.every((i) => selected.value.has(i.id)); }
function groupSomeChecked(g) { return !groupAllChecked(g) && g.items.some((i) => selected.value.has(i.id)); }
function toggleGroupSel(g) {
  const next = new Set(selected.value);
  if (groupAllChecked(g)) for (const i of g.items) next.delete(i.id);
  else for (const i of g.items) next.add(i.id);
  selected.value = next;
}
function clearSelection() { selected.value = new Set(); }
const selectedItems = computed(() => allItems.value.filter((i) => selected.value.has(i.id)));

/** 列表批量忽略（批量确认已被分组处理取代） */
async function onBatchIgnore() {
  if (!selected.value.size) return ElMessage.warning('请先勾选条目');
  let reason;
  try {
    const r = await ElMessageBox.prompt(
      `批量忽略 ${selected.value.size} 个文件？文件留在原位不再处理。`,
      '批量忽略',
      { confirmButtonText: '忽略', cancelButtonText: '取消', type: 'warning', inputPlaceholder: '原因（可空，将记录到日志）', inputValue: '' });
    reason = r?.value || null;
  } catch {
    return;
  }
  const items = selectedItems.value.map((row) => ({ id: row.id, rowVersion: row.rowVersion }));
  submitting.value = true;
  try {
    const res = await api.review.batchIgnore({ items, reason });
    const ok = res?.succeeded?.length || 0;
    const fail = res?.failed?.length || 0;
    if (fail) ElMessage.warning(`批量忽略完成：成功 ${ok} 条 / 失败 ${fail} 条`);
    else ElMessage.success(`已忽略 ${ok} 条`);
    clearSelection();
    await load();
  } finally {
    submitting.value = false;
  }
}

// ---------- 组处理面 ----------

const openGroup = ref(null);
const groupForm = ref({ tmdbId: null, mediaType: 'Movie', categoryId: null });
const groupCandidates = ref([]); // 组内去重并集候选（可被搜索 / 填 ID 灌入）
const episodeEdits = ref({}); // { [itemId]: { checked, season, episode, episodeEnd } }

// 搜索 / 填 ID（默认折叠）
const searchPanelOpen = ref(false);
const searchForm = ref({ query: '', type: 'both', year: null });
const searchResults = ref([]);
const searching = ref(false);
const searched = ref(false);
const idForm = ref({ tmdbId: null, type: 'movie' });
const idQuerying = ref(false);

// 季数 / 逐季集数缓存（按 tmdbId 懒查复用）
const seasonDetailCache = ref({});
const seasonEpisodeCache = ref({});
const seasonFetchInFlight = new Set();

// 去向预览 { [String(itemId)]: { relativePath, fullPath, error } }
const previewMap = ref({});
let previewTimer = null;

// 批量套用季号
const bulkSeason = ref(null);

function anyItemId() { return openGroup.value?.items?.[0]?.id ?? null; }

/** 匹配某媒体类型的分类（含 Both 通用分类） */
function categoriesForType(mt) {
  const want = mt === 'Tv' ? 'Tv' : 'Movie';
  return categories.value.filter((c) => c.mediaType === want || c.mediaType === 'Both');
}
/** 该类型仅一个分类时自动选中，否则 null 等用户选 */
function defaultCategoryId(mt) {
  const opts = categoriesForType(mt);
  return opts.length === 1 ? opts[0].id : null;
}

function openGroupDrawer(g) {
  openGroup.value = g;
  const first = g.items[0];
  const info = parseInfo(first.parsedInfo);
  const mt = inferMediaType(first);

  // 去重并集候选（拷贝一份避免直接改原始 item）
  const seen = new Map();
  for (const it of g.items) for (const c of (it.tmdbCandidates || [])) if (!seen.has(c.tmdbId)) seen.set(c.tmdbId, { ...c });
  groupCandidates.value = [...seen.values()];

  groupForm.value = {
    tmdbId: groupCandidates.value[0]?.tmdbId ?? null,
    mediaType: mt,
    categoryId: defaultCategoryId(mt),
  };

  const edits = {};
  for (const it of g.items) {
    const inf = parseInfo(it.parsedInfo);
    edits[it.id] = { checked: true, season: inf.season ?? null, episode: inf.episode ?? null, episodeEnd: inf.episodeEnd ?? null };
  }
  episodeEdits.value = edits;

  searchPanelOpen.value = false;
  searchForm.value = { query: info.title || '', type: 'both', year: info.year ?? null };
  searchResults.value = [];
  searched.value = false;
  idForm.value = { tmdbId: null, type: mt === 'Tv' ? 'tv' : 'movie' };
  bulkSeason.value = null;
  sortKey.value = null;
  sortAsc.value = true;
  nameColWidth.value = null;
  previewMap.value = {};

  if (groupForm.value.mediaType === 'Tv') ensureSeasonDetail();
  schedulePreview();
}

function closeGroup() {
  openGroup.value = null;
  if (previewTimer) { clearTimeout(previewTimer); previewTimer = null; }
}

const openGroupReasons = computed(() =>
  [...new Set((openGroup.value?.items || []).map((i) => i.reason))].filter((r) => r && REASON_VARIANT[r]));

/** 选定候选 → 同步媒体类型 */
function pickCandidate(c) {
  groupForm.value.tmdbId = c.tmdbId;
  groupForm.value.mediaType = normalizeMediaType(c.mediaType);
}

const selectedCandidate = computed(() =>
  groupCandidates.value.find((c) => c.tmdbId === groupForm.value.tmdbId) ?? null);

// ---------- 季数 / 绝对集号换算 ----------

const selectedSeasonCount = computed(() => {
  const cand = selectedCandidate.value;
  if (cand?.totalSeasons != null) return cand.totalSeasons;
  const id = groupForm.value.tmdbId;
  if (id != null) return seasonDetailCache.value[`tv-${id}`] ?? null;
  return null;
});

/** 季名后缀：过滤无意义的默认季名（「第N季」「Season N」），仅显示有信息量的季名 / 篇章名供对照 */
function seasonNameSuffix(s) {
  const nm = (s?.name || '').trim();
  if (!nm || /^第?\s*\d+\s*季$/.test(nm) || /^season\s*\d+$/i.test(nm)) return '';
  return ` · ${nm}`;
}

const seasonOptions = computed(() => {
  const id = groupForm.value.tmdbId;
  const cached = id != null ? seasonEpisodeCache.value[`tv-${id}`] : null;
  // 有逐季数据（含季名）→ 季号 + 季名，方便拿篇章名对照选季；否则回退「第 N 季」纯数字
  if (cached && cached.length) {
    return cached
      .slice()
      .sort((a, b) => a.seasonNumber - b.seasonNumber)
      .map((s) => ({
        value: s.seasonNumber,
        label: (s.seasonNumber === 0 ? '特别篇（季 0）' : `第 ${s.seasonNumber} 季`) + seasonNameSuffix(s),
      }));
  }
  const n = selectedSeasonCount.value;
  const opts = [];
  if (n != null && n >= 1) {
    opts.push({ value: 0, label: '特别篇（季 0）' });
    for (let i = 1; i <= n; i += 1) opts.push({ value: i, label: `第 ${i} 季` });
  }
  return opts;
});
const seasonSelectReady = computed(() => selectedSeasonCount.value != null && selectedSeasonCount.value >= 1);

const seasonHint = computed(() => {
  if (groupForm.value.mediaType !== 'Tv') return '';
  const n = selectedSeasonCount.value;
  if (n == null) return '剧集需填季 + 集；动漫常无季号时填季 1 + 集号。';
  if (n === 1) return '该剧 TMDB 仅 1 季，缺季号的文件已默认第 1 季。';
  return `该剧 TMDB 共 ${n} 季，用「统一季号」或「按绝对集号分季」批量补季。`;
});

/** 懒查所选剧集季数（候选未带 TotalSeasons 时）：写回候选 + 缓存，失败静默 */
async function ensureSeasonDetail() {
  if (groupForm.value.mediaType !== 'Tv') return;
  const id = groupForm.value.tmdbId;
  const itemId = anyItemId();
  if (id == null || itemId == null) return;
  const cand = selectedCandidate.value;
  if (cand?.totalSeasons != null) return;
  const key = `tv-${id}`;
  if (key in seasonDetailCache.value) {
    if (cand) cand.totalSeasons = seasonDetailCache.value[key];
    return;
  }
  if (seasonFetchInFlight.has(key)) return;
  seasonFetchInFlight.add(key);
  try {
    const d = await api.review.tmdbDetail(itemId, { tmdbId: id, mediaType: 'tv' });
    const ts = d?.totalSeasons ?? null;
    seasonDetailCache.value = { ...seasonDetailCache.value, [key]: ts };
    seasonEpisodeCache.value = { ...seasonEpisodeCache.value, [key]: d?.seasons ?? [] };
    if (cand) cand.totalSeasons = ts;
  } catch { /* 取季数失败：静默 */ } finally {
    seasonFetchInFlight.delete(key);
  }
}

/** 懒查并返回当前剧集逐季集数（绝对集号换算用） */
async function ensureSeasonEpisodeCounts() {
  if (groupForm.value.mediaType !== 'Tv') return [];
  const id = groupForm.value.tmdbId;
  const itemId = anyItemId();
  if (id == null || itemId == null) return [];
  const key = `tv-${id}`;
  if (key in seasonEpisodeCache.value) return seasonEpisodeCache.value[key];
  try {
    const d = await api.review.tmdbDetail(itemId, { tmdbId: id, mediaType: 'tv' });
    const seasons = d?.seasons ?? [];
    seasonEpisodeCache.value = { ...seasonEpisodeCache.value, [key]: seasons };
    if (!(key in seasonDetailCache.value)) {
      seasonDetailCache.value = { ...seasonDetailCache.value, [key]: d?.totalSeasons ?? null };
    }
    return seasons;
  } catch {
    return [];
  }
}

/** 正片季逐季集数（排除特别篇季 0 与未播 0 集季，按季号升序） */
const convertSeasons = computed(() => {
  const id = groupForm.value.tmdbId;
  if (id == null) return [];
  const raw = seasonEpisodeCache.value[`tv-${id}`] || [];
  return raw.filter((s) => s.seasonNumber >= 1 && s.episodeCount > 0).sort((a, b) => a.seasonNumber - b.seasonNumber);
});

/** 绝对集号 → {season, episode}；超出总集数或无季数据返回 null */
function absoluteToSeasonEpisode(absolute, seasons) {
  if (absolute == null || absolute < 1 || !seasons?.length) return null;
  let remaining = absolute;
  for (const s of seasons) {
    if (remaining <= s.episodeCount) return { season: s.seasonNumber, episode: remaining };
    remaining -= s.episodeCount;
  }
  return null;
}

// 选中候选 / 切到剧集 → 补取季数 + 逐季季名（季下拉显示季名供篇章对照）
watch([() => groupForm.value.tmdbId, () => groupForm.value.mediaType], () => {
  if (groupForm.value.mediaType === 'Tv') {
    ensureSeasonDetail();
    ensureSeasonEpisodeCounts();
  }
});

// 媒体类型变化 → 分类按类型过滤：不符则清空并尝试自动选
watch(() => groupForm.value.mediaType, (mt) => {
  const cur = categories.value.find((c) => c.id === groupForm.value.categoryId);
  const want = mt === 'Tv' ? 'Tv' : 'Movie';
  if (cur && !(cur.mediaType === want || cur.mediaType === 'Both')) {
    groupForm.value.categoryId = defaultCategoryId(mt);
  } else if (groupForm.value.categoryId == null) {
    groupForm.value.categoryId = defaultCategoryId(mt);
  }
});

// 单季剧：所有缺季号的勾选行自动补第 1 季
watch(selectedSeasonCount, (n) => {
  if (groupForm.value.mediaType !== 'Tv' || n !== 1) return;
  for (const it of openGroup.value?.items || []) {
    const e = episodeEdits.value[it.id];
    if (e && e.season == null) e.season = 1;
  }
});

// 决策 / 季集变化 → 防抖刷新去向预览
watch([groupForm, episodeEdits], schedulePreview, { deep: true });

// ---------- 抽屉内：手动搜索 TMDB + 选用 ----------

async function onTmdbSearch() {
  const itemId = anyItemId();
  if (itemId == null) return;
  const query = (searchForm.value.query || '').trim();
  if (!query) return ElMessage.warning('请输入搜索关键词');
  searching.value = true;
  try {
    const res = await api.review.tmdbSearch(itemId, {
      query,
      type: searchForm.value.type,
      year: searchForm.value.year ?? undefined,
    });
    searchResults.value = res?.items || [];
    searched.value = true;
    if (!searchResults.value.length) ElMessage.info('没搜到匹配结果，换个关键词或年份再试');
  } finally {
    searching.value = false;
  }
}

async function onTmdbIdQuery() {
  const itemId = anyItemId();
  if (itemId == null) return;
  const tmdbId = idForm.value.tmdbId;
  if (!tmdbId) return ElMessage.warning('请输入 TMDB ID');
  idQuerying.value = true;
  try {
    const d = await api.review.tmdbDetail(itemId, { tmdbId, mediaType: idForm.value.type });
    searchResults.value = [{
      tmdbId: d.tmdbId,
      mediaType: d.mediaType,
      title: d.title,
      originalTitle: d.originalTitle,
      year: d.year,
      posterUrl: d.posterUrl,
      originCountry: d.originCountry,
      totalSeasons: d.totalSeasons,
    }];
    searched.value = true;
  } finally {
    idQuerying.value = false;
  }
}

/** 把一个 TMDB 结果灌入候选并选中（前端态，确认时携带 tmdbId 落库） */
function adoptCandidate(r) {
  const mediaType = normalizeMediaType(r.mediaType);
  const exist = groupCandidates.value.find((c) => c.tmdbId === r.tmdbId);
  const card = {
    tmdbId: r.tmdbId,
    mediaType,
    title: r.title ?? exist?.title ?? null,
    originalTitle: r.originalTitle ?? exist?.originalTitle ?? null,
    year: r.year ?? exist?.year ?? null,
    posterUrl: r.posterUrl ?? exist?.posterUrl ?? null,
    totalSeasons: r.totalSeasons ?? exist?.totalSeasons ?? null,
  };
  if (exist) Object.assign(exist, card);
  else groupCandidates.value.unshift(card);
  groupForm.value.tmdbId = r.tmdbId;
  groupForm.value.mediaType = mediaType;
}
function onPickSearchResult(r) {
  adoptCandidate(r);
  searchPanelOpen.value = false;
  ElMessage.success('已选用该作品');
}

// ---------- 剧集表工具 ----------

const checkedItems = computed(() => (openGroup.value?.items || []).filter((it) => episodeEdits.value[it.id]?.checked));
const checkedCount = computed(() => checkedItems.value.length);
const allRowsChecked = computed(() =>
  (openGroup.value?.items?.length || 0) > 0 && openGroup.value.items.every((it) => episodeEdits.value[it.id]?.checked));

/** 部分勾选（表头全选复选框半选态用） */
const someRowsChecked = computed(() =>
  !allRowsChecked.value && (openGroup.value?.items || []).some((it) => episodeEdits.value[it.id]?.checked));

function toggleRow(id) { const e = episodeEdits.value[id]; if (e) e.checked = !e.checked; }
function toggleAllRows() {
  const t = !allRowsChecked.value;
  for (const it of openGroup.value.items) { const e = episodeEdits.value[it.id]; if (e) e.checked = t; }
}

// ② 表列头排序：默认 null = 组内原序（季→集→文件名）；点「名称 / 扩展名」列头切换 升→降→取消
const sortKey = ref(null); // 'name' | 'ext' | null
const sortAsc = ref(true);
function toggleSort(key) {
  if (sortKey.value !== key) { sortKey.value = key; sortAsc.value = true; return; }
  if (sortAsc.value) sortAsc.value = false;
  else { sortKey.value = null; sortAsc.value = true; }
}
/** 详情表实际渲染顺序：套用列头排序，未排序时维持组内原序 */
const sortedRows = computed(() => {
  const rows = openGroup.value?.items || [];
  if (!sortKey.value) return rows;
  const pick = sortKey.value === 'ext'
    ? (it) => fileExt(it.fileName).toLowerCase()
    : (it) => fileStem(it.fileName).toLowerCase();
  const dir = sortAsc.value ? 1 : -1;
  return rows.slice().sort((a, b) =>
    pick(a).localeCompare(pick(b), 'zh') * dir || (a.fileName || '').localeCompare(b.fileName || ''));
});

// ② 名称列手动拖宽：null = 用默认 fr 列宽；拖动后存固定 px，经 CSS 变量 --ep-name-w 覆盖 grid 模板（TV / 电影通用）
const nameColWidth = ref(null);
const epTableStyle = computed(() => (nameColWidth.value ? { '--ep-name-w': `${nameColWidth.value}px` } : {}));
function startNameResize(e) {
  const startX = e.clientX;
  const headEl = e.currentTarget.closest('.ep-head');
  const nameEl = headEl?.querySelector('.ep-sort');
  const startW = nameEl ? nameEl.getBoundingClientRect().width : 300;
  const onMove = (ev) => { nameColWidth.value = Math.max(160, Math.min(900, startW + (ev.clientX - startX))); };
  const onUp = () => {
    document.removeEventListener('mousemove', onMove);
    document.removeEventListener('mouseup', onUp);
    document.body.style.cursor = '';
    document.body.style.userSelect = '';
  };
  document.addEventListener('mousemove', onMove);
  document.addEventListener('mouseup', onUp);
  document.body.style.cursor = 'col-resize';
  document.body.style.userSelect = 'none';
}

// ② 文件检查：校验整组源文件是否仍存在，不存在的后端转 Ignored 移出队列，前端同步剔除该行
const checkingFiles = ref(false);
async function onCheckFiles() {
  const g = openGroup.value;
  if (!g || !g.items.length) return;
  const items = g.items.map((it) => ({ id: it.id, rowVersion: it.rowVersion }));
  checkingFiles.value = true;
  try {
    const res = await api.review.checkFiles({ items });
    const removed = res?.removed || [];
    const kept = res?.kept ?? 0;
    const failed = res?.failed || [];
    if (removed.length) {
      const gone = new Set(removed);
      allItems.value = allItems.value.filter((i) => !gone.has(i.id)); // 从队列源数据剔除，list 视图分组随之更新
      g.items = g.items.filter((i) => !gone.has(i.id));
      g.count = g.items.length;
      const edits = { ...episodeEdits.value };
      for (const id of removed) delete edits[id];
      episodeEdits.value = edits;
    }
    let msg = `检查完成：移除 ${removed.length} 个不存在的文件，保留 ${kept} 个`;
    if (failed.length) msg += `，${failed.length} 个未处理（并发或状态变化，建议刷新）`;
    if (removed.length) ElMessage.success(msg);
    else ElMessage.info(msg);
    if (!g.items.length) { closeGroup(); await load(); } // 整组已空 → 回队列并刷新
  } finally {
    checkingFiles.value = false;
  }
}

/** 把统一季号套用到所有勾选行 */
function applyBulkSeason() {
  if (bulkSeason.value == null) return ElMessage.warning('请先选择要套用的季号');
  let n = 0;
  for (const it of checkedItems.value) { episodeEdits.value[it.id].season = bulkSeason.value; n += 1; }
  if (n) ElMessage.success(`已把第 ${bulkSeason.value} 季套用到 ${n} 个文件`);
  else ElMessage.info('没有勾选的文件');
}

/** 按绝对集号给勾选行自动分季：以各自集号为绝对集号，按 TMDB 季结构换算 */
async function autoSplitSeasons() {
  await ensureSeasonEpisodeCounts();
  const seasons = convertSeasons.value;
  if (!seasons.length) return ElMessage.warning('未取得该剧每季集数，无法自动分季（请确认已选定 TMDB 剧集）');
  let ok = 0;
  const failed = [];
  for (const it of checkedItems.value) {
    const e = episodeEdits.value[it.id];
    const r = absoluteToSeasonEpisode(e.episode, seasons);
    if (r) { e.season = r.season; e.episode = r.episode; ok += 1; } else failed.push(it.fileName);
  }
  if (ok) ElMessage.success(`已按绝对集号分季 ${ok} 项${failed.length ? `，${failed.length} 项超出范围未改` : ''}`);
  else ElMessage.warning('没有可换算的集号（请确认各行已填绝对集号）');
}

/** 合并为单季连续编号：TMDB 仅 1 季但文件标了多季时，勾选行按 (季,集) 升序重编号为该季 E01.. */
async function mergeToSingleSeason() {
  if (selectedSeasonCount.value !== 1) return ElMessage.warning('该剧 TMDB 不止 1 季，请改用「按绝对集号分季」');
  await ensureSeasonEpisodeCounts();
  const season = convertSeasons.value[0]?.seasonNumber ?? 1;
  const entries = checkedItems.value.map((it) => {
    const e = episodeEdits.value[it.id];
    return { id: it.id, season: e.season, episode: e.episode };
  });
  if (entries.length <= 1) return ElMessage.info('请先勾选要一并合并的多个文件');
  entries.sort((a, b) => (a.season ?? 9e4) - (b.season ?? 9e4) || (a.episode ?? 9e4) - (b.episode ?? 9e4));
  entries.forEach((en, idx) => {
    const e = episodeEdits.value[en.id];
    e.season = season;
    e.episode = idx + 1;
    e.episodeEnd = null;
  });
  ElMessage.success(`已合并为第 ${season} 季连续编号：共 ${entries.length} 项（E01–E${String(entries.length).padStart(2, '0')}）`);
}

// ---------- 去向预览 ----------

function buildPreviewItems() {
  const g = openGroup.value;
  if (!g) return [];
  const { tmdbId, mediaType, categoryId } = groupForm.value;
  if (!tmdbId || !categoryId) return [];
  const picked = selectedCandidate.value;
  const isTv = mediaType === 'Tv';
  const out = [];
  for (const it of checkedItems.value) {
    const e = episodeEdits.value[it.id];
    const info = parseInfo(it.parsedInfo);
    out.push({
      key: String(it.id),
      tmdbId,
      mediaType,
      title: picked?.title || info.title || null,
      year: picked?.year ?? info.year ?? null,
      season: isTv ? e.season : null,
      episode: isTv ? e.episode : null,
      episodeEnd: isTv ? (e.episodeEnd ?? null) : null,
      categoryId,
      fileName: it.fileName,
    });
  }
  return out;
}

function schedulePreview() {
  if (previewTimer) clearTimeout(previewTimer);
  previewTimer = setTimeout(runPreview, 300);
}

async function runPreview() {
  const items = buildPreviewItems();
  if (!items.length) { previewMap.value = {}; return; }
  try {
    const res = await api.review.previewPaths({ items });
    const map = {};
    for (const e of (res?.entries || [])) map[e.key] = e;
    previewMap.value = map;
  } catch { /* 预览失败静默，不阻断确认 */ }
}

/** 底栏去向摘要：首个可算路径 → 显示落点；否则首个错误 */
const footerPreview = computed(() => {
  const items = checkedItems.value;
  if (!items.length) return null;
  for (const it of items) { const p = previewMap.value[String(it.id)]; if (p?.fullPath) return { path: p.fullPath, count: items.length }; }
  for (const it of items) { const p = previewMap.value[String(it.id)]; if (p?.error) return { error: p.error, count: items.length }; }
  return { count: items.length };
});

// ---------- 确认 / 忽略 ----------

/** 分片提交 batch-confirm 并实时刷新进度遮罩，返回聚合 { ok, fail } */
async function runBatchConfirmWithProgress(items) {
  const CHUNK = 5;
  batchProgress.value = { visible: true, done: 0, total: items.length };
  let ok = 0;
  let fail = 0;
  try {
    for (let i = 0; i < items.length; i += CHUNK) {
      const slice = items.slice(i, i + CHUNK);
      const res = await api.review.batchConfirm({ items: slice });
      ok += res?.succeeded?.length || 0;
      fail += res?.failed?.length || 0;
      batchProgress.value.done = Math.min(i + slice.length, items.length);
    }
  } finally {
    batchProgress.value.visible = false;
  }
  return { ok, fail };
}

/** 确认归档可用：作品 + 分类已定，且至少一个勾选行（剧集还要季集齐全） */
const canConfirm = computed(() => {
  if (!groupForm.value.tmdbId || !groupForm.value.categoryId) return false;
  if (!checkedCount.value) return false;
  if (groupForm.value.mediaType !== 'Tv') return true;
  return checkedItems.value.some((it) => {
    const e = episodeEdits.value[it.id];
    return e?.season != null && e?.episode != null;
  });
});

/** 用组内选定的同一作品 + 分类，确认勾选项（剧集每项带各自季 / 集） */
async function onConfirmGroup() {
  const g = openGroup.value;
  if (!g) return;
  const { tmdbId, mediaType, categoryId } = groupForm.value;
  if (!tmdbId) return ElMessage.warning('请先选择一个 TMDB 作品');
  if (!categoryId) return ElMessage.warning('请选择归档分类');
  const isTv = mediaType === 'Tv';
  const picked = selectedCandidate.value;

  const items = [];
  const skipped = [];
  for (const it of checkedItems.value) {
    const e = episodeEdits.value[it.id];
    const info = parseInfo(it.parsedInfo);
    if (isTv && (e.season == null || e.episode == null)) { skipped.push(it.fileName); continue; }
    items.push({
      id: it.id,
      tmdbId,
      mediaType,
      categoryId,
      title: picked?.title || info.title || null,
      year: picked?.year ?? info.year ?? null,
      season: isTv ? e.season : null,
      episode: isTv ? e.episode : null,
      episodeEnd: isTv ? (e.episodeEnd ?? null) : null,
      rowVersion: it.rowVersion,
    });
  }

  if (skipped.length) ElMessage.warning(`${skipped.length} 个文件缺季号或集号，已跳过（请补全后再确认）`);
  if (!items.length) return;

  // 同名冲突项（reason=NameCollision）的「确认归档」= 人工裁定覆盖：提交前显式二次确认，避免误删目标已存在文件
  const payloadIds = new Set(items.map((p) => p.id));
  const overwriteCount = checkedItems.value.filter((it) => it.reason === 'NameCollision' && payloadIds.has(it.id)).length;
  if (overwriteCount > 0) {
    try {
      await ElMessageBox.confirm(
        `选中含 ${overwriteCount} 个同名冲突项，确认归档将覆盖目标位置已存在的同名文件（旧文件连同其字幕 / nfo 一并删除，不可恢复）。是否继续？`,
        '覆盖确认',
        { confirmButtonText: '覆盖并归档', cancelButtonText: '取消', type: 'warning' });
    } catch {
      return; // 用户取消覆盖，不提交
    }
  }

  submitting.value = true;
  try {
    const { ok, fail } = await runBatchConfirmWithProgress(items);
    if (fail) ElMessage.warning(`确认完成：成功 ${ok} 条 / 失败 ${fail} 条`);
    else ElMessage.success(`已完成归档：${ok} 条`);
    closeGroup();
    await load();
  } finally {
    submitting.value = false;
  }
}

/** 忽略勾选项 */
async function onIgnoreGroup() {
  const items = checkedItems.value;
  if (!items.length) return ElMessage.warning('请先勾选要忽略的文件');
  let reason;
  try {
    const r = await ElMessageBox.prompt(
      `忽略选中的 ${items.length} 个文件？文件留在原位不再处理。`,
      '忽略',
      { confirmButtonText: '忽略', cancelButtonText: '取消', type: 'warning', inputPlaceholder: '原因（可空，记录到日志）', inputValue: '' });
    reason = r?.value || null;
  } catch {
    return;
  }
  const payloadItems = items.map((it) => ({ id: it.id, rowVersion: it.rowVersion }));
  submitting.value = true;
  try {
    const res = await api.review.batchIgnore({ items: payloadItems, reason });
    const ok = res?.succeeded?.length || 0;
    const fail = res?.failed?.length || 0;
    if (fail) ElMessage.warning(`忽略完成：成功 ${ok} 条 / 失败 ${fail} 条`);
    else ElMessage.success(`已忽略 ${ok} 条`);
    closeGroup();
    await load();
  } finally {
    submitting.value = false;
  }
}

onMounted(load);
onUnmounted(() => { if (previewTimer) clearTimeout(previewTimer); });
</script>

<template>
  <div class="page" v-loading="loading">
    <!-- ===== 列表模式（未打开任何作品组时） ===== -->
    <template v-if="!openGroup">
    <PmmPageHeader eyebrow="待确认队列" :title="`${groups.length} 组 · ${filteredCount} 个文件待确认`">
      <template #actions>
        <button class="btn" @click="load" :disabled="loading">
          <PmmIcon name="refresh" :size="14" /> 刷新
        </button>
      </template>
    </PmmPageHeader>

    <!-- 过滤栏 -->
    <div class="filter-bar card">
      <el-input v-model="filterName" placeholder="按文件名搜索" clearable style="width: 260px">
        <template #prefix><PmmIcon name="search" :size="14" /></template>
      </el-input>
      <el-select v-model="filterReasons" multiple collapse-tags collapse-tags-tooltip placeholder="入队原因（全部）" clearable style="width: 260px">
        <el-option v-for="o in REASON_OPTIONS" :key="o.value" :label="o.label" :value="o.value" />
      </el-select>
      <el-select v-model="filterSources" multiple collapse-tags collapse-tags-tooltip placeholder="解析来源（全部）" clearable style="width: 220px">
        <el-option v-for="o in SOURCE_OPTIONS" :key="o.value" :label="o.label" :value="o.value" />
      </el-select>
    </div>

    <!-- 批量忽略栏（选中至少一项时出现） -->
    <div v-if="selected.size" class="card batch-bar">
      <div class="batch-info">
        <PmmIcon name="check" :size="14" /> 已选 <b>{{ selected.size }}</b> 个文件
      </div>
      <div class="batch-actions">
        <button class="btn btn-ghost btn-sm" @click="clearSelection">清空</button>
        <button class="btn btn-sm" :disabled="submitting" @click="onBatchIgnore">
          <PmmIcon name="ban" :size="13" /> 批量忽略
        </button>
      </div>
    </div>

    <!-- 列表头常驻全选 -->
    <div v-if="groups.length" class="list-toolbar">
      <div
        class="checkbox"
        :class="{ on: allSelected, partial: someSelected }"
        role="checkbox"
        :aria-checked="allSelected ? 'true' : (someSelected ? 'mixed' : 'false')"
        @click="toggleSelectAll"
      >
        <PmmIcon v-if="allSelected" name="check" :size="12" :stroke="2.5" />
        <span v-else-if="someSelected" class="cb-dash" />
      </div>
      <span class="select-all-text" @click="toggleSelectAll">{{ allSelected ? '取消全选' : '全选' }}</span>
      <span class="muted small">
        共 {{ groups.length }} 组 / {{ filteredCount }} 文件<template v-if="selected.size"> · 已选 {{ selected.size }}</template>
      </span>
    </div>

    <!-- 分组列表 -->
    <div class="review-list">
      <div v-for="g in groups" :key="g.key" class="card review-row" @click="openGroupDrawer(g)">
        <div class="check-cell" @click.stop>
          <div class="checkbox" :class="{ on: groupAllChecked(g), partial: groupSomeChecked(g) }" @click="toggleGroupSel(g)">
            <PmmIcon v-if="groupAllChecked(g)" name="check" :size="12" :stroke="2.5" />
            <span v-else-if="groupSomeChecked(g)" class="cb-dash" />
          </div>
        </div>
        <PmmPoster :title="g.title" :year="g.year" :type="g.isTv ? 'tv' : 'movie'" size="sm" :show-text="false" />
        <div class="review-meta">
          <div class="row-tags">
            <el-tag v-for="rs in g.reasons" :key="rs" :type="REASON_VARIANT[rs].type" size="small" effect="light">
              {{ REASON_VARIANT[rs].label }}
            </el-tag>
            <el-tooltip
              v-if="g.autoRetry"
              content="TMDB 暂未收录该条目（规则识别可信但多查询全零结果）：系统每天自动重新识别一次，收录后自动归档，无需手动处理；也可现在手动搜索绑定"
              placement="top"
            >
              <el-tag type="success" size="small" effect="light">候补自动重试中</el-tag>
            </el-tooltip>
            <el-tag v-for="s in g.sources" :key="s" size="small" effect="plain">{{ SOURCE_MAP[s].label }}</el-tag>
            <span
              v-for="e in g.exts"
              :key="e"
              class="ext-chip"
              :class="{ bad: !VIDEO_EXTS.has(e.toLowerCase()) }"
              :title="VIDEO_EXTS.has(e.toLowerCase()) ? '文件格式' : '非视频格式（疑似下载残留 / 临时文件），可勾选后批量忽略'"
            >{{ e }}</span>
            <span class="muted small">{{ timeAgo(g.createdAt) }}</span>
          </div>
          <div class="title-line">
            {{ g.title || '未识别' }}
            <span v-if="g.year" class="muted year">({{ g.year }})</span>
            <span v-if="g.isTv && g.seasonLabel" class="season-badge">{{ g.seasonLabel }}</span>
            <span v-for="st in g.seasonTitles" :key="st" class="arc-badge" title="篇章季标题，供对照 TMDB 季名选季">{{ st }}</span>
            <span v-if="g.isTv && g.count > 1" class="series-badge">{{ g.count }} 集</span>
          </div>
          <div class="src-path font-mono">{{ g.dir || (g.items[0].sourcePath || g.items[0].fileName) }}</div>
        </div>
        <div class="cand-col">
          <div class="muted small">{{ g.candCount }} 个候选</div>
          <div v-if="g.topCandidate" class="cand-best">最佳: <b>{{ g.topCandidate.title }}</b></div>
        </div>
        <div class="action-col" @click.stop>
          <button class="btn btn-primary btn-sm" @click="openGroupDrawer(g)">
            处理<template v-if="g.count > 1">（{{ g.count }}）</template>
          </button>
        </div>
      </div>

      <div v-if="!groups.length && !loading" class="card empty-card">
        <PmmIcon name="check" :size="36" style="color: var(--success); margin-bottom: 10px" />
        <div class="h2" style="margin-bottom: 6px">队列已清空</div>
        <div class="muted">所有文件都已自动归档完成。</div>
      </div>
    </div>

    </template>

    <!-- ===== 详情模式（整页）：①作品 → ②文件/剧集 → ③分类 → sticky 确认 ===== -->
      <div v-else class="review-detail">
        <header class="rd-head">
          <button class="btn btn-ghost btn-sm rd-back" @click="closeGroup">
            <PmmIcon name="chevronLeft" :size="14" /> 返回队列
          </button>
          <div class="rd-head-title">
            <span class="rd-head-name">{{ openGroup.title || '未识别' }}</span>
            <span class="muted small">
              {{ openGroup.count }} 个文件 · {{ groupForm.mediaType === 'Tv' ? '剧集' : '电影' }}
              <span v-for="st in openGroup.seasonTitles" :key="st" class="arc-badge" title="篇章季标题，供对照 TMDB 季名选季">{{ st }}</span>
            </span>
          </div>
          <el-tag v-for="rs in openGroupReasons" :key="rs" :type="REASON_VARIANT[rs].type" size="small" effect="light">
            {{ REASON_VARIANT[rs].label }}
          </el-tag>
        </header>

        <div class="rd-body">
          <!-- 概览 -->
          <section class="rd-summary">
            <PmmPoster :title="openGroup.title" :year="openGroup.year" :type="groupForm.mediaType === 'Tv' ? 'tv' : 'movie'" size="md" />
            <div class="rd-info">
              <div class="eyebrow">来源目录</div>
              <div class="font-mono src-line">{{ openGroup.dir || openGroup.items[0].sourcePath }}</div>
              <div class="rd-chips">
                <el-tag size="small" effect="plain">检测于 {{ fmtTime(openGroup.createdAt) }}（{{ timeAgo(openGroup.createdAt) }}）</el-tag>
                <el-tag v-for="s in openGroup.sources" :key="s" size="small" effect="plain">{{ SOURCE_MAP[s].label }}</el-tag>
              </div>
            </div>
          </section>

          <!-- ① 作品 -->
          <section class="rd-section">
            <div class="eyebrow step">① 这是哪部作品 — 单选一个 TMDB 条目</div>

            <div v-if="groupCandidates.length" class="cand-grid">
              <div
                v-for="c in groupCandidates"
                :key="c.tmdbId"
                class="card cand-card"
                :class="{ active: groupForm.tmdbId === c.tmdbId }"
                @click="pickCandidate(c)"
              >
                <PmmPoster :title="c.title" :year="c.year" :src="c.posterUrl" size="sm" :show-text="false" />
                <div class="cand-meta">
                  <div class="cand-title">
                    {{ c.title || '(无标题)' }}<span v-if="c.year" class="muted small"> ({{ c.year }})</span>
                  </div>
                  <div v-if="c.originalTitle && c.originalTitle !== c.title" class="muted small">原名：{{ c.originalTitle }}</div>
                  <div class="muted xs">
                    TmdbId {{ c.tmdbId }} · {{ normalizeMediaType(c.mediaType) === 'Tv' ? '剧集' : '电影' }}<span v-if="c.totalSeasons"> · {{ c.totalSeasons }} 季</span>
                  </div>
                </div>
                <div class="cand-pick" :class="{ on: groupForm.tmdbId === c.tmdbId }">
                  <PmmIcon v-if="groupForm.tmdbId === c.tmdbId" name="check" :size="13" :stroke="2.5" />
                </div>
              </div>
            </div>
            <div v-else class="empty-cands muted small">
              <PmmIcon name="warning" :size="16" /> TMDB 没有返回候选，请用下方手动搜索 / 填 ID。
            </div>

            <!-- 折叠：手动搜索 / 填 ID -->
            <button class="link-toggle" @click="searchPanelOpen = !searchPanelOpen">
              <PmmIcon :name="searchPanelOpen ? 'chevronDown' : 'chevronRight'" :size="13" />
              搜不到 / 选错了？手动搜索或填 TMDB ID
            </button>
            <div v-if="searchPanelOpen" class="search-panel">
              <div class="tmdb-search">
                <el-input v-model="searchForm.query" class="sr-query" placeholder="手动搜索 TMDB 标题（中 / 英文）" clearable @keyup.enter="onTmdbSearch">
                  <template #prefix><PmmIcon name="search" :size="14" /></template>
                </el-input>
                <el-select v-model="searchForm.type" style="width: 96px">
                  <el-option label="全部" value="both" />
                  <el-option label="电影" value="movie" />
                  <el-option label="剧集" value="tv" />
                </el-select>
                <el-input-number v-model="searchForm.year" :min="1900" :max="2100" :value-on-clear="null" :controls="false" placeholder="年份" style="width: 90px" />
                <button class="btn btn-primary" :disabled="searching" @click="onTmdbSearch">
                  <PmmIcon name="search" :size="13" /> {{ searching ? '搜索中…' : '搜索' }}
                </button>
              </div>
              <div class="tmdb-id-row">
                <span class="id-label muted">或直接填 ID</span>
                <el-input-number v-model="idForm.tmdbId" :min="1" :value-on-clear="null" :controls="false" placeholder="TMDB ID" style="width: 120px" @keyup.enter="onTmdbIdQuery" />
                <el-select v-model="idForm.type" style="width: 88px">
                  <el-option label="电影" value="movie" />
                  <el-option label="剧集" value="tv" />
                </el-select>
                <button class="btn" :disabled="idQuerying" @click="onTmdbIdQuery">
                  <PmmIcon name="search" :size="13" /> {{ idQuerying ? '查询中…' : '查询' }}
                </button>
              </div>
              <div v-if="searchResults.length" class="search-results">
                <div
                  v-for="r in searchResults"
                  :key="`${r.mediaType}-${r.tmdbId}`"
                  class="card cand-card sr-card"
                  :class="{ active: groupForm.tmdbId === r.tmdbId }"
                >
                  <PmmPoster :title="r.title" :year="r.year" :src="r.posterUrl" size="sm" :show-text="false" />
                  <div class="cand-meta">
                    <div class="cand-title">{{ r.title || '(无标题)' }}<span v-if="r.year" class="muted small"> ({{ r.year }})</span></div>
                    <div v-if="r.originalTitle && r.originalTitle !== r.title" class="muted small">原名：{{ r.originalTitle }}</div>
                    <div class="muted xs">
                      TmdbId {{ r.tmdbId }} · {{ r.mediaType === 'tv' ? '剧集' : '电影' }}<span v-if="r.originCountry?.length"> · {{ r.originCountry.join('/') }}</span>
                    </div>
                  </div>
                  <button class="btn btn-primary btn-sm" @click="onPickSearchResult(r)">选用</button>
                </div>
              </div>
              <div v-else-if="searched" class="muted small sr-empty">没搜到匹配结果，换个关键词或年份再试。</div>
            </div>
          </section>

          <!-- ② 文件 / 剧集 -->
          <section class="rd-section">
            <div class="eyebrow step step-row">
              <span>② {{ groupForm.mediaType === 'Tv' ? '逐集核对季 / 集' : '确认文件' }}（{{ checkedCount }} / {{ openGroup.items.length }} 选中）</span>
              <div class="ep-tools">
                <template v-if="groupForm.mediaType === 'Tv'">
                  <el-select v-if="seasonSelectReady" v-model="bulkSeason" placeholder="统一季号" size="small" filterable clearable style="width: 124px">
                    <el-option v-for="o in seasonOptions" :key="o.value" :label="o.label" :value="o.value" />
                  </el-select>
                  <el-input-number v-else v-model="bulkSeason" :min="0" :max="999" :value-on-clear="null" :controls="false" size="small" placeholder="季" style="width: 64px" />
                  <button class="btn btn-ghost btn-sm" :disabled="!checkedCount" @click="applyBulkSeason">套用季号</button>
                  <button v-if="selectedSeasonCount !== 1" class="btn btn-ghost btn-sm" :disabled="!groupForm.tmdbId || !checkedCount" @click="autoSplitSeasons" title="把各行集号当绝对集号，按 TMDB 季结构换算成各自季 / 集">
                    按绝对集号分季
                  </button>
                  <button v-else class="btn btn-ghost btn-sm" :disabled="!groupForm.tmdbId || !checkedCount" @click="mergeToSingleSeason" title="TMDB 仅 1 季：勾选项按原季 / 集排序后合并重编号为该季连续集号">
                    合并为单季
                  </button>
                </template>
                <button class="btn btn-ghost btn-sm" :disabled="checkingFiles || !openGroup.items.length" @click="onCheckFiles" title="检查各文件源是否仍存在，已删除的从队列移除（转忽略）">
                  <PmmIcon name="search" :size="13" /> {{ checkingFiles ? '检查中…' : '检查文件' }}
                </button>
              </div>
            </div>

            <div class="ep-table" :class="groupForm.mediaType === 'Tv' ? 'tv' : 'movie'" :style="epTableStyle">
              <!-- 列头：名称 / 扩展名 与 季 / 集 / 末集 分列，便于逐集核对 -->
              <div class="ep-head">
                <span class="ep-head-check">
                  <div
                    class="checkbox"
                    :class="{ on: allRowsChecked, partial: someRowsChecked }"
                    role="checkbox"
                    :aria-checked="allRowsChecked ? 'true' : (someRowsChecked ? 'mixed' : 'false')"
                    title="全选 / 全不选"
                    @click="toggleAllRows"
                  >
                    <PmmIcon v-if="allRowsChecked" name="check" :size="12" :stroke="2.5" />
                    <span v-else-if="someRowsChecked" class="cb-dash" />
                  </div>
                </span>
                <span class="ep-sort ep-sort-name" :class="{ active: sortKey === 'name' }" title="点击按名称排序" @click="toggleSort('name')">
                  名称<span class="sort-ind">{{ sortKey === 'name' ? (sortAsc ? '↑' : '↓') : '↕' }}</span>
                  <span class="col-resize" title="拖动调整名称列宽" @click.stop @mousedown.stop.prevent="startNameResize" />
                </span>
                <span class="ep-sort" :class="{ active: sortKey === 'ext' }" title="点击按扩展名排序" @click="toggleSort('ext')">
                  扩展名<span class="sort-ind">{{ sortKey === 'ext' ? (sortAsc ? '↑' : '↓') : '↕' }}</span>
                </span>
                <template v-if="groupForm.mediaType === 'Tv'">
                  <span>季</span>
                  <span>集</span>
                  <span>末集</span>
                </template>
                <span>归档去向预览</span>
              </div>
              <div v-for="it in sortedRows" :key="it.id" class="ep-row" :class="{ on: episodeEdits[it.id]?.checked }">
                <div class="checkbox" :class="{ on: episodeEdits[it.id]?.checked }" @click="toggleRow(it.id)">
                  <PmmIcon v-if="episodeEdits[it.id]?.checked" name="check" :size="12" :stroke="2.5" />
                </div>
                <div class="ep-name font-mono" :title="it.sourcePath">{{ fileStem(it.fileName) }}</div>
                <div class="ep-ext">
                  <span class="ext-chip" :class="{ bad: !isVideoExt(it.fileName) }" :title="isVideoExt(it.fileName) ? '' : '非视频格式（疑似下载残留 / 临时文件）'">
                    {{ fileExt(it.fileName) || '无扩展名' }}
                  </span>
                </div>
                <template v-if="groupForm.mediaType === 'Tv' && episodeEdits[it.id]">
                  <div class="ep-cell">
                    <el-input-number v-model="episodeEdits[it.id].season" :min="0" :max="999" :value-on-clear="null" controls-position="right" size="small" placeholder="季" />
                  </div>
                  <div class="ep-cell">
                    <el-input-number v-model="episodeEdits[it.id].episode" :min="0" :max="9999" :value-on-clear="null" controls-position="right" size="small" placeholder="集" />
                  </div>
                  <div class="ep-cell">
                    <el-input-number v-model="episodeEdits[it.id].episodeEnd" :min="0" :max="9999" :value-on-clear="null" controls-position="right" size="small" placeholder="末集" />
                  </div>
                </template>
                <div class="ep-dest" :class="{ err: previewMap[String(it.id)]?.error }" :title="previewMap[String(it.id)]?.fullPath || ''">
                  <template v-if="previewMap[String(it.id)]?.error">⚠ {{ previewMap[String(it.id)].error }}</template>
                  <template v-else-if="previewMap[String(it.id)]?.relativePath">→ {{ previewMap[String(it.id)].relativePath }}</template>
                  <template v-else>—</template>
                </div>
              </div>
            </div>
            <div class="se-hint muted xs" v-if="groupForm.mediaType === 'Tv'">{{ seasonHint }} 动漫绝对编号用「按绝对集号分季」；多季合并成 1 季用「合并为单季」；「集」「末集」清空：删掉框内数字即可回到默认（末集仅合集 / 双集文件需填）。</div>
          </section>

          <!-- ③ 分类 -->
          <section class="rd-section">
            <div class="eyebrow step">
              ③ 归到哪个分类
              <span v-if="categoriesForType(groupForm.mediaType).length && groupForm.categoryId == null" class="step-required">· 必选</span>
            </div>
            <div class="cat-chips">
              <button
                v-for="c in categoriesForType(groupForm.mediaType)"
                :key="c.id"
                class="cat-chip"
                :class="{ on: groupForm.categoryId === c.id }"
                :title="c.targetRoot"
                @click="groupForm.categoryId = c.id"
              >
                <span class="cat-name">{{ c.name }}</span>
                <span class="cat-root font-mono">{{ c.targetRoot }}</span>
              </button>
              <span v-if="!categoriesForType(groupForm.mediaType).length" class="muted small">
                没有匹配「{{ groupForm.mediaType === 'Tv' ? '剧集' : '电影' }}」的分类，请先到「设置 · 分类」新建。
              </span>
            </div>
            <!-- 未选分类提示：有可选分类但尚未选中时提醒，呼应底栏「确认归档」禁用原因 -->
            <div v-if="categoriesForType(groupForm.mediaType).length && groupForm.categoryId == null" class="cat-warn">
              <PmmIcon name="warning" :size="14" /> 请先选择一个归档分类，否则无法确认归档（文件不知道该放到哪个库）。
            </div>
          </section>
        </div>

        <!-- sticky 底栏：实时去向 + 忽略 / 确认 -->
        <footer class="rd-foot">
          <div class="rd-foot-preview">
            <template v-if="footerPreview?.path">
              <span class="muted xs">去向</span>
              <span class="dest-path font-mono" :title="footerPreview.path">{{ footerPreview.path }}</span>
              <span v-if="footerPreview.count > 1" class="muted xs">等 {{ footerPreview.count }} 项</span>
            </template>
            <template v-else-if="footerPreview?.error">
              <span class="dest-err">⚠ {{ footerPreview.error }}</span>
            </template>
            <template v-else>
              <span class="muted xs">选定作品 + 分类后显示归档去向</span>
            </template>
          </div>
          <div class="rd-foot-actions">
            <button class="btn btn-ghost btn-sm" :disabled="submitting || !checkedCount" @click="onIgnoreGroup">
              <PmmIcon name="ban" :size="13" /> 忽略选中
            </button>
            <button class="btn btn-primary" :disabled="submitting || !canConfirm" @click="onConfirmGroup">
              <PmmIcon name="check" :size="14" /> 确认归档（{{ checkedCount }} 项）
            </button>
          </div>
        </footer>
      </div>

    <!-- 批量归档进度遮罩 -->
    <div v-if="batchProgress.visible" class="batch-progress-mask">
      <div class="batch-progress-card">
        <div class="bp-title"><span class="bp-spin" /> 正在归档，请勿关闭页面…</div>
        <div class="bp-bar"><div class="bp-bar-fill" :style="{ width: batchProgressPercent + '%' }" /></div>
        <div class="bp-sub">已处理 {{ batchProgress.done }} / {{ batchProgress.total }} 项（{{ batchProgressPercent }}%）</div>
      </div>
    </div>
  </div>
</template>

<style scoped lang="scss">
.filter-bar {
  display: flex;
  gap: 12px;
  align-items: center;
  padding: 12px 14px;
  margin-bottom: 14px;
  flex-wrap: wrap;
}
.review-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.review-row {
  display: grid;
  grid-template-columns: auto 64px 1fr auto auto;
  gap: 16px;
  padding: 14px;
  align-items: center;
  cursor: pointer;
  transition: border-color 0.15s, background 0.15s;
}
.review-row:hover {
  border-color: var(--border-strong);
  background: color-mix(in oklab, var(--surface) 80%, var(--surface-2));
}
.review-meta {
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.row-tags {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.small { font-size: 11px; }
.xs { font-size: 10px; }
.title-line {
  font-weight: 600;
  font-size: 14px;
  color: var(--text);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.title-line .year { font-weight: 500; margin-left: 6px; }
.series-badge {
  margin-left: 8px;
  font-size: 11px;
  font-weight: 600;
  color: var(--accent);
  background: color-mix(in oklab, var(--accent) 12%, transparent);
  border-radius: 10px;
  padding: 1px 8px;
}
/* 季号徽标：与集数徽标同色系、描边区分（季在前、集数在后）；缺季号不渲染 */
.season-badge {
  margin-left: 8px;
  font-size: 11px;
  font-weight: 600;
  color: var(--accent);
  border: 1px solid color-mix(in oklab, var(--accent) 40%, transparent);
  border-radius: 10px;
  padding: 0 7px;
}
/* 篇章季标题徽标（如「锻刀村篇」）：暖色与季号 / 集数徽标区分，供对照 TMDB 季名选季 */
.arc-badge {
  margin-left: 8px;
  font-size: 11px;
  font-weight: 600;
  color: var(--warning);
  background: color-mix(in oklab, var(--warning) 14%, transparent);
  border-radius: 10px;
  padding: 1px 8px;
}
.src-path {
  font-size: 11.5px;
  color: var(--text-dim);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.cand-col {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 4px;
  min-width: 160px;
  text-align: right;
}
.cand-best { font-size: 12px; color: var(--text); }
.action-col { display: flex; gap: 6px; }
.empty-card { padding: 60px 20px; text-align: center; }

.batch-bar {
  display: flex;
  align-items: center;
  padding: 10px 14px;
  margin-bottom: 12px;
  gap: 16px;
  background: color-mix(in oklab, var(--accent) 8%, var(--surface));
  border-color: var(--accent);
}
.batch-info { display: flex; align-items: center; gap: 6px; font-size: 13px; color: var(--text); }
.batch-actions { margin-left: auto; display: flex; gap: 6px; }

/* 列表头常驻全选条 */
.list-toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 2px 4px 10px;
}
.list-toolbar .checkbox.partial { border-color: var(--accent); }
.cb-dash { width: 8px; height: 2px; border-radius: 1px; background: var(--accent); }
.select-all-text { font-size: 13px; color: var(--text); cursor: pointer; user-select: none; }
.select-all-text:hover { color: var(--accent); }
.review-row .checkbox.partial { border-color: var(--accent); }

/* 抽屉骨架：头 + 可滚区 + sticky 底栏 */
/* 整页详情：取代旧 860px 抽屉，铺满内容区，季集逐行核对空间更充裕 */
.review-detail {
  display: flex;
  flex-direction: column;
  border: 1px solid var(--border-soft);
  border-radius: var(--r-2);
  background: var(--bg-elev);
  overflow: hidden;
}
.rd-head {
  padding: 14px 20px;
  display: flex;
  align-items: center;
  gap: 12px;
  border-bottom: 1px solid var(--border-soft);
  flex-shrink: 0;
}
.rd-back { flex-shrink: 0; }
.rd-head-title { display: flex; flex-direction: column; gap: 2px; min-width: 0; margin-right: auto; }
.rd-head-name { font-size: 15px; font-weight: 600; color: var(--text); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.rd-body { display: flex; flex-direction: column; }
.rd-summary {
  padding: 18px 24px;
  display: grid;
  grid-template-columns: 100px 1fr;
  gap: 20px;
  border-bottom: 1px solid var(--border-soft);
}
.rd-info { min-width: 0; }
.src-line { font-size: 12px; color: var(--text-dim); margin: 4px 0 12px; word-break: break-all; }
.rd-chips { display: flex; gap: 8px; flex-wrap: wrap; }
.rd-section { padding: 18px 24px; border-bottom: 1px solid var(--border-soft); }
.eyebrow.step {
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
  letter-spacing: 0;
  text-transform: none;
  margin-bottom: 12px;
}
.eyebrow.step.step-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; }

/* 候选网格 */
.cand-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 10px;
}
.cand-card {
  padding: 10px;
  display: grid;
  grid-template-columns: 48px 1fr auto;
  gap: 12px;
  align-items: center;
  cursor: pointer;
  transition: border-color 0.15s, background 0.15s;
}
.cand-card:hover { border-color: var(--accent); }
.cand-card.active {
  border-color: var(--accent);
  background: color-mix(in oklab, var(--accent) 8%, transparent);
}
.cand-title { font-size: 13.5px; font-weight: 600; color: var(--text); }
.cand-pick {
  width: 20px;
  height: 20px;
  border-radius: 50%;
  border: 1.5px solid var(--border-strong);
  display: flex;
  align-items: center;
  justify-content: center;
  color: #fff;
  flex-shrink: 0;
}
.cand-pick.on { background: var(--accent); border-color: var(--accent); }
.empty-cands { display: flex; align-items: center; gap: 6px; padding: 10px 0; }

/* 折叠搜索 */
.link-toggle {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  margin-top: 12px;
  padding: 4px 0;
  background: none;
  border: 0;
  color: var(--accent);
  font-size: 12.5px;
  cursor: pointer;
}
.link-toggle:hover { text-decoration: underline; }
.search-panel {
  margin-top: 10px;
  padding: 12px;
  border: 1px dashed var(--border-soft);
  border-radius: var(--r-2);
  background: var(--surface-2);
}
.tmdb-search { display: flex; gap: 8px; align-items: center; }
.tmdb-id-row { display: flex; gap: 8px; align-items: center; margin-top: 10px; }
.tmdb-id-row .id-label { font-size: 12px; white-space: nowrap; }
.sr-query { flex: 1; }
.search-results { display: flex; flex-direction: column; gap: 8px; margin-top: 12px; }
.sr-card { grid-template-columns: 48px 1fr auto; cursor: default; }

/* 剧集 / 文件表：名称 / 扩展名 与 季 / 集 / 末集 分列，逐集核对 */
.ep-tools { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; }
.ep-table { display: flex; flex-direction: column; gap: 6px; max-height: 460px; overflow-y: auto; }
.ep-head {
  display: grid;
  gap: 12px;
  align-items: center;
  padding: 0 10px 2px;
  font-size: 11px;
  color: var(--text-mute);
}
/* 表头全选复选框 + 可点排序列头（名称 / 扩展名） */
.ep-head-check { display: flex; align-items: center; }
.ep-head .checkbox.partial { border-color: var(--accent); }
.ep-sort {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  cursor: pointer;
  user-select: none;
  transition: color 0.15s;
}
.ep-sort:hover { color: var(--text); }
.ep-sort.active { color: var(--accent); }
.sort-ind { font-size: 9px; opacity: 0.6; }
.ep-sort:hover .sort-ind,
.ep-sort.active .sort-ind { opacity: 1; }
/* 名称列拖动手柄：贴列右缘（落在列间距里），独占点击不触发排序 */
.ep-sort-name { position: relative; }
.col-resize {
  position: absolute;
  top: -4px;
  bottom: -4px;
  right: -8px;
  width: 10px;
  cursor: col-resize;
  border-radius: 2px;
}
.col-resize::after {
  content: '';
  position: absolute;
  top: 2px;
  bottom: 2px;
  left: 4px;
  width: 2px;
  background: var(--border-strong);
  opacity: 0;
  transition: opacity 0.15s;
}
.col-resize:hover::after { opacity: 1; }
.ep-row {
  display: grid;
  gap: 12px;
  align-items: center;
  padding: 7px 10px;
  border: 1px solid var(--border-soft);
  border-radius: 6px;
  background: var(--surface-2);
}
/* 列宽：TV = 勾选 名称 扩展名 季 集 末集 去向；电影 = 勾选 名称 扩展名 去向 */
/* 名称列宽走 CSS 变量 --ep-name-w（默认 fr 自适应，拖动手柄后覆盖为固定 px） */
.ep-table.tv .ep-head,
.ep-table.tv .ep-row {
  grid-template-columns: 28px var(--ep-name-w, minmax(300px, 2.4fr)) 96px 82px 82px 82px minmax(150px, 1fr);
}
.ep-table.movie .ep-head,
.ep-table.movie .ep-row {
  grid-template-columns: 28px var(--ep-name-w, minmax(340px, 2.6fr)) 120px minmax(180px, 1fr);
}
.ep-row.on { border-color: var(--accent); background: color-mix(in oklab, var(--accent) 6%, var(--surface)); }
.ep-name { font-size: 12px; color: var(--text); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; min-width: 0; }
.ep-ext { min-width: 0; }
.ep-cell { min-width: 0; }
.ep-cell :deep(.el-input-number) { width: 100%; }
.ep-dest {
  font-size: 11px;
  color: var(--text-dim);
  font-family: var(--font-mono, monospace);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  min-width: 0;
}
.ep-dest.err { color: var(--warning); }
.se-hint { margin-top: 10px; line-height: 1.5; }

/* 扩展名 chip：非视频格式（下载残留 / 临时文件）红色高亮，一眼可辨 */
.ext-chip {
  display: inline-block;
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  padding: 1px 6px;
  border-radius: 5px;
  background: color-mix(in oklab, var(--text) 8%, transparent);
  color: var(--text-dim);
  white-space: nowrap;
}
.ext-chip.bad {
  background: color-mix(in oklab, var(--warning) 18%, transparent);
  color: var(--warning);
  font-weight: 600;
}

/* 分类 chips */
.cat-chips { display: flex; flex-wrap: wrap; gap: 8px; }
.cat-chip {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  padding: 8px 12px;
  border: 1px solid var(--border-soft);
  border-radius: 8px;
  background: var(--surface-2);
  cursor: pointer;
  transition: border-color 0.15s, background 0.15s;
  max-width: 280px;
}
.cat-chip:hover { border-color: var(--accent); }
.cat-chip.on { border-color: var(--accent); background: color-mix(in oklab, var(--accent) 10%, transparent); }
.cat-name { font-size: 13px; font-weight: 600; color: var(--text); }
.cat-root { font-size: 10.5px; color: var(--text-dim); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 256px; }

/* 第三步未选分类提示 */
.step-required { color: var(--warning); font-weight: 600; margin-left: 4px; font-size: 11px; }
.cat-warn {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 12px;
  font-size: 12px;
  color: var(--warning);
}

/* sticky 底栏：整页底部常驻，随页面滚动吸底 */
.rd-foot {
  position: sticky;
  bottom: 0;
  z-index: 5;
  flex-shrink: 0;
  padding: 12px 20px;
  border-top: 1px solid var(--border-soft);
  background: var(--bg-elev);
  display: flex;
  align-items: center;
  gap: 16px;
}
.rd-foot-preview { flex: 1; min-width: 0; display: flex; align-items: center; gap: 8px; }
.dest-path { font-size: 11.5px; color: var(--text); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; min-width: 0; }
.dest-err { font-size: 12px; color: var(--warning); }
.rd-foot-actions { display: flex; gap: 8px; flex-shrink: 0; }

/* 批量归档进度遮罩 */
.batch-progress-mask {
  position: fixed;
  inset: 0;
  z-index: 3000;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(0, 0, 0, 0.5);
}
.batch-progress-card {
  width: 380px;
  max-width: 86vw;
  padding: 22px 24px;
  border-radius: 12px;
  background: var(--surface-2);
  border: 1px solid var(--border-strong);
  box-shadow: 0 12px 40px rgba(0, 0, 0, 0.45);
  color: var(--text);
}
.bp-title { display: flex; align-items: center; gap: 10px; font-size: 14px; font-weight: 600; margin-bottom: 16px; }
.bp-spin {
  flex: none;
  width: 16px;
  height: 16px;
  border: 2px solid var(--border-strong);
  border-top-color: var(--accent);
  border-radius: 50%;
  animation: bp-rotate 0.8s linear infinite;
}
@keyframes bp-rotate { to { transform: rotate(360deg); } }
.bp-bar { height: 12px; border-radius: 6px; background: var(--surface-3); overflow: hidden; }
.bp-bar-fill { height: 100%; border-radius: 6px; background: var(--accent); transition: width 0.25s ease; }
.bp-sub { margin-top: 10px; font-size: 12px; color: var(--text-mute); text-align: center; }
</style>
