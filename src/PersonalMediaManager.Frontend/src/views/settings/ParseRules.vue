<script setup>
import { computed, onMounted, ref } from 'vue';
import { ElMessage, ElMessageBox, ElDialog } from 'element-plus';
import { useRouter } from 'vue-router';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

const router = useRouter();

const list = ref([]);
const builtins = ref([]);
const builtinExpanded = ref({});
const loading = ref(false);
const dialogVisible = ref(false);
const editing = ref(null);
// 与后端 ParseRule（Create/Update）字段对齐：
// { name, scope(FileName/ParentFolder/FullPath/AllAncestors/RelativePath), pattern,
//   defaultType(null/'movie'/'tv'), forceType, priority, confidenceBonus(0~0.3), enabled, description, rowVersion }
// rowVersion：从 list 拿到的 ParseRuleResponse.rowVersion；编辑保存时回传，服务端比对版本号实现乐观锁
const SCOPE_OPTIONS = [
  { value: 'FileName', label: '仅文件名（不含后缀）' },
  { value: 'ParentFolder', label: '直接父目录段' },
  { value: 'FullPath', label: '兼容值：父目录 + 文件名拼接' },
  { value: 'AllAncestors', label: '所有祖先目录段（内→外逐段尝试）' },
  { value: 'RelativePath', label: '监控根→文件的整条相对路径（/ 分隔）' },
];
const form = ref({
  name: '',
  scope: 'FileName',
  pattern: '',
  defaultType: null,
  forceType: false,
  priority: 100,
  confidenceBonus: 0,
  enabled: true,
  description: '',
  rowVersion: 0,
});

const tester = ref({ fileName: '', result: null });
const threshold = ref(0.6);

async function load() {
  loading.value = true;
  try {
    // 用户规则与内置规则并发拉取；前者影响表单交互必须等到，后者只读失败不阻塞
    const [userRules, builtinRules] = await Promise.all([
      api.parseRules.list(),
      api.parseRules.listBuiltin().catch(() => []),
    ]);
    list.value = userRules?.items || userRules || [];
    builtins.value = builtinRules || [];
  } finally {
    loading.value = false;
  }
}

function toggleBuiltin(key) {
  builtinExpanded.value = { ...builtinExpanded.value, [key]: !builtinExpanded.value[key] };
}

function openAdd() {
  editing.value = null;
  form.value = {
    name: '',
    scope: 'FileName',
    pattern: '',
    defaultType: null,
    forceType: false,
    priority: 100,
    confidenceBonus: 0,
    enabled: true,
    description: '',
    rowVersion: 0,
  };
  dialogVisible.value = true;
}

function openEdit(row) {
  editing.value = row;
  form.value = {
    name: row.name,
    scope: row.scope || 'FileName',
    pattern: row.pattern,
    defaultType: row.defaultType ?? null,
    forceType: !!row.forceType,
    priority: row.priority ?? 100,
    confidenceBonus: row.confidenceBonus ?? 0,
    enabled: !!row.enabled,
    description: row.description ?? '',
    rowVersion: row.rowVersion ?? 0,
  };
  dialogVisible.value = true;
}

async function save() {
  if (!form.value.name || !form.value.pattern) {
    ElMessage.warning('规则名与正则必填');
    return;
  }
  if (editing.value) {
    await api.parseRules.update(editing.value.id, form.value);
    ElMessage.success('已更新');
  } else {
    await api.parseRules.create(form.value);
    ElMessage.success('已添加');
  }
  dialogVisible.value = false;
  load();
}

async function toggle(row) {
  await api.parseRules.update(row.id, { ...row, enabled: !row.enabled });
  load();
}

async function remove(row) {
  await ElMessageBox.confirm(`删除规则 "${row.name}"？`, '提示', { type: 'warning' });
  await api.parseRules.delete(row.id);
  ElMessage.success('已删除');
  load();
}

const sortedRules = computed(() => [...list.value].sort((a, b) => (a.priority ?? 100) - (b.priority ?? 100)));

async function quickTest() {
  if (!tester.value.fileName) {
    ElMessage.warning('请填写测试文件名');
    return;
  }
  // 按优先级遍历所有启用规则，逐条调后端 /api/settings/parse-rules/test 复用统一正则引擎；
  // 命中第一条即停。后端 TestParseRuleRequest = { pattern, sample }。
  for (const r of sortedRules.value) {
    if (!r.enabled) continue;
    try {
      const resp = await api.parseRules.test({ pattern: r.pattern, sample: tester.value.fileName });
      if (resp?.matched) {
        tester.value.result = {
          rule: r.name,
          pattern: r.pattern,
          groups: resp.groups || {},
          elapsedMs: resp.elapsedMilliseconds,
        };
        return;
      }
    } catch {
      // 单条无效正则不打断整体测试
    }
  }
  tester.value.result = { rule: null, message: '没有规则命中，会转交 AI 兜底解析' };
}

const importInput = ref(null);
const importing = ref(false);

function openImport() {
  importInput.value?.click();
}

async function onPickImport(ev) {
  const file = ev.target?.files?.[0];
  if (!file) return;

  let mode = 'Merge';
  try {
    const r = await ElMessageBox.confirm(
      `导入规则文件 "${file.name}"？\n• 合并模式：仅新增不在现有规则中的条目（按名称去重）\n• 替换模式：先清空所有现有规则再插入（高风险）`,
      '选择导入模式',
      { type: 'warning', confirmButtonText: '合并', cancelButtonText: '取消', distinguishCancelAndClose: true });
    if (r === 'confirm') mode = 'Merge';
  } catch (action) {
    if (action === 'close') {
      // 用户点了关闭：再问要不要替换模式
      try {
        await ElMessageBox.confirm('改用替换模式？将清空所有现有规则后再插入。', '危险操作', {
          type: 'error', confirmButtonText: '我已备份，确认替换', cancelButtonText: '取消',
        });
        mode = 'Replace';
      } catch {
        if (ev.target) ev.target.value = '';
        return;
      }
    } else {
      if (ev.target) ev.target.value = '';
      return;
    }
  }

  importing.value = true;
  try {
    const data = await api.parseRules.import(file, mode);
    const failCount = data?.failures?.length || 0;
    const msgParts = [`新增 ${data?.added || 0} 条`, `跳过 ${data?.skipped || 0} 条`];
    if (data?.deletedBeforeImport) msgParts.push(`导入前删除 ${data.deletedBeforeImport} 条`);
    if (failCount) msgParts.push(`失败 ${failCount} 条`);
    if (failCount) ElMessage.warning(`导入完成：${msgParts.join('，')}（详情见控制台）`);
    else ElMessage.success(`导入完成：${msgParts.join('，')}`);
    if (failCount && typeof console !== 'undefined') {
      console.warn('[ParseRules.import] failures:', data.failures);
    }
    await load();
  } finally {
    importing.value = false;
    if (ev.target) ev.target.value = '';
  }
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="设置 / 解析规则"
      title="规则引擎"
      subtitle="自定义规则按优先级匹配。命名捕获组 title / year / season / episode 会被自动提取。"
    >
      <template #actions>
        <input ref="importInput" type="file" accept=".json,application/json" style="display: none" @change="onPickImport" />
        <button class="btn btn-ghost btn-sm" :disabled="importing" @click="openImport">
          <PmmIcon name="upload" :size="14" /> {{ importing ? '导入中…' : '导入' }}
        </button>
        <button class="btn btn-primary btn-sm" @click="openAdd">
          <PmmIcon name="plus" :size="14" /> 新增规则
        </button>
      </template>
    </PmmPageHeader>

    <!-- 阈值条 -->
    <div class="card threshold-bar">
      <div class="th-meta">
        <div class="label">规则置信度阈值</div>
        <div class="hint">低于此值的解析结果会直接转交 AI 兜底。</div>
      </div>
      <div class="th-slider">
        <input type="range" min="0" max="1" step="0.05" v-model.number="threshold" />
        <div class="font-display tabular th-val">{{ threshold.toFixed(2) }}</div>
      </div>
    </div>

    <!-- 规则表 -->
    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th style="width: 70px">优先级</th>
            <th>名称</th>
            <th>正则</th>
            <th style="width: 80px">启用</th>
            <th style="width: 100px">操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in sortedRules" :key="r.id">
            <td class="tabular muted">{{ r.priority }}</td>
            <td class="cell-strong">{{ r.name }}</td>
            <td>
              <div class="font-mono pattern-cell">{{ r.pattern }}</div>
            </td>
            <td>
              <div class="switch" :class="{ on: r.enabled }" @click="toggle(r)" />
            </td>
            <td>
              <div class="hstack" style="gap: 4px">
                <button class="icon-btn" @click="openEdit(r)"><PmmIcon name="edit" :size="15" /></button>
                <button class="icon-btn" @click="remove(r)"><PmmIcon name="trash" :size="15" /></button>
              </div>
            </td>
          </tr>
          <tr v-if="!list.length && !loading">
            <td colspan="5" class="empty">尚无规则，使用内置规则兜底</td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- 内置规则（只读，兜底运行） -->
    <section class="section-card builtin-card" style="margin-top: 16px">
      <header class="section-head">
        <div>
          <h3>内置规则 <span class="ro-chip">只读</span></h3>
          <div class="sub">所有用户规则均未命中时兜底运行，由系统维护。下列 {{ builtins.length }} 条覆盖季集 / 集号 / 年份提取与标题清洗。</div>
        </div>
      </header>
      <div v-if="!builtins.length" class="empty builtin-empty">尚未加载内置规则列表</div>
      <ul v-else class="builtin-list">
        <li v-for="b in builtins" :key="b.key" class="builtin-row" :class="{ expanded: !!builtinExpanded[b.key] }">
          <div class="builtin-head" @click="toggleBuiltin(b.key)">
            <div class="builtin-title">
              <span class="builtin-order tabular">#{{ b.order }}</span>
              <span class="builtin-name">{{ b.name }}</span>
            </div>
            <PmmIcon :name="builtinExpanded[b.key] ? 'chevron-up' : 'chevron-down'" :size="14" />
          </div>
          <div class="builtin-desc">{{ b.description }}</div>
          <div v-if="builtinExpanded[b.key]" class="builtin-detail">
            <div class="detail-row">
              <div class="detail-label">正则</div>
              <pre class="font-mono builtin-pattern">{{ b.pattern }}</pre>
            </div>
            <div v-if="b.samples?.length" class="detail-row">
              <div class="detail-label">样本</div>
              <ul class="sample-list">
                <li v-for="(s, i) in b.samples" :key="i" class="font-mono sample-item">{{ s }}</li>
              </ul>
            </div>
          </div>
        </li>
      </ul>
    </section>

    <!-- 测试器 -->
    <section class="section-card" style="margin-top: 16px">
      <header class="section-head">
        <div>
          <h3>规则测试器</h3>
          <div class="sub">在浏览器端预演规则匹配，便于快速调试。</div>
        </div>
      </header>
      <div class="form-row">
        <div>
          <div class="label">测试文件名</div>
          <div class="hint">输入完整文件名（含扩展名）。</div>
        </div>
        <div class="control">
          <input
            class="input font-mono"
            v-model="tester.fileName"
            placeholder="如 Inception.2010.1080p.BluRay.x264.mkv"
            style="flex: 1; max-width: 480px"
            @keyup.enter="quickTest"
          />
          <button class="btn btn-primary" @click="quickTest"><PmmIcon name="play" :size="13" /> 测试</button>
        </div>
      </div>
      <div v-if="tester.result" class="form-row">
        <div>
          <div class="label">结果</div>
        </div>
        <div class="control">
          <pre class="result-pre">{{ JSON.stringify(tester.result, null, 2) }}</pre>
        </div>
      </div>
    </section>

    <!-- 编辑对话框 -->
    <el-dialog v-model="dialogVisible" :title="editing ? '编辑规则' : '新增规则'" width="560px">
      <div class="vstack" style="--gap: 14px">
        <div class="field">
          <div class="field-label">规则名</div>
          <input class="input" v-model="form.name" placeholder="如 PT 站标准命名" />
        </div>
        <div class="field">
          <div class="field-label">作用域</div>
          <select class="input" v-model="form.scope">
            <option v-for="opt in SCOPE_OPTIONS" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
          </select>
          <div class="hint">AllAncestors 适合「剧名在父/祖父目录、文件名只剩 SxxExx」的 PT 站命名；RelativePath 适合需要跨段联合匹配的复杂规则。</div>
        </div>
        <div class="field">
          <div class="field-label">Regex 模式</div>
          <textarea class="textarea font-mono" rows="4" v-model="form.pattern" placeholder="如 ^(?<title>.+?)\\.(?<year>\\d{4})\\." />
          <div class="hint">支持命名捕获组：<code class="font-mono path">title</code>、<code class="font-mono path">year</code>、<code class="font-mono path">season</code>、<code class="font-mono path">episode</code>、<code class="font-mono path">episodeEnd</code>（双集合并）</div>
        </div>
        <div class="field">
          <div class="field-label">优先级</div>
          <input class="input tabular" type="number" v-model.number="form.priority" style="width: 120px" />
        </div>
        <label class="hstack" style="gap: 8px; cursor: pointer">
          <div class="switch" :class="{ on: form.enabled }" @click="form.enabled = !form.enabled" />
          <span>启用</span>
        </label>
      </div>
      <template #footer>
        <button class="btn btn-ghost" @click="dialogVisible = false">取消</button>
        <button class="btn btn-primary" @click="save"><PmmIcon name="check" :size="14" /> 保存</button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped lang="scss">
.threshold-bar {
  padding: 16px;
  margin-bottom: 16px;
  display: flex;
  align-items: center;
  gap: 18px;
  flex-wrap: wrap;
}
.th-meta {
  flex: 1;
  min-width: 240px;
}
.th-meta .label { font-size: 13px; font-weight: 600; color: var(--text); }
.th-meta .hint { font-size: 12px; color: var(--text-mute); margin-top: 2px; }
.th-slider {
  display: flex;
  align-items: center;
  gap: 14px;
  flex: 1;
}
.th-slider input[type='range'] {
  flex: 1;
  accent-color: var(--accent);
}
.th-val {
  font-size: 22px;
  color: var(--accent);
  min-width: 60px;
  text-align: right;
}
.table-card { overflow: hidden; }
.pattern-cell {
  font-size: 11.5px;
  color: var(--text);
  max-width: 480px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.field { display: flex; flex-direction: column; gap: 6px; }
.field-label { font-size: 12.5px; font-weight: 600; color: var(--text); }
.hint { font-size: 11.5px; color: var(--text-mute); margin-top: 4px; }
.result-pre {
  margin: 0;
  background: var(--surface-2);
  border: 1px solid var(--border-soft);
  border-radius: var(--r-2);
  padding: 10px 12px;
  font-family: 'JetBrains Mono', monospace;
  font-size: 12px;
  color: var(--text);
  max-width: 600px;
  overflow: auto;
}

.builtin-card .ro-chip {
  display: inline-block;
  margin-left: 6px;
  padding: 1px 8px;
  font-size: 11px;
  font-weight: 600;
  color: var(--text-mute);
  background: var(--surface-2);
  border: 1px solid var(--border-soft);
  border-radius: 999px;
  vertical-align: 2px;
}
.builtin-empty {
  padding: 14px 12px;
  color: var(--text-mute);
}
.builtin-list {
  list-style: none;
  padding: 0;
  margin: 0;
  display: flex;
  flex-direction: column;
}
.builtin-row {
  padding: 12px 4px;
  border-top: 1px solid var(--border-soft);
}
.builtin-row:first-child { border-top: none; }
.builtin-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  cursor: pointer;
  user-select: none;
}
.builtin-head:hover .builtin-name { color: var(--accent); }
.builtin-title {
  display: flex;
  align-items: baseline;
  gap: 10px;
}
.builtin-order {
  font-size: 11.5px;
  color: var(--text-mute);
  min-width: 26px;
}
.builtin-name {
  font-size: 13.5px;
  font-weight: 600;
  color: var(--text);
  transition: color 120ms ease;
}
.builtin-desc {
  font-size: 12.5px;
  color: var(--text-mute);
  margin-top: 4px;
  padding-left: 36px;
  line-height: 1.55;
}
.builtin-detail {
  margin-top: 8px;
  padding: 10px 12px 10px 36px;
  background: var(--surface-2);
  border: 1px solid var(--border-soft);
  border-radius: var(--r-2);
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.detail-row { display: flex; gap: 12px; align-items: flex-start; }
.detail-label {
  width: 38px;
  flex: 0 0 auto;
  font-size: 11.5px;
  color: var(--text-mute);
  padding-top: 2px;
}
.builtin-pattern {
  margin: 0;
  flex: 1;
  font-size: 11.5px;
  color: var(--text);
  white-space: pre-wrap;
  word-break: break-all;
}
.sample-list {
  flex: 1;
  list-style: none;
  padding: 0;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.sample-item {
  font-size: 11.5px;
  color: var(--text-mute);
}
</style>
