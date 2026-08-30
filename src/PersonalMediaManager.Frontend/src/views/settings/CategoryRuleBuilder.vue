<script setup>
// 分类匹配规则编辑器（权威 DSL：{ all: [...] } / { any: [...] } / { field, op, value }）
//
// 后端 CategoryMatchRule.Conditions 以 JSON 列存储，权威格式：
//   组节点：{ all: [子节点...] } 或 { any: [子节点...] }（互斥，仅一个 key）
//   叶子节点：{ field, op, value }
//
// 字段 / 操作符必须与后端求值器 CategoryRuleEvaluator 严格一致，否则规则能保存但分类时永不命中（静默失效）。
// 分类发生在 TMDB 匹配之后，按权威元数据判定，故仅以下 4 个字段：
//   - type            标量，movie / tv
//   - originalLanguage 标量，ISO 639-1（zh / en / ja…）
//   - originCountry   列表，出品地区（ISO 3166-1）
//   - genres          列表，TMDB 类型名（Animation / 动画…）
// 操作符（camelCase，后端 ToLowerInvariant 后对应 eq / noteq / in / notin / contains）：
//   - 标量字段：eq / notEq / in / notIn
//   - 列表字段：contains / in / notIn
// 求值器未实现 gt/gte/lt/lte/startsWith/endsWith，本编辑器一律不提供（提供即静默失效）。
import { computed, defineComponent, onMounted, ref } from 'vue';
import { useRouter } from 'vue-router';
import { ElMessage, ElMessageBox } from 'element-plus';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

const props = defineProps({ id: { type: [String, Number], required: true } });
const router = useRouter();

// 取值候选项 —— { value: 提交后端的权威码, label: UI 展示（中文名 + 码，filterable 同时可搜中文与码）}。
// 后端无「码→名」映射（LibraryService 的 country/language facet 直接拿码当名），故友好名在前端维护。
// 列表为「常见项」，配合 el-select 的 allow-create：表中没有的冷门码仍可手输（保持与旧文本框等价的灵活性）。
const TYPE_OPTIONS = [
  { value: 'movie', label: '电影 movie' },
  { value: 'tv', label: '剧集 tv' },
];
// 原始语言 ISO 639-1（TMDB original_language 口径：普通话用 zh，粤语用 cn）
const LANGUAGE_OPTIONS = [
  { value: 'zh', label: '中文·普通话 zh' },
  { value: 'cn', label: '中文·粤语 cn' },
  { value: 'en', label: '英语 en' },
  { value: 'ja', label: '日语 ja' },
  { value: 'ko', label: '韩语 ko' },
  { value: 'fr', label: '法语 fr' },
  { value: 'de', label: '德语 de' },
  { value: 'es', label: '西班牙语 es' },
  { value: 'it', label: '意大利语 it' },
  { value: 'ru', label: '俄语 ru' },
  { value: 'pt', label: '葡萄牙语 pt' },
  { value: 'hi', label: '印地语 hi' },
  { value: 'th', label: '泰语 th' },
  { value: 'vi', label: '越南语 vi' },
  { value: 'id', label: '印尼语 id' },
  { value: 'ms', label: '马来语 ms' },
  { value: 'ar', label: '阿拉伯语 ar' },
  { value: 'tr', label: '土耳其语 tr' },
  { value: 'nl', label: '荷兰语 nl' },
  { value: 'sv', label: '瑞典语 sv' },
  { value: 'da', label: '丹麦语 da' },
  { value: 'no', label: '挪威语 no' },
  { value: 'fi', label: '芬兰语 fi' },
  { value: 'pl', label: '波兰语 pl' },
  { value: 'cs', label: '捷克语 cs' },
  { value: 'hu', label: '匈牙利语 hu' },
  { value: 'el', label: '希腊语 el' },
  { value: 'he', label: '希伯来语 he' },
  { value: 'fa', label: '波斯语 fa' },
  { value: 'uk', label: '乌克兰语 uk' },
];
// 出品地区 ISO 3166-1 alpha-2
const COUNTRY_OPTIONS = [
  { value: 'CN', label: '中国大陆 CN' },
  { value: 'HK', label: '中国香港 HK' },
  { value: 'TW', label: '中国台湾 TW' },
  { value: 'JP', label: '日本 JP' },
  { value: 'KR', label: '韩国 KR' },
  { value: 'US', label: '美国 US' },
  { value: 'GB', label: '英国 GB' },
  { value: 'FR', label: '法国 FR' },
  { value: 'DE', label: '德国 DE' },
  { value: 'IT', label: '意大利 IT' },
  { value: 'ES', label: '西班牙 ES' },
  { value: 'CA', label: '加拿大 CA' },
  { value: 'AU', label: '澳大利亚 AU' },
  { value: 'IN', label: '印度 IN' },
  { value: 'TH', label: '泰国 TH' },
  { value: 'SG', label: '新加坡 SG' },
  { value: 'MY', label: '马来西亚 MY' },
  { value: 'ID', label: '印度尼西亚 ID' },
  { value: 'PH', label: '菲律宾 PH' },
  { value: 'VN', label: '越南 VN' },
  { value: 'RU', label: '俄罗斯 RU' },
  { value: 'BR', label: '巴西 BR' },
  { value: 'MX', label: '墨西哥 MX' },
  { value: 'AR', label: '阿根廷 AR' },
  { value: 'NL', label: '荷兰 NL' },
  { value: 'SE', label: '瑞典 SE' },
  { value: 'DK', label: '丹麦 DK' },
  { value: 'NO', label: '挪威 NO' },
  { value: 'FI', label: '芬兰 FI' },
  { value: 'PL', label: '波兰 PL' },
  { value: 'TR', label: '土耳其 TR' },
  { value: 'IR', label: '伊朗 IR' },
  { value: 'IL', label: '以色列 IL' },
  { value: 'NZ', label: '新西兰 NZ' },
  { value: 'BE', label: '比利时 BE' },
  { value: 'CH', label: '瑞士 CH' },
  { value: 'AT', label: '奥地利 AT' },
  { value: 'IE', label: '爱尔兰 IE' },
  { value: 'PT', label: '葡萄牙 PT' },
  { value: 'GR', label: '希腊 GR' },
];

// 字段定义 —— 必须与后端 CategoryRuleEvaluator 实际支持的字段严格一致（type/originalLanguage/originCountry/genres）。
// kind：'single'（标量，对应求值器 EvalSingle）/ 'list'（列表，对应 EvalList），决定可选 op 列表与取值控件。
// options：配置后取值控件用可搜索 el-select（type/originalLanguage/originCountry）；未配置则退回文本框（genres）。
// allowCreate：el-select 允许手输候选表外的码（ISO 冷门项），保持与旧自由文本框等价的录入能力。
const FIELDS = {
  type: { label: '媒体类型 type', kind: 'single', options: TYPE_OPTIONS, placeholder: '选择媒体类型' },
  originalLanguage: { label: '原始语言 originalLanguage', kind: 'single', options: LANGUAGE_OPTIONS, allowCreate: true, placeholder: '搜索/选择语言，或手输 ISO 639-1 码' },
  originCountry: { label: '出品地区 originCountry', kind: 'list', options: COUNTRY_OPTIONS, allowCreate: true, placeholder: '搜索/选择地区，或手输 ISO 3166-1 码' },
  genres: { label: 'TMDB 类型 genres', kind: 'list', placeholder: '类型名，如 Animation / 动画（多值逗号分隔）' },
};

// 操作符定义（label 给 UI；allow=适用字段 kind 白名单；multi=true 表示 value 为列表，UI 用逗号输入）。
// 名称（camelCase）经后端 ToLowerInvariant 后对应 eq/noteq/in/notin/contains，与 CategoryRuleEvaluator 一一对应。
// 列表字段不提供 eq/notEq（后端 EvalList.eq 语义为「列表恰好 1 元素且相等」，易误用），只给 contains/in/notIn。
const OPS = {
  eq: { label: '等于 =', allow: ['single'], multi: false },
  notEq: { label: '不等于 ≠', allow: ['single'], multi: false },
  contains: { label: '包含 contains', allow: ['list'], multi: false },
  in: { label: '属于任一 in', allow: ['single', 'list'], multi: true },
  notIn: { label: '不属于任一 notIn', allow: ['single', 'list'], multi: true },
};

// 编辑器内部状态
const rootRule = ref({ all: [] }); // 当前编辑的权威 DSL 根节点
const ruleId = ref(null); // 已有规则 → update；无则 create
const ruleRowVersion = ref(0); // 乐观并发令牌，update 时回传后端
const name = ref('');
const priority = ref(100);
const enabled = ref(true);
const category = ref(null);
const saving = ref(false);

// 加载分类基础信息（用于面包屑显示）
async function loadCategory() {
  try {
    const list = await api.categories.list();
    const items = list?.items || list || [];
    category.value = items.find((c) => String(c.id) === String(props.id)) || { id: props.id, name: '未知分类' };
  } catch (e) {
    category.value = { id: props.id, name: '未知分类' };
  }
}

// 加载当前分类下的第一条规则作为初始编辑对象
async function loadRule() {
  try {
    const list = await api.categoryMatchRules.list({ categoryId: props.id });
    const items = list?.items || list || [];
    const first = items[0];
    if (first) {
      ruleId.value = first.id;
      ruleRowVersion.value = first.rowVersion ?? 0;
      name.value = first.name || '';
      priority.value = first.priority ?? 100;
      enabled.value = first.enabled ?? true;
      // 后端可能返回字符串或对象
      let conditions = first.conditions;
      if (typeof conditions === 'string') {
        try { conditions = JSON.parse(conditions); } catch { conditions = null; }
      }
      rootRule.value = normalizeDsl(conditions);
    } else {
      ruleId.value = null;
      rootRule.value = { all: [] };
    }
  } catch (e) {
    rootRule.value = { all: [] };
  }
}

// 将后端返回的 conditions 规范化为本地编辑结构
function normalizeDsl(node) {
  if (!node || typeof node !== 'object') return { all: [] };
  if (Array.isArray(node.all)) return { all: node.all.map(normalizeChild) };
  if (Array.isArray(node.any)) return { any: node.any.map(normalizeChild) };
  return { all: [] };
}
function normalizeChild(child) {
  if (!child || typeof child !== 'object') return makeLeaf();
  if (Array.isArray(child.all) || Array.isArray(child.any)) return normalizeDsl(child);
  const leaf = { field: child.field || 'type', op: child.op || 'eq', value: child.value ?? '' };
  coerceLeafValue(leaf); // 历史数据 value 可能是字符串/数组混杂，规范成控件期望的形状
  return leaf;
}

// 工厂方法
function makeLeaf() {
  return { field: 'type', op: 'eq', value: '' };
}
function makeGroup() {
  return { all: [makeLeaf()] };
}

// 节点类型判断 / 取子节点
function isGroup(node) {
  return node && (Array.isArray(node.all) || Array.isArray(node.any));
}
function groupKey(node) {
  return Array.isArray(node.all) ? 'all' : 'any';
}
function groupChildren(node) {
  return node[groupKey(node)];
}

// 切换组的 AND/OR（all ↔ any）
function toggleGroupOp(node, nextKey) {
  const curKey = groupKey(node);
  if (curKey === nextKey) return;
  const children = node[curKey];
  delete node[curKey];
  node[nextKey] = children;
}

// 增删条件 / 子组
function addLeaf(group) { groupChildren(group).push(makeLeaf()); }
function addSubGroup(group) { groupChildren(group).push(makeGroup()); }
function removeChild(group, index) { groupChildren(group).splice(index, 1); }

// 字段切换：若当前 op 不再兼容新字段的 kind，重置为第一个可用 op；
// 取值域随字段改变，value 重置为当前 op 形状的空值（有候选项 + 多值 → 空数组，否则空串）。
function onFieldChange(leaf) {
  const kind = FIELDS[leaf.field]?.kind;
  const allowed = Object.entries(OPS).filter(([, def]) => def.allow.includes(kind)).map(([k]) => k);
  if (!allowed.includes(leaf.op)) leaf.op = allowed[0] || 'eq';
  leaf.value = (FIELDS[leaf.field]?.options && OPS[leaf.op]?.multi) ? [] : '';
}

// 操作符切换：在「单值 ↔ 多值」之间重塑 value 形状，尽量保留已填内容。
function onOpChange(leaf) {
  coerceLeafValue(leaf);
}

// 把叶子 value 规范成与「字段是否有候选项 + 当前 op 是否多值」匹配的形状：
//   有候选项 + 多值 → 数组（el-select multiple 绑定数组）
//   有候选项 + 单值 → 字符串（el-select 单选）
//   无候选项（genres）+ 多值 → 逗号串（沿用文本框多值语义）
//   无候选项 + 单值 → 字符串
// 切换 op 或加载历史数据时调用，保证控件绑定类型正确且不丢已填值。
function coerceLeafValue(leaf) {
  const multi = !!OPS[leaf.op]?.multi;
  const hasOpts = !!FIELDS[leaf.field]?.options;
  const toArr = (x) => Array.isArray(x)
    ? x.map((s) => String(s).trim()).filter(Boolean)
    : (typeof x === 'string' && x.trim() ? x.split(',').map((s) => s.trim()).filter(Boolean) : []);
  if (hasOpts && multi) {
    leaf.value = toArr(leaf.value);
  } else if (hasOpts && !multi) {
    const a = toArr(leaf.value);
    leaf.value = a.length ? a[0] : '';
  } else if (!hasOpts && multi) {
    leaf.value = toArr(leaf.value).join(',');
  } else {
    const a = toArr(leaf.value);
    leaf.value = a.length ? a[0] : (typeof leaf.value === 'string' ? leaf.value : '');
  }
}

// 当前字段允许的 op 列表
function opsForField(field) {
  const kind = FIELDS[field]?.kind;
  return Object.entries(OPS).filter(([, def]) => def.allow.includes(kind));
}

// 当前字段的候选项（type/originalLanguage/originCountry 配置了 options；genres 无，返回 null 走文本框）
function enumOptions(field) {
  return FIELDS[field]?.options || null;
}

// 递归校验：所有叶子的 field/op/value 均非空，且至少 1 个叶子
function collectLeaves(node, bag) {
  if (isGroup(node)) {
    for (const c of groupChildren(node)) collectLeaves(c, bag);
  } else {
    bag.push(node);
  }
}
function validate() {
  const leaves = [];
  collectLeaves(rootRule.value, leaves);
  if (leaves.length === 0) {
    ElMessage.warning('至少需要 1 个条件');
    return false;
  }
  for (const leaf of leaves) {
    if (!leaf.field || !leaf.op) {
      ElMessage.warning('存在未填写字段或操作符的条件');
      return false;
    }
    const v = leaf.value;
    const empty = v === null || v === undefined
      || (typeof v === 'string' && v.trim() === '')
      || (Array.isArray(v) && v.length === 0);
    if (empty) {
      ElMessage.warning(`字段「${FIELDS[leaf.field]?.label || leaf.field}」的值不能为空`);
      return false;
    }
  }
  return true;
}

// 将编辑结构转为提交给后端的 DSL（multi 操作 in/notIn 把逗号串转字符串数组；4 个字段均为字符串/枚举，无数值转换）
function buildPayloadDsl(node) {
  if (isGroup(node)) {
    const key = groupKey(node);
    return { [key]: groupChildren(node).map(buildPayloadDsl) };
  }
  let value = node.value;
  if (OPS[node.op]?.multi) {
    // 多值：el-select 多选给数组、genres 文本框给逗号串，统一规整为去空数组
    const arr = Array.isArray(value) ? value : String(value ?? '').split(',');
    value = arr.map((s) => String(s).trim()).filter(Boolean);
  }
  return { field: node.field, op: node.op, value };
}

// 实时预览 JSON（模板里展示）
const previewJson = computed(() => {
  try { return JSON.stringify(buildPayloadDsl(rootRule.value), null, 2); }
  catch { return '{}'; }
});

// 保存：根据 ruleId 决定 create / update
async function onSave() {
  if (saving.value) return;
  if (!validate()) return;
  saving.value = true;
  try {
    // 后端 Conditions 列存 JSON 字符串（DTO 为 string，服务端 JsonDocument.Parse 校验合法性）；
    // 提交前必须 stringify——与 loadRule 里 JSON.parse(string→对象) 对称。
    // 漏 stringify 会把对象塞进 string 形参，触发模型绑定失败（HTTP 400，规则保存失败）。
    const conditions = JSON.stringify(buildPayloadDsl(rootRule.value));
    const payload = {
      categoryId: Number(props.id),
      name: name.value || `规则 #${Date.now()}`,
      priority: Number(priority.value) || 100,
      enabled: enabled.value,
      conditions,
    };
    if (ruleId.value) {
      await api.categoryMatchRules.update(ruleId.value, { ...payload, rowVersion: ruleRowVersion.value });
      ElMessage.success('规则已更新');
    } else {
      await api.categoryMatchRules.create(payload);
      ElMessage.success('规则已创建');
    }
    router.push('/settings/categories');
  } catch (e) {
    // http.js 拦截器已自动 ElMessage.error，此处兜底防中断
  } finally {
    saving.value = false;
  }
}

// 仅测试：弹窗展示生成的 DSL JSON（后端暂无 evaluate 端点）
function onTest() {
  if (!validate()) return;
  ElMessageBox.alert(
    `<pre style="text-align:left;font-family:Consolas,Menlo,monospace;font-size:12px;line-height:1.5;margin:0;white-space:pre-wrap;">${escapeHtml(previewJson.value)}</pre>`,
    '生成的规则 JSON',
    { dangerouslyUseHTMLString: true, confirmButtonText: '关闭', customClass: 'dsl-preview-dialog' },
  ).catch(() => {});
}
function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function onCancel() {
  router.push('/settings/categories');
}

onMounted(async () => {
  await loadCategory();
  await loadRule();
});

// ----------------------------------------------------------------------------
// 递归子组件 RuleGroup —— 渲染单个组（all/any）及其 children
// 采用 defineComponent + template 字符串，避免依赖额外 .js 文件
// ----------------------------------------------------------------------------
const RuleGroup = defineComponent({
  name: 'RuleGroup',
  components: { PmmIcon },
  props: {
    group: { type: Object, required: true },
    isRoot: { type: Boolean, default: false },
    fields: { type: Object, required: true },
    ops: { type: Object, required: true },
    opsForField: { type: Function, required: true },
    enumOptions: { type: Function, required: true },
    onFieldChange: { type: Function, required: true },
    onOpChange: { type: Function, required: true },
    onToggleOp: { type: Function, required: true },
    onAddLeaf: { type: Function, required: true },
    onAddGroup: { type: Function, required: true },
    onRemoveChild: { type: Function, required: true },
  },
  setup() {
    function isGroupNode(n) { return n && (Array.isArray(n.all) || Array.isArray(n.any)); }
    function keyOf(n) { return Array.isArray(n.all) ? 'all' : 'any'; }
    function childrenOf(n) { return n[keyOf(n)]; }
    return { isGroupNode, keyOf, childrenOf };
  },
  template: `
    <div class="tg-box" :class="{ 'is-any': keyOf(group) === 'any' }">
      <div class="tg-head">
        <div class="op-toggle">
          <button :class="{ active: keyOf(group) === 'all', and: true }" @click="onToggleOp(group, 'all')">AND（all）</button>
          <button :class="{ active: keyOf(group) === 'any', or: true }" @click="onToggleOp(group, 'any')">OR（any）</button>
        </div>
        <span class="muted xs">{{ keyOf(group) === 'all' ? '全部条件都需满足' : '任一条件满足即可' }}</span>
      </div>

      <div class="tg-children">
        <template v-for="(child, i) in childrenOf(group)" :key="i">
          <!-- 子组：递归 -->
          <RuleGroup
            v-if="isGroupNode(child)"
            :group="child"
            :is-root="false"
            :fields="fields"
            :ops="ops"
            :ops-for-field="opsForField"
            :enum-options="enumOptions"
            :on-field-change="onFieldChange"
            :on-op-change="onOpChange"
            :on-toggle-op="onToggleOp"
            :on-add-leaf="onAddLeaf"
            :on-add-group="onAddGroup"
            :on-remove-child="onRemoveChild"
          />
          <!-- 叶子：field / op / value -->
          <div v-else class="cond-row">
            <select class="select" v-model="child.field" @change="onFieldChange(child)" style="width: 200px">
              <option v-for="(def, key) in fields" :key="key" :value="key">{{ def.label }}</option>
            </select>
            <select class="select" v-model="child.op" @change="onOpChange(child)" style="width: 200px">
              <option v-for="[k, def] in opsForField(child.field)" :key="k" :value="k">{{ def.label }}</option>
            </select>
            <!-- 有候选项字段（type/originalLanguage/originCountry）：可搜索 el-select；多值操作符 → 多选 -->
            <el-select
              v-if="enumOptions(child.field)"
              v-model="child.value"
              :multiple="!!ops[child.op]?.multi"
              filterable
              :allow-create="!!fields[child.field]?.allowCreate"
              default-first-option
              :reserve-keyword="false"
              collapse-tags
              collapse-tags-tooltip
              clearable
              :placeholder="fields[child.field]?.placeholder || '搜索或选择'"
              style="flex: 1"
            >
              <el-option v-for="opt in enumOptions(child.field)" :key="opt.value" :label="opt.label" :value="opt.value" />
            </el-select>
            <!-- 无候选项字段（genres）：保留逗号分隔文本框 -->
            <input
              v-else
              class="input"
              type="text"
              v-model="child.value"
              :placeholder="ops[child.op]?.multi ? '逗号分隔多个值' : (fields[child.field]?.placeholder || '值')"
              style="flex: 1"
            />
            <button class="icon-btn" style="width: 28px; height: 28px" @click="onRemoveChild(group, i)" title="删除此条件">
              <PmmIcon name="x" :size="14" />
            </button>
          </div>
        </template>
      </div>

      <div class="tg-add">
        <button class="btn btn-sm btn-ghost" @click="onAddLeaf(group)">
          <PmmIcon name="plus" :size="12" /> + 条件
        </button>
        <button class="btn btn-sm btn-ghost" @click="onAddGroup(group)">
          <PmmIcon name="plus" :size="12" /> + 子组
        </button>
      </div>
    </div>
  `,
});
</script>

<template>
  <div class="page">
    <div class="back-bar">
      <button class="btn btn-ghost btn-sm" @click="onCancel">
        <PmmIcon name="chevronLeft" :size="14" /> 返回分类列表
      </button>
      <span class="muted small">分类</span>
      <span class="bc-cur">{{ category?.name || '加载中…' }}</span>
      <span class="muted small">/</span>
      <span class="bc-cur" style="color: var(--accent)">匹配规则</span>
    </div>

    <PmmPageHeader
      eyebrow="设置 / 媒体分类"
      title="匹配规则编辑器"
      subtitle="组合条件（all=且 / any=或，可嵌套）匹配媒体项，命中后自动归入此分类。"
    >
      <template #actions>
        <button class="btn btn-ghost" @click="onTest">
          <PmmIcon name="play" :size="14" /> 仅测试不保存
        </button>
        <button class="btn btn-ghost" @click="onCancel">取消</button>
        <button class="btn btn-primary" :disabled="saving" @click="onSave">
          <PmmIcon name="check" :size="14" /> {{ saving ? '保存中…' : '保存规则' }}
        </button>
      </template>
    </PmmPageHeader>

    <!-- 顶部基础信息 -->
    <div class="card top-strip">
      <div class="field" style="flex: 2">
        <div class="field-label">规则名称</div>
        <input class="input" v-model="name" placeholder="例：国产剧（type=tv & origin=CN）" />
      </div>
      <div class="field">
        <div class="field-label">优先级</div>
        <input class="input tabular" type="number" v-model.number="priority" />
      </div>
      <div class="field">
        <div class="field-label">命中后归入</div>
        <input class="input" :value="category?.name || ''" disabled />
      </div>
      <label class="hstack" style="gap: 8px; padding-bottom: 8px; cursor: pointer">
        <div class="switch" :class="{ on: enabled }" @click="enabled = !enabled" />
        <span style="font-size: 13px; font-weight: 600">启用</span>
      </label>
    </div>

    <!-- 条件树 -->
    <section class="card">
      <header class="card-head">
        <div>
          <h3 class="h3">条件树</h3>
          <div class="muted small">支持 all/any 嵌套；叶子为 (field, op, value)。</div>
        </div>
      </header>

      <div class="tree-pad">
        <RuleGroup
          :group="rootRule"
          :is-root="true"
          :fields="FIELDS"
          :ops="OPS"
          :ops-for-field="opsForField"
          :enum-options="enumOptions"
          :on-field-change="onFieldChange"
          :on-op-change="onOpChange"
          :on-toggle-op="toggleGroupOp"
          :on-add-leaf="addLeaf"
          :on-add-group="addSubGroup"
          :on-remove-child="removeChild"
        />
      </div>

      <div class="code-block">
        <div class="eyebrow xs" style="margin-bottom: 6px">等效 JSON DSL（提交后端的权威格式）</div>
        <pre class="code-pre font-mono">{{ previewJson }}</pre>
      </div>
    </section>
  </div>
</template>

<style scoped lang="scss">
.back-bar { display: flex; align-items: center; gap: 10px; margin-bottom: 14px; }
.bc-cur { font-size: 12px; font-weight: 600; }
.small { font-size: 11.5px; }
.xs { font-size: 10.5px; }

.top-strip {
  padding: 18px;
  margin-bottom: 18px;
  display: grid;
  grid-template-columns: 2fr 1fr 1fr auto;
  gap: 16px;
  align-items: flex-end;
}
.field { display: flex; flex-direction: column; gap: 6px; }
.field-label { font-size: 12.5px; font-weight: 600; color: var(--text); }

.tree-pad { padding: 18px; }
.code-block { padding: 0 18px 18px; }
.code-pre {
  padding: 14px;
  background: var(--bg-elev);
  border-radius: 6px;
  font-size: 12px;
  color: var(--text);
  line-height: 1.6;
  margin: 0;
  white-space: pre-wrap;
}
</style>

<style lang="scss">
/* RuleGroup 内部样式（不能 scoped，否则递归子组件失效） */
.tg-box {
  position: relative;
  border-left: 3px solid var(--info);
  border-radius: 6px;
  padding: 12px 14px 12px 18px;
  background: color-mix(in oklab, var(--info) 6%, var(--surface));
}
.tg-box.is-any {
  border-left-color: var(--accent);
  background: color-mix(in oklab, var(--accent) 6%, var(--surface));
}
.tg-head { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; }
.op-toggle {
  display: flex;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 2px;
}
.op-toggle button {
  padding: 3px 12px; border-radius: 4px; font-size: 11.5px; font-weight: 700;
  background: transparent; border: 0; cursor: pointer; color: var(--text-mute);
}
.op-toggle button.active.and { background: var(--info); color: white; }
.op-toggle button.active.or { background: var(--accent); color: white; }
.tg-children { display: flex; flex-direction: column; gap: 8px; }
.tg-add { display: flex; gap: 6px; margin-top: 10px; }

.cond-row {
  display: flex; align-items: center; gap: 8px;
  padding: 10px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 6px;
}

.dsl-preview-dialog { width: 560px; max-width: 90vw; }
</style>
