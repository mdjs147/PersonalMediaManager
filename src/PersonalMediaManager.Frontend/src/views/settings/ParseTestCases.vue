<script setup>
import { computed, onMounted, ref } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

// 与后端 ParseTestCase（Create/Update）字段对齐：
// { samplePath, watchRootPath, expectedTitle, expectedYear, expectedMediaType('Movie'|'Tv'|null),
//   expectedSeason, expectedEpisode, notes, rowVersion }
const STATUS_TABS = [
  { value: null, label: '全部' },
  { value: 'Active', label: '正式样本' },
  { value: 'PendingTriage', label: '暂存区' },
  { value: 'Disabled', label: '停用' },
];

const SOURCE_LABEL = { Manual: '手动', FromFailed: '失败灌入' };
const STATUS_LABEL = { PendingTriage: '暂存区', Active: '正式', Disabled: '停用' };
const RUN_LABEL = { NotRun: '未运行', Pass: '通过', Fail: '失败' };
const MEDIA_OPTIONS = [
  { value: null, label: '未指定' },
  { value: 'Movie', label: '电影' },
  { value: 'Tv', label: '剧集' },
];
const SCOPE_OPTIONS = [
  { value: 'FileName', label: '仅文件名（不含后缀）' },
  { value: 'ParentFolder', label: '直接父目录段' },
  { value: 'FullPath', label: '兼容值：父目录 + 文件名拼接' },
  { value: 'AllAncestors', label: '所有祖先目录段（内→外逐段尝试）' },
  { value: 'RelativePath', label: '监控根→文件的整条相对路径（/ 分隔）' },
];
const TYPE_OPTIONS = [
  { value: null, label: '按捕获推断' },
  { value: 'movie', label: 'movie' },
  { value: 'tv', label: 'tv' },
];

const list = ref([]);
const total = ref(0);
const page = ref(1);
const pageSize = ref(50);
const status = ref(null);
const keyword = ref('');
const loading = ref(false);

const dialogVisible = ref(false);
const editing = ref(null);
const form = ref(makeEmptyForm());

const importDialogVisible = ref(false);
const importForm = ref({ limit: 50, sinceUtc: null });
const importing = ref(false);

const batchRunning = ref(false);
const singleRunning = ref({});
const approving = ref({});
const triaging = ref({});
const suggesting = ref({});
// 详情对话框（展示 LastRunResult JSON 全文）
const detailVisible = ref(false);
const detailRow = ref(null);
// AI 判定详情对话框
const verdictVisible = ref(false);
const verdictRow = ref(null);
// AI 规则建议对话框 + 采纳表单
const suggestVisible = ref(false);
const suggestData = ref(null);
const suggestSourceRow = ref(null);
const adoptForm = ref({ name: '', scope: 'FileName', pattern: '', defaultType: null, priority: 100 });
const adopting = ref(false);

function makeEmptyForm() {
  return {
    samplePath: '',
    watchRootPath: '',
    expectedTitle: '',
    expectedYear: null,
    expectedMediaType: null,
    expectedSeason: null,
    expectedEpisode: null,
    notes: '',
    rowVersion: 0,
  };
}

async function load() {
  loading.value = true;
  try {
    const data = await api.parseTestCases.list({
      status: status.value || undefined,
      keyword: keyword.value || undefined,
      page: page.value,
      pageSize: pageSize.value,
    });
    list.value = data?.items || [];
    total.value = data?.total || 0;
  } finally {
    loading.value = false;
  }
}

function switchStatus(v) {
  status.value = v;
  page.value = 1;
  load();
}

function onSearch() {
  page.value = 1;
  load();
}

function openAdd() {
  editing.value = null;
  form.value = makeEmptyForm();
  dialogVisible.value = true;
}

function openEdit(row) {
  editing.value = row;
  form.value = {
    samplePath: row.samplePath || '',
    watchRootPath: row.watchRootPath || '',
    expectedTitle: row.expectedTitle || '',
    expectedYear: row.expectedYear ?? null,
    expectedMediaType: row.expectedMediaType ?? null,
    expectedSeason: row.expectedSeason ?? null,
    expectedEpisode: row.expectedEpisode ?? null,
    notes: row.notes || '',
    rowVersion: row.rowVersion ?? 0,
  };
  dialogVisible.value = true;
}

function buildPayload() {
  const f = form.value;
  return {
    samplePath: f.samplePath?.trim(),
    watchRootPath: f.watchRootPath?.trim() || null,
    expectedTitle: f.expectedTitle?.trim() || null,
    expectedYear: f.expectedYear || null,
    expectedMediaType: f.expectedMediaType || null,
    expectedSeason: f.expectedSeason ?? null,
    expectedEpisode: f.expectedEpisode ?? null,
    notes: f.notes?.trim() || null,
  };
}

async function save() {
  if (!form.value.samplePath?.trim()) {
    ElMessage.warning('样本路径必填');
    return;
  }
  const payload = buildPayload();
  if (editing.value) {
    await api.parseTestCases.update(editing.value.id, { ...payload, rowVersion: form.value.rowVersion });
    ElMessage.success('已更新');
  } else {
    await api.parseTestCases.create(payload);
    ElMessage.success('已添加');
  }
  dialogVisible.value = false;
  load();
}

async function remove(row) {
  await ElMessageBox.confirm(`删除测试用例 "${row.samplePath}"？`, '提示', { type: 'warning' });
  await api.parseTestCases.delete(row.id);
  ElMessage.success('已删除');
  load();
}

async function promote(row) {
  await api.parseTestCases.promote(row.id, row.rowVersion ?? 0);
  ElMessage.success('已升级为正式样本');
  load();
}

async function disable(row) {
  await api.parseTestCases.disable(row.id, row.rowVersion ?? 0);
  ElMessage.success('已停用');
  load();
}

async function resetTriage(row) {
  await api.parseTestCases.reset(row.id, row.rowVersion ?? 0);
  ElMessage.success('已回退暂存区');
  load();
}

function openImport() {
  importForm.value = { limit: 50, sinceUtc: null };
  importDialogVisible.value = true;
}

async function runImport() {
  importing.value = true;
  try {
    const limit = Math.max(1, Math.min(200, Number(importForm.value.limit) || 50));
    const data = await api.parseTestCases.importFromFailed({
      limit,
      sinceUtc: importForm.value.sinceUtc || null,
    });
    const parts = [`新增 ${data?.imported || 0} 条`, `跳过 ${data?.skipped || 0} 条`];
    const fails = data?.failures?.length || 0;
    if (fails) parts.push(`失败 ${fails} 条`);
    if (fails) ElMessage.warning(`灌入完成：${parts.join('，')}（详情见控制台）`);
    else ElMessage.success(`灌入完成：${parts.join('，')}`);
    if (fails && typeof console !== 'undefined') {
      console.warn('[ParseTestCases.import] failures:', data.failures);
    }
    importDialogVisible.value = false;
    page.value = 1;
    status.value = 'PendingTriage';
    await load();
  } finally {
    importing.value = false;
  }
}

const totalPages = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)));

function gotoPage(p) {
  const next = Math.max(1, Math.min(totalPages.value, p));
  if (next === page.value) return;
  page.value = next;
  load();
}

function formatDate(s) {
  if (!s) return '';
  try { return new Date(s).toLocaleString(); } catch { return s; }
}

// 解析 LastRunResult JSON；损坏返 null
function parseLastRun(json) {
  if (!json) return null;
  try { return JSON.parse(json); } catch { return null; }
}

// diff 表格展示的字段顺序与中文标签
const DIFF_FIELDS = [
  { key: 'title', label: '标题' },
  { key: 'year', label: '年份' },
  { key: 'mediaType', label: '类型' },
  { key: 'season', label: '季' },
  { key: 'episode', label: '集' },
];

// 期望字段标准化（用于 diff 判定）：MediaType 'Movie'/'Tv' → 'movie'/'tv'，与 RuleParseResult.mediaType 一致
function expectedAsActualShape(r) {
  return {
    title: r.expectedTitle ?? null,
    year: r.expectedYear ?? null,
    mediaType: r.expectedMediaType ? r.expectedMediaType.toLowerCase() : null,
    season: r.expectedSeason ?? null,
    episode: r.expectedEpisode ?? null,
  };
}

function isSet(v) {
  return v !== null && v !== undefined && v !== '';
}

// 单字段 diff：期望未约定 → 'na'；实测缺失 → 'fail'；否则比较
function fieldDiff(expected, actual) {
  if (!isSet(expected)) return 'na';
  if (actual === null || actual === undefined) return 'fail';
  if (typeof expected === 'string') {
    return expected.trim().toLowerCase() === String(actual).trim().toLowerCase() ? 'ok' : 'fail';
  }
  return expected === actual ? 'ok' : 'fail';
}

// 计算每行要展示的 diff 项（期望或实测任一有值的字段才展示）
function diffRows(r) {
  const exp = expectedAsActualShape(r);
  const act = parseLastRun(r.lastRunResult) || {};
  return DIFF_FIELDS
    .map((f) => ({
      key: f.key,
      label: f.label,
      expected: exp[f.key],
      actual: act[f.key] ?? null,
      state: fieldDiff(exp[f.key], act[f.key]),
    }))
    .filter((row) => isSet(row.expected) || row.actual !== null);
}

async function runOne(row) {
  singleRunning.value = { ...singleRunning.value, [row.id]: true };
  try {
    await api.parseTestCases.run(row.id);
    ElMessage.success('已运行回归');
    await load();
  } finally {
    singleRunning.value = { ...singleRunning.value, [row.id]: false };
  }
}

async function runAll() {
  if (batchRunning.value) return;
  batchRunning.value = true;
  try {
    const data = await api.parseTestCases.runBatch({ status: status.value || 'Active', limit: 500 });
    const parts = [`运行 ${data?.ran || 0} 条`];
    if (data?.pass) parts.push(`通过 ${data.pass}`);
    if (data?.fail) parts.push(`失败 ${data.fail}`);
    if (data?.notComparable) parts.push(`无基线 ${data.notComparable}`);
    const fails = data?.failures?.length || 0;
    if (fails) parts.push(`异常 ${fails}`);
    if (fails) ElMessage.warning(`批量完成：${parts.join('，')}（详情见控制台）`);
    else ElMessage.success(`批量完成：${parts.join('，')}`);
    if (fails && typeof console !== 'undefined') {
      console.warn('[ParseTestCases.runBatch] failures:', data.failures);
    }
    await load();
  } finally {
    batchRunning.value = false;
  }
}

async function approve(row) {
  await ElMessageBox.confirm(`将实测结果固化为期望基线吗？\n这会覆盖现有 Expected* 字段。`, '批准为基线', { type: 'warning' });
  approving.value = { ...approving.value, [row.id]: true };
  try {
    await api.parseTestCases.approve(row.id, row.rowVersion ?? 0);
    ElMessage.success('已批准为期望基线');
    await load();
  } finally {
    approving.value = { ...approving.value, [row.id]: false };
  }
}

function openDetail(row) {
  detailRow.value = row;
  detailVisible.value = true;
}

// 解析 AiVerdict JSON；损坏返 null
function parseVerdict(json) {
  if (!json) return null;
  try { return JSON.parse(json); } catch { return null; }
}

async function triageOne(row) {
  triaging.value = { ...triaging.value, [row.id]: true };
  try {
    await api.parseTestCases.triage(row.id);
    ElMessage.success('AI 判定完成');
    await load();
  } finally {
    triaging.value = { ...triaging.value, [row.id]: false };
  }
}

function openVerdict(row) {
  verdictRow.value = row;
  verdictVisible.value = true;
}

async function suggestRule(row) {
  suggesting.value = { ...suggesting.value, [row.id]: true };
  try {
    const data = await api.parseTestCases.suggestRule(row.id);
    suggestData.value = data;
    suggestSourceRow.value = row;
    // 预填采纳表单：规则名优先用期望标题，回退到样本文件名 stem
    const stem = (row.expectedTitle || (row.samplePath || '').split(/[\\/]/).pop() || '').replace(/\.[^.]+$/, '');
    adoptForm.value = {
      name: stem ? `${stem}（AI）` : 'AI 生成规则',
      scope: data?.scope || 'FileName',
      pattern: data?.pattern || '',
      defaultType: data?.defaultType ?? null,
      priority: 100,
    };
    suggestVisible.value = true;
  } finally {
    suggesting.value = { ...suggesting.value, [row.id]: false };
  }
}

async function adoptRule() {
  if (!adoptForm.value.name?.trim() || !adoptForm.value.pattern?.trim()) {
    ElMessage.warning('规则名与正则必填');
    return;
  }
  adopting.value = true;
  try {
    await api.parseRules.create({
      name: adoptForm.value.name.trim(),
      scope: adoptForm.value.scope,
      pattern: adoptForm.value.pattern,
      defaultType: adoptForm.value.defaultType || null,
      forceType: false,
      priority: adoptForm.value.priority ?? 100,
      confidenceBonus: 0,
      enabled: true,
      description: suggestData.value?.explanation ? `AI 生成：${suggestData.value.explanation}` : 'AI 生成的解析规则',
    });
    ElMessage.success('已新增解析规则，可在「解析规则」页查看');
    suggestVisible.value = false;
    // 采纳后重跑该样本，立刻看回归是否变绿
    if (suggestSourceRow.value) {
      try { await api.parseTestCases.run(suggestSourceRow.value.id); } catch { /* 重跑失败不阻断 */ }
    }
    await load();
  } finally {
    adopting.value = false;
  }
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="设置 / 解析测试集"
      title="解析回归测试集"
      subtitle="管理已知的文件路径样本。规则变动后可批量回归，预先发现解析偏差。"
    >
      <template #actions>
        <button class="btn btn-ghost btn-sm" :disabled="batchRunning" @click="runAll">
          <PmmIcon name="play" :size="14" /> {{ batchRunning ? '运行中…' : (status ? `批量运行（${status}）` : '批量运行（Active）') }}
        </button>
        <button class="btn btn-ghost btn-sm" @click="openImport">
          <PmmIcon name="upload" :size="14" /> 灌入失败历史
        </button>
        <button class="btn btn-primary btn-sm" @click="openAdd">
          <PmmIcon name="plus" :size="14" /> 新增样本
        </button>
      </template>
    </PmmPageHeader>

    <!-- 状态筛选 + 搜索 -->
    <div class="card filter-bar">
      <div class="tabs">
        <button
          v-for="t in STATUS_TABS"
          :key="String(t.value)"
          class="tab"
          :class="{ active: status === t.value }"
          @click="switchStatus(t.value)"
        >{{ t.label }}</button>
      </div>
      <div class="search">
        <input
          class="input"
          v-model="keyword"
          placeholder="按样本路径或期望标题筛选"
          @keyup.enter="onSearch"
        />
        <button class="btn btn-ghost btn-sm" @click="onSearch">
          <PmmIcon name="search" :size="13" /> 搜索
        </button>
      </div>
    </div>

    <!-- 列表 -->
    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th style="width: 80px">来源</th>
            <th style="width: 80px">状态</th>
            <th>样本路径</th>
            <th style="width: 260px">期望 vs 实测</th>
            <th style="width: 100px">回归</th>
            <th style="width: 240px">操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in list" :key="r.id">
            <td>
              <span class="badge" :class="`bg-source-${r.source}`">{{ SOURCE_LABEL[r.source] || r.source }}</span>
            </td>
            <td>
              <span class="badge" :class="`bg-status-${r.status}`">{{ STATUS_LABEL[r.status] || r.status }}</span>
            </td>
            <td>
              <div class="font-mono path-cell" :title="r.samplePath">{{ r.samplePath }}</div>
              <div v-if="r.watchRootPath" class="muted small">监控根：{{ r.watchRootPath }}</div>
            </td>
            <td>
              <div v-if="diffRows(r).length" class="diff-grid">
                <div v-for="d in diffRows(r)" :key="d.key" class="diff-row">
                  <span class="muted small dim-label">{{ d.label }}</span>
                  <span class="diff-cell" :class="`d-${d.state}`">
                    期 {{ d.expected ?? '—' }}
                    <span class="arrow">→</span>
                    实 {{ d.actual ?? '—' }}
                  </span>
                </div>
              </div>
              <span v-else class="muted small">未设期望且未运行</span>
            </td>
            <td>
              <span class="run-chip" :class="`run-${r.lastRunStatus}`">{{ RUN_LABEL[r.lastRunStatus] || r.lastRunStatus }}</span>
              <div v-if="r.lastRunAt" class="muted small">{{ formatDate(r.lastRunAt) }}</div>
              <div v-if="r.lastRunResult" class="muted small detail-link" @click="openDetail(r)">查看实测 JSON</div>
              <div
                v-if="parseVerdict(r.aiVerdict)"
                class="ai-chip"
                :class="parseVerdict(r.aiVerdict).worthAdding ? 'ai-yes' : 'ai-no'"
                @click="openVerdict(r)"
              >
                <PmmIcon name="brain" :size="11" /> {{ parseVerdict(r.aiVerdict).worthAdding ? 'AI 建议纳入' : 'AI 不建议' }}
              </div>
            </td>
            <td>
              <div class="hstack" style="gap: 4px; flex-wrap: wrap">
                <button class="icon-btn" title="运行回归" :disabled="!!singleRunning[r.id]" @click="runOne(r)">
                  <PmmIcon name="play" :size="14" />
                </button>
                <button class="icon-btn" title="交给 AI 判定" :disabled="!!triaging[r.id]" @click="triageOne(r)">
                  <PmmIcon name="brain" :size="14" />
                </button>
                <button class="icon-btn" title="AI 生成解析规则" :disabled="!!suggesting[r.id]" @click="suggestRule(r)">
                  <PmmIcon name="filter" :size="14" />
                </button>
                <button v-if="r.lastRunResult" class="icon-btn" title="批准为期望基线" :disabled="!!approving[r.id]" @click="approve(r)">
                  <PmmIcon name="check" :size="14" />
                </button>
                <button v-if="r.status !== 'Active'" class="icon-btn" title="升级为正式" @click="promote(r)">
                  <PmmIcon name="check" :size="14" />
                </button>
                <button v-if="r.status !== 'PendingTriage'" class="icon-btn" title="回退暂存区" @click="resetTriage(r)">
                  <PmmIcon name="refresh" :size="14" />
                </button>
                <button v-if="r.status !== 'Disabled'" class="icon-btn" title="停用" @click="disable(r)">
                  <PmmIcon name="ban" :size="14" />
                </button>
                <button class="icon-btn" title="编辑" @click="openEdit(r)"><PmmIcon name="edit" :size="14" /></button>
                <button class="icon-btn" title="删除" @click="remove(r)"><PmmIcon name="trash" :size="14" /></button>
              </div>
            </td>
          </tr>
          <tr v-if="!list.length && !loading">
            <td colspan="6" class="empty">尚无测试用例</td>
          </tr>
        </tbody>
      </table>

      <!-- 分页 -->
      <div v-if="total > 0" class="pager">
        <div class="muted small">共 {{ total }} 条，第 {{ page }} / {{ totalPages }} 页</div>
        <div class="hstack" style="gap: 6px">
          <button class="btn btn-ghost btn-sm" :disabled="page <= 1" @click="gotoPage(page - 1)">上一页</button>
          <button class="btn btn-ghost btn-sm" :disabled="page >= totalPages" @click="gotoPage(page + 1)">下一页</button>
        </div>
      </div>
    </div>

    <!-- 新增 / 编辑对话框 -->
    <el-dialog v-model="dialogVisible" :title="editing ? '编辑样本' : '新增样本'" width="640px">
      <div class="vstack" style="--gap: 14px">
        <div class="field">
          <div class="field-label">样本完整路径 <span class="required">*</span></div>
          <input class="input font-mono" v-model="form.samplePath" placeholder="如 F:\迅雷下载\国务卿女士 6季\第1季\01.mp4" />
          <div class="hint">绝对路径；用于驱动回归解析的输入。</div>
        </div>
        <div class="field">
          <div class="field-label">监控根（可选）</div>
          <input class="input font-mono" v-model="form.watchRootPath" placeholder="如 F:\迅雷下载（留空自动按监控目录最长前缀反查）" />
        </div>
        <div class="hstack" style="gap: 12px; flex-wrap: wrap">
          <div class="field" style="flex: 1; min-width: 200px">
            <div class="field-label">期望标题</div>
            <input class="input" v-model="form.expectedTitle" placeholder="如 国务卿女士" />
          </div>
          <div class="field" style="width: 120px">
            <div class="field-label">期望年份</div>
            <input class="input tabular" type="number" v-model.number="form.expectedYear" placeholder="2014" />
          </div>
        </div>
        <div class="hstack" style="gap: 12px; flex-wrap: wrap">
          <div class="field" style="width: 160px">
            <div class="field-label">期望媒体类型</div>
            <select class="input" v-model="form.expectedMediaType">
              <option v-for="opt in MEDIA_OPTIONS" :key="String(opt.value)" :value="opt.value">{{ opt.label }}</option>
            </select>
          </div>
          <div class="field" style="width: 120px">
            <div class="field-label">期望季</div>
            <input class="input tabular" type="number" v-model.number="form.expectedSeason" placeholder="1" />
          </div>
          <div class="field" style="width: 120px">
            <div class="field-label">期望集</div>
            <input class="input tabular" type="number" v-model.number="form.expectedEpisode" placeholder="1" />
          </div>
        </div>
        <div class="field">
          <div class="field-label">备注</div>
          <textarea class="textarea" rows="2" v-model="form.notes" placeholder="可选；500 字以内" />
        </div>
      </div>
      <template #footer>
        <button class="btn btn-ghost" @click="dialogVisible = false">取消</button>
        <button class="btn btn-primary" @click="save"><PmmIcon name="check" :size="14" /> 保存</button>
      </template>
    </el-dialog>

    <!-- 实测结果详情 -->
    <el-dialog v-model="detailVisible" title="最近一次回归实测结果" width="560px">
      <div v-if="detailRow" class="vstack" style="--gap: 10px">
        <div class="muted small">样本路径：<span class="font-mono">{{ detailRow.samplePath }}</span></div>
        <div class="muted small">运行时间：{{ formatDate(detailRow.lastRunAt) || '—' }}</div>
        <div class="muted small">命中规则 Id：{{ detailRow.lastMatchedRuleId ?? '内置兜底 / 无' }}</div>
        <pre class="detail-pre font-mono">{{ (() => { try { return JSON.stringify(JSON.parse(detailRow.lastRunResult), null, 2); } catch { return detailRow.lastRunResult || ''; } })() }}</pre>
      </div>
      <template #footer>
        <button class="btn btn-ghost" @click="detailVisible = false">关闭</button>
      </template>
    </el-dialog>

    <!-- AI 判定详情 -->
    <el-dialog v-model="verdictVisible" title="AI 判定结果" width="520px">
      <div v-if="verdictRow && parseVerdict(verdictRow.aiVerdict)" class="vstack" style="--gap: 12px">
        <div class="muted small">样本：<span class="font-mono">{{ verdictRow.samplePath }}</span></div>
        <div class="verdict-headline" :class="parseVerdict(verdictRow.aiVerdict).worthAdding ? 'ai-yes' : 'ai-no'">
          <PmmIcon name="brain" :size="16" />
          {{ parseVerdict(verdictRow.aiVerdict).worthAdding ? '建议纳入测试集' : '不建议纳入' }}
          <span class="muted small">（与现有样本相似度 {{ (parseVerdict(verdictRow.aiVerdict).similarity ?? 0).toFixed(2) }}）</span>
        </div>
        <div class="field">
          <div class="field-label">理由</div>
          <div class="verdict-reason">{{ parseVerdict(verdictRow.aiVerdict).reason }}</div>
        </div>
        <div class="field">
          <div class="field-label">AI 解析建议（供确认期望基线参考）</div>
          <div class="verdict-suggest font-mono">
            标题：{{ parseVerdict(verdictRow.aiVerdict).suggested?.title ?? '—' }}
            · 年份：{{ parseVerdict(verdictRow.aiVerdict).suggested?.year ?? '—' }}
            · 类型：{{ parseVerdict(verdictRow.aiVerdict).suggested?.mediaType ?? '—' }}
            <template v-if="parseVerdict(verdictRow.aiVerdict).suggested?.season != null">
              · S{{ parseVerdict(verdictRow.aiVerdict).suggested.season }}<template v-if="parseVerdict(verdictRow.aiVerdict).suggested?.episode != null">E{{ parseVerdict(verdictRow.aiVerdict).suggested.episode }}</template>
            </template>
          </div>
        </div>
        <div class="muted small">判定时间：{{ formatDate(parseVerdict(verdictRow.aiVerdict).advisedAt) }}</div>
      </div>
      <template #footer>
        <button class="btn btn-ghost" @click="verdictVisible = false">关闭</button>
      </template>
    </el-dialog>

    <!-- AI 规则建议 + 采纳 -->
    <el-dialog v-model="suggestVisible" title="AI 生成解析规则" width="600px">
      <div v-if="suggestData" class="vstack" style="--gap: 14px">
        <div v-if="suggestData.explanation" class="suggest-explain">
          <PmmIcon name="brain" :size="14" /> {{ suggestData.explanation }}
        </div>
        <div class="field">
          <div class="field-label">规则名 <span class="required">*</span></div>
          <input class="input" v-model="adoptForm.name" placeholder="规则名（唯一）" />
        </div>
        <div class="field">
          <div class="field-label">作用域</div>
          <select class="input" v-model="adoptForm.scope">
            <option v-for="opt in SCOPE_OPTIONS" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
          </select>
        </div>
        <div class="field">
          <div class="field-label">正则（AI 生成，可手动微调）</div>
          <textarea class="textarea font-mono" rows="3" v-model="adoptForm.pattern" />
          <div class="hint">命名捕获组：title / year / season / episode / episodeEnd</div>
        </div>
        <div class="hstack" style="gap: 12px; flex-wrap: wrap">
          <div class="field" style="width: 160px">
            <div class="field-label">默认类型</div>
            <select class="input" v-model="adoptForm.defaultType">
              <option v-for="opt in TYPE_OPTIONS" :key="String(opt.value)" :value="opt.value">{{ opt.label }}</option>
            </select>
          </div>
          <div class="field" style="width: 120px">
            <div class="field-label">优先级</div>
            <input class="input tabular" type="number" v-model.number="adoptForm.priority" />
          </div>
        </div>
        <div class="hint">采纳后会新增到「解析规则」并立即重跑本样本回归，可直接看是否变为通过。</div>
      </div>
      <template #footer>
        <button class="btn btn-ghost" @click="suggestVisible = false">取消</button>
        <button class="btn btn-primary" :disabled="adopting" @click="adoptRule">
          <PmmIcon name="check" :size="14" /> {{ adopting ? '采纳中…' : '采纳为规则' }}
        </button>
      </template>
    </el-dialog>

    <!-- 灌入失败历史对话框 -->
    <el-dialog v-model="importDialogVisible" title="从失败历史灌入暂存区" width="480px">
      <div class="vstack" style="--gap: 14px">
        <div class="field">
          <div class="field-label">灌入数量上限</div>
          <input class="input tabular" type="number" min="1" max="200" v-model.number="importForm.limit" />
          <div class="hint">单次最多 200 条；按失败时间倒序取。</div>
        </div>
        <div class="field">
          <div class="field-label">起始时间（可选 ISO8601）</div>
          <input class="input" v-model="importForm.sinceUtc" placeholder="如 2026-05-01T00:00:00Z（留空=不限）" />
        </div>
        <div class="hint">同 SamplePath 已存在的用例将自动跳过；新条目状态为「暂存区」，等待人工/AI 判定。</div>
      </div>
      <template #footer>
        <button class="btn btn-ghost" @click="importDialogVisible = false">取消</button>
        <button class="btn btn-primary" :disabled="importing" @click="runImport">
          <PmmIcon name="upload" :size="14" /> {{ importing ? '灌入中…' : '开始灌入' }}
        </button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped lang="scss">
.filter-bar {
  display: flex;
  align-items: center;
  gap: 18px;
  padding: 12px 16px;
  margin-bottom: 12px;
  flex-wrap: wrap;
}
.tabs { display: flex; gap: 4px; }
.tab {
  padding: 5px 12px;
  font-size: 12.5px;
  background: transparent;
  border: 1px solid transparent;
  color: var(--text-mute);
  border-radius: 999px;
  cursor: pointer;
}
.tab.active {
  color: var(--text);
  background: var(--surface-2);
  border-color: var(--border-soft);
}
.search { display: flex; gap: 8px; flex: 1; min-width: 240px; }
.search .input { flex: 1; max-width: 360px; }

.table-card { overflow: hidden; }
.path-cell {
  font-size: 11.5px;
  color: var(--text);
  max-width: 460px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.expected .ex-line { font-size: 12px; line-height: 1.5; }
.small { font-size: 11px; }
.badge {
  display: inline-block;
  padding: 1px 8px;
  font-size: 11px;
  font-weight: 600;
  border-radius: 999px;
  color: var(--text);
  background: var(--surface-2);
  border: 1px solid var(--border-soft);
}
.bg-source-Manual { color: var(--accent); }
.bg-source-FromFailed { color: #e08e3c; }
.bg-status-Active { color: #2fa86b; }
.bg-status-PendingTriage { color: #c6a23a; }
.bg-status-Disabled { color: var(--text-mute); }
.run-chip {
  display: inline-block;
  padding: 1px 8px;
  font-size: 11px;
  font-weight: 600;
  border-radius: 999px;
  border: 1px solid var(--border-soft);
}
.run-NotRun { color: var(--text-mute); }
.run-Pass { color: #2fa86b; }
.run-Fail { color: #cc3344; }

.field { display: flex; flex-direction: column; gap: 6px; }
.field-label { font-size: 12.5px; font-weight: 600; color: var(--text); }
.required { color: #cc3344; }
.hint { font-size: 11.5px; color: var(--text-mute); margin-top: 4px; }

.pager {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 16px;
  border-top: 1px solid var(--border-soft);
}

.diff-grid {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.diff-row {
  display: flex;
  gap: 6px;
  align-items: baseline;
  font-size: 12px;
  line-height: 1.55;
}
.dim-label {
  flex: 0 0 32px;
  text-align: right;
}
.diff-cell {
  display: inline-flex;
  align-items: baseline;
  gap: 4px;
  padding: 0 4px;
  border-radius: 4px;
}
.diff-cell .arrow { color: var(--text-mute); font-size: 10px; }
.d-ok { color: #2fa86b; }
.d-fail { color: #cc3344; background: rgba(204, 51, 68, 0.08); }
.d-na { color: var(--text-mute); }

.detail-link {
  cursor: pointer;
  color: var(--accent);
  text-decoration: underline;
  text-decoration-style: dotted;
}

.ai-chip {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  margin-top: 3px;
  padding: 1px 7px;
  font-size: 11px;
  font-weight: 600;
  border-radius: 999px;
  cursor: pointer;
  border: 1px solid var(--border-soft);
}
.ai-chip.ai-yes { color: #2fa86b; }
.ai-chip.ai-no { color: var(--text-mute); }
.verdict-headline {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 14px;
  font-weight: 600;
}
.verdict-headline.ai-yes { color: #2fa86b; }
.verdict-headline.ai-no { color: #cc7a33; }
.verdict-reason {
  font-size: 13px;
  line-height: 1.6;
  color: var(--text);
}
.verdict-suggest {
  font-size: 12px;
  color: var(--text);
  background: var(--surface-2);
  border: 1px solid var(--border-soft);
  border-radius: var(--r-2);
  padding: 8px 10px;
}
.suggest-explain {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  font-size: 12.5px;
  line-height: 1.6;
  color: var(--text-mute);
  background: var(--surface-2);
  border: 1px solid var(--border-soft);
  border-radius: var(--r-2);
  padding: 8px 10px;
}
.detail-pre {
  margin: 0;
  background: var(--surface-2);
  border: 1px solid var(--border-soft);
  border-radius: var(--r-2);
  padding: 10px 12px;
  font-size: 12px;
  color: var(--text);
  max-height: 360px;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-all;
}
</style>
