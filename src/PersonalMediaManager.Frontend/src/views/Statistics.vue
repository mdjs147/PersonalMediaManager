<script setup>
import { computed, onMounted, ref } from 'vue';
import { useRouter } from 'vue-router';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';
import { fmtSize } from '@/utils/format';

const router = useRouter();
const loading = ref(false);
const overview = ref(null);

// 时间范围切换（按归档时间窗过滤整页；默认「全部」= 全量库构成）
const RANGES = [
  { v: '30d', label: '近 30 天' },
  { v: '90d', label: '近 90 天' },
  { v: '1y', label: '近 1 年' },
  { v: 'all', label: '全部' },
];
const range = ref('all');
const rangeLabel = computed(() => RANGES.find((x) => x.v === range.value)?.label || '全部');

async function load() {
  loading.value = true;
  try {
    overview.value = await api.statistics.overview({ range: range.value });
  } finally {
    loading.value = false;
  }
}
function setRange(v) {
  if (range.value === v) return;
  range.value = v;
  load();
}
onMounted(load);

// 多序列调色板（首色沿用仪表盘强调金，与既有 donut 视觉一致）
const PALETTE = ['#e5a00d', '#3b82f6', '#22c55e', '#a855f7', '#ec4899', '#14b8a6', '#f97316', '#64748b', '#0ea5e9', '#ef4444'];

// 常见出品国家码 → 中文（未收录回退原码）
const COUNTRY_NAMES = {
  CN: '中国', JP: '日本', US: '美国', KR: '韩国', GB: '英国', FR: '法国', HK: '香港',
  TW: '台湾', DE: '德国', CA: '加拿大', IN: '印度', TH: '泰国', RU: '俄罗斯',
  IT: '意大利', ES: '西班牙', AU: '澳大利亚', NL: '荷兰', SE: '瑞典', DK: '丹麦', BE: '比利时',
};

// 全库是否空（决定是否显示空引导）
const isEmpty = computed(() => {
  const s = overview.value?.summary;
  return !s || (s.workCount === 0 && s.fileCount === 0);
});

// 识别方式构成（后端只给计数，比率此处计算；口径：窗口内完成归档的媒体）
const idn = computed(() => overview.value?.identification || null);
const idnTotal = computed(() => idn.value?.total ?? 0);
const pctText = (n) => (idnTotal.value > 0 ? `${Math.round((n / idnTotal.value) * 100)}%` : '—');
const aiRateText = computed(() => pctText(idn.value?.aiInvolvedCount ?? 0));
const reviewRateText = computed(() => pctText(idn.value?.reviewInvolvedCount ?? 0));

// 识别方式 5 类横向条（互斥完备，条宽 = 占 Total 真实比例）
const IDN_META = [
  { key: 'ruleCount', name: '规则直查' },
  { key: 'aiCount', name: 'AI 兜底' },
  { key: 'hybridCount', name: '复用+AI 混合' },
  { key: 'manualCount', name: '强制标识' },
  { key: 'otherCount', name: '未识别' },
];
const idnBars = computed(() => {
  const d = idn.value;
  if (!d) return [];
  return IDN_META.map((m, i) => {
    const count = d[m.key] ?? 0;
    const pct = d.total > 0 ? (count / d.total) * 100 : 0;
    return { name: m.name, count, pct, pctText: pctText(count), color: PALETTE[i % PALETTE.length] };
  });
});

// 顶部 KPI
const tiles = computed(() => {
  const s = overview.value?.summary;
  if (!s) return [];
  return [
    { label: '作品总数', value: (s.workCount ?? 0).toLocaleString(), hint: `电影 ${s.movieCount ?? 0} · 剧集 ${s.tvCount ?? 0}`, color: 'accent' },
    { label: '归档文件', value: (s.fileCount ?? 0).toLocaleString(), hint: '已完成入库', color: 'info' },
    { label: '占用空间', value: fmtSize(s.totalSize ?? 0), hint: '归档文件总和', color: 'success' },
    { label: '平均评分', value: s.avgRating != null ? s.avgRating.toFixed(1) : '—', hint: `${s.ratedCount ?? 0} 部有评分`, color: 'warning' },
    { label: '年份跨度', value: s.oldestYear && s.newestYear ? `${s.oldestYear}–${s.newestYear}` : '—', hint: '最早 ~ 最新作品', color: 'neutral' },
    { label: 'AI 参与率', value: aiRateText.value, hint: `${idn.value?.aiInvolvedCount ?? 0} / ${idnTotal.value} 个媒体`, color: 'accent' },
  ];
});

// 入库趋势折线（粒度随 range 自适应：day=按天 / month=按月）
const trend = computed(() => {
  const data = overview.value?.trend || [];
  const gran = overview.value?.trendGranularity || 'month';
  const W = 640, H = 220, padL = 34, padR = 16, padT = 18, padB = 30;
  const iw = W - padL - padR, ih = H - padT - padB;
  const max = Math.max(...data.map((d) => d.count), 1);
  const niceMax = Math.max(1, Math.ceil(max * 1.15));
  const xs = (i) => padL + (data.length <= 1 ? iw / 2 : (i / (data.length - 1)) * iw);
  const ys = (v) => padT + ih - (v / niceMax) * ih;
  const linePath = data.map((d, i) => `${i === 0 ? 'M' : 'L'} ${xs(i).toFixed(1)} ${ys(d.count).toFixed(1)}`).join(' ');
  const areaPath = data.length
    ? `${linePath} L ${xs(data.length - 1).toFixed(1)} ${(padT + ih).toFixed(1)} L ${xs(0).toFixed(1)} ${(padT + ih).toFixed(1)} Z`
    : '';
  const dots = data.map((d, i) => ({ x: xs(i), y: ys(d.count), count: d.count, bucket: d.bucket }));
  // day 桶键 yyyy-MM-dd → M/D；month 桶键 yyyy-MM → M月
  const fmtLabel = (b) => (gran === 'day' ? `${+b.split('-')[1]}/${+b.split('-')[2]}` : `${+b.slice(5)}月`);
  // 标签按粒度稀疏化：天最多 ~8 个、月最多 ~12 个，避免拥挤
  const step = gran === 'day' ? Math.max(1, Math.ceil(data.length / 8)) : Math.max(1, Math.ceil(data.length / 12));
  const labels = data.map((d, i) => ({ x: xs(i), text: fmtLabel(d.bucket), i }));
  const total = data.reduce((a, d) => a + d.count, 0);
  return { W, H, padL, padR, padT, padB, iw, ih, niceMax, linePath, areaPath, dots, labels, total, max, gran, step };
});

// 年代分布柱状
const decadeBars = computed(() => {
  const data = overview.value?.decadeDistribution || [];
  const max = Math.max(...data.map((d) => d.count), 1);
  return data.map((d) => ({ label: `${d.decade}s`, count: d.count, pct: (d.count / max) * 100 }));
});

// 评分分布直方图（固定 5 桶）
const ratingBars = computed(() => {
  const data = overview.value?.ratingHistogram || [];
  const max = Math.max(...data.map((d) => d.count), 1);
  const COLORS = { '<6': 'danger', '6~7': 'warning', '7~8': 'warning', '8~9': 'success', '9~10': 'success' };
  return data.map((d) => ({ label: d.label, count: d.count, pct: (d.count / max) * 100, color: COLORS[d.label] || 'info' }));
});

// 类型 Top（横向条）
const genreBars = computed(() => {
  const data = overview.value?.genreTop || [];
  const max = Math.max(...data.map((d) => d.count), 1);
  return data.map((d, i) => ({ name: d.name, count: d.count, pct: (d.count / max) * 100, color: PALETTE[i % PALETTE.length] }));
});

// 出品国家 Top（横向条）
const countryBars = computed(() => {
  const data = overview.value?.countryTop || [];
  const max = Math.max(...data.map((d) => d.count), 1);
  return data.map((d, i) => ({
    name: COUNTRY_NAMES[d.name] || d.name,
    code: d.name,
    count: d.count,
    pct: (d.count / max) * 100,
    color: PALETTE[i % PALETTE.length],
  }));
});

// 分类构成 donut
const catTotal = computed(() => (overview.value?.categoryDistribution || []).reduce((a, c) => a + c.count, 0) || 1);
const catSegments = computed(() => {
  const data = overview.value?.categoryDistribution || [];
  let cum = 0;
  const out = [];
  for (let i = 0; i < data.length; i++) {
    const c = data[i];
    const start = cum / catTotal.value;
    cum += c.count;
    const end = cum / catTotal.value;
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
      color: PALETTE[i % PALETTE.length],
      name: c.name,
      count: c.count,
      pct: Math.round((c.count / catTotal.value) * 100),
      full: end - start >= 0.999,
    });
  }
  return out;
});

// 存储 Top（横向条）
const storageBars = computed(() => {
  const data = overview.value?.storageTop || [];
  const max = Math.max(...data.map((d) => d.size), 1);
  return data.map((d) => ({ ...d, pct: (d.size / max) * 100, sizeText: fmtSize(d.size) }));
});

function openWork(d) {
  router.push({ name: 'LibraryDetail', params: { tmdbId: d.tmdbId }, query: { mediaType: d.mediaType } });
}
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader eyebrow="统计分析" title="媒体库洞察">
      <template #actions>
        <div class="range-switch">
          <button
            v-for="x in RANGES"
            :key="x.v"
            class="range-btn"
            :class="{ active: range === x.v }"
            @click="setRange(x.v)"
          >{{ x.label }}</button>
        </div>
        <button class="btn" @click="load" :disabled="loading">
          <PmmIcon name="refresh" :size="14" />
          刷新
        </button>
      </template>
    </PmmPageHeader>

    <!-- 空库引导（仅「全部」范围且全库为空时；时间窗内无数据则照常渲染各板块空态） -->
    <div v-if="isEmpty && range === 'all' && !loading" class="empty-hero card">
      <PmmIcon name="chart" :size="40" />
      <h3 class="h3" style="margin: 14px 0 6px">暂无统计数据</h3>
      <p class="muted">媒体库尚无已归档作品。完成首批文件入库后，这里会呈现库构成、入库趋势与存储分析。</p>
      <button class="btn btn-primary" style="margin-top: 14px" @click="router.push('/library')">
        <PmmIcon name="film" :size="14" /> 前往媒体库
      </button>
    </div>

    <template v-else>
      <!-- KPI -->
      <div class="tiles">
        <div v-for="t in tiles" :key="t.label" class="card tile">
          <div class="eyebrow xs">{{ t.label }}</div>
          <div class="font-display tabular tile-val" :style="{ color: `var(--${t.color})` }">{{ t.value }}</div>
          <div class="tile-hint">{{ t.hint }}</div>
        </div>
      </div>

      <!-- 入库趋势 -->
      <section class="card">
        <header class="card-head row-head">
          <div>
            <h3 class="h3">入库趋势</h3>
            <div class="muted small">{{ rangeLabel }} · 按{{ trend.gran === 'day' ? '天' : '月' }}归档完成文件数</div>
          </div>
          <span class="muted small">合计 {{ trend.total.toLocaleString() }} 个文件</span>
        </header>
        <div class="card-body">
          <svg :viewBox="`0 0 ${trend.W} ${trend.H}`" class="trend-svg">
            <!-- 网格 -->
            <line
              v-for="(p, i) in [0, 0.25, 0.5, 0.75, 1]"
              :key="i"
              :x1="trend.padL"
              :x2="trend.padL + trend.iw"
              :y1="trend.padT + trend.ih * p"
              :y2="trend.padT + trend.ih * p"
              stroke="var(--border-soft)"
              stroke-width="1"
            />
            <text :x="trend.padL - 6" :y="trend.padT + 4" text-anchor="end" font-size="9" fill="var(--text-dim)">{{ trend.niceMax }}</text>
            <text :x="trend.padL - 6" :y="trend.padT + trend.ih + 3" text-anchor="end" font-size="9" fill="var(--text-dim)">0</text>
            <!-- 面积 + 折线 -->
            <path :d="trend.areaPath" fill="var(--accent-soft)" opacity="0.5" />
            <path :d="trend.linePath" fill="none" stroke="var(--accent)" stroke-width="2.2" stroke-linejoin="round" />
            <!-- 数据点 -->
            <g v-for="(dt, i) in trend.dots" :key="i">
              <circle :cx="dt.x" :cy="dt.y" :r="trend.gran === 'day' ? 1.6 : 3" fill="var(--accent)" />
              <text v-if="dt.count > 0 && trend.gran === 'month'" :x="dt.x" :y="dt.y - 8" text-anchor="middle" font-size="9" fill="var(--text-mute)" font-family="Outfit">{{ dt.count }}</text>
            </g>
            <!-- 时间轴标签（按粒度稀疏显示） -->
            <text
              v-for="l in trend.labels"
              v-show="l.i % trend.step === 0"
              :key="'l' + l.i"
              :x="l.x"
              :y="trend.H - 10"
              text-anchor="middle"
              font-size="9"
              fill="var(--text-dim)"
            >{{ l.text }}</text>
          </svg>
        </div>
      </section>

      <!-- 识别方式构成 -->
      <section class="card">
        <header class="card-head row-head">
          <h3 class="h3">识别方式构成</h3>
          <span class="muted small">口径：窗口内完成归档的媒体</span>
        </header>
        <div class="card-body">
          <template v-if="idnTotal > 0">
            <div class="hbars">
              <div v-for="b in idnBars" :key="b.name" class="hbar-row">
                <span class="hbar-name">{{ b.name }}</span>
                <div class="hbar-track">
                  <div class="hbar-fill" :style="{ width: (b.count > 0 ? Math.max(b.pct, 3) : 0) + '%', background: b.color }" />
                </div>
                <span class="hbar-cnt tabular">{{ b.count }} · {{ b.pctText }}</span>
              </div>
            </div>
            <div class="idn-summary muted small">
              <span>AI 参与率 {{ aiRateText }}（{{ idn?.aiInvolvedCount ?? 0 }}/{{ idnTotal }}）</span>
              <span>人工审核介入率 {{ reviewRateText }}（{{ idn?.reviewInvolvedCount ?? 0 }}/{{ idnTotal }}）</span>
            </div>
          </template>
          <div v-else class="empty">暂无识别数据（窗口内无完成归档的媒体）</div>
        </div>
      </section>

      <!-- 年代分布 + 评分分布 -->
      <div class="row-2">
        <section class="card">
          <header class="card-head"><h3 class="h3">年代分布</h3></header>
          <div class="card-body">
            <div v-if="decadeBars.length" class="vbars">
              <div v-for="b in decadeBars" :key="b.label" class="vbar-col">
                <div class="vbar-val tabular xs muted">{{ b.count }}</div>
                <div class="vbar-track">
                  <div class="vbar-fill" :style="{ height: b.pct + '%', background: 'var(--accent)' }" />
                </div>
                <div class="vbar-label xs">{{ b.label }}</div>
              </div>
            </div>
            <div v-else class="empty">暂无年份数据</div>
          </div>
        </section>

        <section class="card">
          <header class="card-head"><h3 class="h3">评分分布</h3></header>
          <div class="card-body">
            <div v-if="ratingBars.some((b) => b.count > 0)" class="vbars">
              <div v-for="b in ratingBars" :key="b.label" class="vbar-col">
                <div class="vbar-val tabular xs muted">{{ b.count }}</div>
                <div class="vbar-track">
                  <div class="vbar-fill" :style="{ height: b.pct + '%', background: `var(--${b.color})` }" />
                </div>
                <div class="vbar-label xs">{{ b.label }}</div>
              </div>
            </div>
            <div v-else class="empty">暂无评分数据（作品富化后呈现）</div>
          </div>
        </section>
      </div>

      <!-- 类型 Top + 出品国家 Top -->
      <div class="row-2">
        <section class="card">
          <header class="card-head"><h3 class="h3">类型 Top</h3></header>
          <div class="card-body">
            <div v-if="genreBars.length" class="hbars">
              <div v-for="b in genreBars" :key="b.name" class="hbar-row">
                <span class="hbar-name">{{ b.name }}</span>
                <div class="hbar-track">
                  <div class="hbar-fill" :style="{ width: Math.max(b.pct, 3) + '%', background: b.color }" />
                </div>
                <span class="hbar-cnt tabular">{{ b.count }}</span>
              </div>
            </div>
            <div v-else class="empty">暂无类型数据（作品富化后呈现）</div>
          </div>
        </section>

        <section class="card">
          <header class="card-head"><h3 class="h3">出品国家 Top</h3></header>
          <div class="card-body">
            <div v-if="countryBars.length" class="hbars">
              <div v-for="b in countryBars" :key="b.code" class="hbar-row">
                <span class="hbar-name">{{ b.name }} <span class="muted xs">{{ b.code }}</span></span>
                <div class="hbar-track">
                  <div class="hbar-fill" :style="{ width: Math.max(b.pct, 3) + '%', background: b.color }" />
                </div>
                <span class="hbar-cnt tabular">{{ b.count }}</span>
              </div>
            </div>
            <div v-else class="empty">暂无国家数据（作品富化后呈现）</div>
          </div>
        </section>
      </div>

      <!-- 分类构成 + 存储 Top -->
      <div class="row-2">
        <section class="card">
          <header class="card-head"><h3 class="h3">分类构成</h3></header>
          <div class="card-body donut-body">
            <svg viewBox="0 0 160 160" class="donut">
              <circle cx="80" cy="80" r="60" fill="none" stroke="var(--surface-3)" stroke-width="18" />
              <template v-for="(seg, i) in catSegments" :key="i">
                <circle v-if="seg.full" cx="80" cy="80" r="60" fill="none" :stroke="seg.color" stroke-width="18" />
                <path v-else :d="seg.d" fill="none" :stroke="seg.color" stroke-width="18" />
              </template>
              <text x="80" y="76" text-anchor="middle" font-size="22" font-weight="700" font-family="Outfit" fill="var(--text)">
                {{ (catTotal === 1 && !catSegments.length ? 0 : catTotal).toLocaleString() }}
              </text>
              <text x="80" y="92" text-anchor="middle" font-size="10" fill="var(--text-dim)">作品</text>
            </svg>
            <div class="donut-legend">
              <div v-for="(seg, i) in catSegments" :key="i" class="donut-row">
                <span class="dl-color" :style="{ background: seg.color }" />
                <span class="dl-name">{{ seg.name }}</span>
                <span class="dl-pct muted tabular">{{ seg.pct }}%</span>
                <span class="dl-cnt tabular">{{ seg.count.toLocaleString() }}</span>
              </div>
              <div v-if="!catSegments.length" class="empty" style="padding: 16px">暂无分类数据</div>
            </div>
          </div>
        </section>

        <section class="card">
          <header class="card-head row-head">
            <h3 class="h3">存储占用 Top</h3>
            <a class="muted small" href="#" @click.prevent="router.push('/library')">媒体库 →</a>
          </header>
          <div class="card-body">
            <div v-if="storageBars.length" class="hbars">
              <button v-for="(b, i) in storageBars" :key="b.tmdbId + b.mediaType" class="hbar-row clickable" @click="openWork(b)">
                <span class="hbar-rank muted tabular xs">{{ i + 1 }}</span>
                <span class="hbar-name" :title="b.title || '未知'">
                  {{ b.title || '未知作品' }}<span v-if="b.year" class="muted xs"> · {{ b.year }}</span>
                </span>
                <div class="hbar-track">
                  <div class="hbar-fill" :style="{ width: Math.max(b.pct, 3) + '%', background: 'var(--info)' }" />
                </div>
                <span class="hbar-cnt tabular">{{ b.sizeText }}</span>
              </button>
            </div>
            <div v-else class="empty">暂无存储数据</div>
          </div>
        </section>
      </div>
    </template>
  </div>
</template>

<style scoped lang="scss">
.tiles {
  display: grid;
  grid-template-columns: repeat(6, 1fr);
  gap: 14px;
  margin-bottom: 18px;
}
.tile { padding: 16px; }
.tile-val { font-size: 26px; margin-top: 4px; line-height: 1.1; }
.tile-hint { font-size: 11px; color: var(--text-dim); margin-top: 4px; }

.row-2 {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 18px;
  margin-bottom: 18px;
}
.card + .row-2,
.card + .card { margin-top: 18px; }
.row-head { justify-content: space-between; }
.trend-svg { width: 100%; height: auto; }

// 时间范围段切换器
.range-switch { display: inline-flex; gap: 2px; background: var(--surface-3); padding: 3px; border-radius: 9px; }
.range-btn { border: 0; background: transparent; color: var(--text-dim); font-size: 12px; padding: 5px 11px; border-radius: 7px; cursor: pointer; }
.range-btn.active { background: var(--surface-1); color: var(--text); box-shadow: 0 1px 2px rgba(0, 0, 0, 0.08); }

// 竖向柱状（年代 / 评分）
.vbars {
  display: flex;
  align-items: flex-end;
  gap: 10px;
  height: 180px;
  padding-top: 6px;
}
.vbar-col {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  height: 100%;
  gap: 6px;
}
.vbar-val { line-height: 1; }
.vbar-track {
  flex: 1;
  width: 100%;
  max-width: 42px;
  display: flex;
  align-items: flex-end;
  justify-content: center;
}
.vbar-fill {
  width: 100%;
  border-radius: 4px 4px 0 0;
  min-height: 2px;
  transition: height 0.3s;
}
.vbar-label { color: var(--text-mute); white-space: nowrap; }

// 横向条（类型 / 国家 / 存储）
.hbars { display: flex; flex-direction: column; gap: 10px; }
.hbar-row {
  display: grid;
  grid-template-columns: minmax(72px, 0.5fr) 1fr auto;
  gap: 12px;
  align-items: center;
  font-size: 12.5px;
}
.hbar-row.clickable {
  grid-template-columns: 18px minmax(72px, 0.5fr) 1fr auto;
  width: 100%;
  background: none;
  border: 0;
  padding: 4px 6px;
  margin: 0 -6px;
  border-radius: 8px;
  cursor: pointer;
  color: inherit;
  text-align: left;
  font: inherit;
}
.hbar-row.clickable:hover { background: var(--surface-3); }
.hbar-rank { text-align: center; }
.hbar-name {
  font-weight: 600;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.hbar-track {
  height: 14px;
  background: var(--surface-2);
  border-radius: 7px;
  overflow: hidden;
}
.hbar-fill { height: 100%; border-radius: 7px; transition: width 0.3s; }
.hbar-cnt { min-width: 56px; text-align: right; font-weight: 600; color: var(--text-mute); }

// 识别方式构成小结（AI 参与率 / 人工审核介入率两行速览）
.idn-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 24px;
  margin-top: 12px;
  padding-top: 10px;
  border-top: 1px solid var(--border-soft);
}

// donut（分类构成，复用仪表盘范式）
.donut-body { display: flex; flex-direction: column; align-items: center; gap: 16px; }
.donut { width: 160px; height: 160px; flex-shrink: 0; }
.donut-legend { width: 100%; display: flex; flex-direction: column; gap: 8px; }
.donut-row { display: grid; grid-template-columns: 10px 1fr auto auto; gap: 10px; align-items: center; font-size: 12px; }
.dl-color { width: 8px; height: 8px; border-radius: 2px; }
.dl-name { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.dl-pct { min-width: 38px; text-align: right; }
.dl-cnt { min-width: 48px; text-align: right; font-weight: 600; }

// 空库引导
.empty-hero {
  text-align: center;
  padding: 48px 24px;
  color: var(--text-mute);
  display: flex;
  flex-direction: column;
  align-items: center;
}
.empty-hero p { max-width: 420px; font-size: 13px; line-height: 1.6; }

.small { font-size: 11.5px; }
.xs { font-size: 10.5px; }
.empty { padding: 30px; text-align: center; color: var(--text-mute); }
</style>
