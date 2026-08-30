<script setup>
// 通用设置页（r1 P0-5 + r2 P2-11）
// 数据契约（GET /api/settings/general）：
//   { groups: { [groupName]: [{ key, value, category, description }] } }
// 写入契约（POST /api/settings/general/update）：
//   { items: [{ key, value }] }
// 注意：
// 1) 后端 setting 表 key 由后端定义（System_TargetRoot 等），前端不硬编码任何字段名；
// 2) 后端目前未返回 valueType，若返回则按 string/int/number/bool/boolean 渲染对应控件，未返回时一律 string 输入框；
// 3) value 永远以字符串发回后端（System_Setting KV 为字符串存储）。
import { computed, onMounted, reactive, ref } from 'vue';
import { ElMessage } from 'element-plus';
import { api } from '@/api';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

// form: { [groupName]: { [key]: { value, originalValue, description, valueType, category } } }
const form = reactive({});
const loading = ref(false);
const saving = ref(false);

// 分组标题中文映射（Category 字段在 DB 中保持英文做 GroupBy 与索引，仅展示时翻译）
// 未在表中的新分组按原文展示，提示开发者补齐
const GROUP_LABELS = {
  General: '通用',
  Parse: '解析',
  Tmdb: 'TMDB',
  Log: '日志',
  Webhook: 'Webhook 推送',
  Archive: '归档',
  Backup: '备份',
  Audio: '音频',
};

function groupLabel(name) {
  return GROUP_LABELS[name] || name;
}

// 枚举值中文 label（仅对已知枚举设置项本地化；未列出的 value 原样展示，保证通用渲染器对未来新 enum 仍可用）。
// 当前唯一 enum 设置 = Archive_ConflictPolicy（同名冲突策略）。
const ENUM_VALUE_LABELS = {
  Archive_ConflictPolicy: {
    Skip: '跳过（不覆盖已有文件）',
    Overwrite: '升级替换（新文件更大才替换）',
    KeepBoth: '保留多版本（加序号共存）',
    Ask: '询问（放入待确认，人工决定是否覆盖）',
  },
};

function optionLabel(key, opt) {
  return ENUM_VALUE_LABELS[key]?.[opt] ?? opt;
}

// 部分设置项的「帮助跳转」链接（针对特定 key 定制）：当前用于引导用户下载自备的 ffmpeg
// （音频不兼容轨探测 / 重混依赖外部 ffprobe/ffmpeg，本项目不捆绑）
const SETTING_HELP_LINKS = {
  Audio_FfmpegPath: { text: '下载 ffmpeg', url: 'https://ffmpeg.org/download.html' },
};

// 常规页只展示「没有专属设置页」的分组：通用(General) / 解析(Parse) / 日志(Log) / 归档(Archive)。
// Tmdb / Webhook / Proxy / Update 各有专属页（TMDB / Webhook / 代理 / 软件更新），在此隐藏，
// 避免与专属页重复编辑同一批配置——且专属页多带范围校验，常规页是裸 KV，重复入口易写入非法值。
const VISIBLE_GROUPS = new Set(['General', 'Parse', 'Log', 'Archive', 'Backup', 'Audio']);

// 变更项数量（顶部计数 + 保存按钮禁用判断）
const changedCount = computed(() => {
  let n = 0;
  for (const groupName of Object.keys(form)) {
    const items = form[groupName];
    for (const key of Object.keys(items)) {
      if (!isSameValue(items[key].value, items[key].originalValue)) {
        n += 1;
      }
    }
  }
  return n;
});

/**
 * 比较两值是否相等（bool/number/string 跨类型按字符串比较，避免 "true"/true 误判变更）
 */
function isSameValue(a, b) {
  if (a === b) return true;
  if (a === null || a === undefined) return b === null || b === undefined || b === '';
  if (b === null || b === undefined) return a === '';
  return String(a) === String(b);
}

/**
 * 根据 valueType 把后端字符串 value 转成前端表单初值
 */
function parseValue(raw, valueType) {
  if (raw === null || raw === undefined) {
    if (valueType === 'bool' || valueType === 'boolean') return false;
    if (valueType === 'int' || valueType === 'number') return 0;
    return '';
  }
  switch (valueType) {
    case 'bool':
    case 'boolean':
      return String(raw).toLowerCase() === 'true';
    case 'int':
    case 'number': {
      const n = Number(raw);
      return Number.isFinite(n) ? n : 0;
    }
    default:
      return String(raw);
  }
}

async function load() {
  loading.value = true;
  try {
    const data = (await api.generalSettings.get()) || {};
    const groups = data.groups || {};
    // 清空既有 form
    for (const k of Object.keys(form)) delete form[k];
    for (const groupName of Object.keys(groups)) {
      if (!VISIBLE_GROUPS.has(groupName)) continue; // 跳过有专属页的分组，避免重复编辑
      const bucket = {};
      const items = groups[groupName] || [];
      for (const it of items) {
        if (!it || !it.key) continue;
        const valueType = it.valueType || 'string';
        const initial = parseValue(it.value, valueType);
        bucket[it.key] = {
          value: initial,
          originalValue: initial,
          description: it.description || '',
          valueType,
          options: it.options || [],
          category: it.category || groupName,
        };
      }
      form[groupName] = bucket;
    }
  } finally {
    loading.value = false;
  }
}

async function save() {
  // 收集变更项
  const items = [];
  for (const groupName of Object.keys(form)) {
    const bucket = form[groupName];
    for (const key of Object.keys(bucket)) {
      const it = bucket[key];
      if (!isSameValue(it.value, it.originalValue)) {
        // 后端 KV 存字符串，统一 String 化（bool 转 "true"/"false"，number 转字面量）
        items.push({ key, value: String(it.value) });
      }
    }
  }
  if (items.length === 0) {
    ElMessage.info('没有需要更新的配置项');
    return;
  }
  saving.value = true;
  try {
    await api.generalSettings.update({ items });
    ElMessage.success(`已保存 ${items.length} 项配置`);
    // 布局常驻的待确认提醒订阅需立即应用本页开关，无需用户刷新页面。
    window.dispatchEvent(new CustomEvent('pmm:general-settings-updated', { detail: { items } }));
    // 重新拉取并刷新 originalValue
    await load();
  } finally {
    saving.value = false;
  }
}

function revert() {
  for (const groupName of Object.keys(form)) {
    const bucket = form[groupName];
    for (const key of Object.keys(bucket)) {
      bucket[key].value = bucket[key].originalValue;
    }
  }
  ElMessage.info('已重置全部变更');
}

// ffmpeg 路径自检：音频不兼容轨探测 / 重混依赖外部 ffprobe/ffmpeg，配好路径后点「测试可用性」即时验证。
// 用当前输入框的值测（未保存也能验证），免去「先保存再测」的来回。
const FFMPEG_PATH_KEY = 'Audio_FfmpegPath';
const ffmpegTesting = ref(false);

async function testFfmpeg(path) {
  ffmpegTesting.value = true;
  try {
    // 后端统一返 200 + data.success（工具缺失 / 不可执行不抛异常），必须看 success 才能区分成败
    const r = (await api.generalSettings.testFfmpeg((path || '').trim())) || {};
    ElMessage({
      type: r.success ? 'success' : 'error',
      message: r.message || (r.success ? 'ffmpeg 可用' : 'ffmpeg 不可用'),
      duration: r.success ? 3000 : 6000,
    });
  } finally {
    ffmpegTesting.value = false;
  }
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="设置"
      title="常规"
      subtitle="编辑通用 / 解析 / 日志 / 归档 / 音频等无专属页面的配置项；TMDB、Webhook、代理、软件更新请到各自专属页设置。修改后点击保存立即生效。"
    />

    <div class="toolbar">
      <span class="muted">
        共 {{ Object.keys(form).length }} 个分组，
        <span :class="{ 'changed-hint': changedCount > 0 }">{{ changedCount }} 项待保存</span>
      </span>
      <div class="toolbar-actions">
        <el-button :disabled="loading || saving" @click="load">重新加载</el-button>
        <el-button :disabled="loading || saving || changedCount === 0" @click="revert">重置变更</el-button>
        <el-button type="primary" :loading="saving" :disabled="loading || changedCount === 0" @click="save">
          保存
        </el-button>
      </div>
    </div>

    <el-empty v-if="!loading && Object.keys(form).length === 0" description="后端未返回任何设置项" />

    <template v-else>
      <section
        v-for="(items, groupName) in form"
        :key="groupName"
        class="section-card"
      >
        <header class="section-head"><h3>{{ groupLabel(groupName) }}</h3></header>
        <div
          v-for="(item, key) in items"
          :key="key"
          class="form-row"
        >
          <div>
            <div class="label">{{ item.description || key }}</div>
            <div class="hint">
              <span class="key-mono">{{ key }}</span>
              <a
                v-if="SETTING_HELP_LINKS[key]"
                :href="SETTING_HELP_LINKS[key].url"
                target="_blank"
                rel="noopener noreferrer"
                class="help-link"
              >{{ SETTING_HELP_LINKS[key].text }} ↗</a>
              <span
                v-if="!isSameValue(item.value, item.originalValue)"
                class="changed-badge"
              >已修改</span>
            </div>
          </div>
          <div class="control">
            <el-input-number
              v-if="item.valueType === 'int' || item.valueType === 'number'"
              v-model.number="item.value"
              :controls-position="'right'"
              style="width: 200px"
            />
            <el-switch
              v-else-if="item.valueType === 'bool' || item.valueType === 'boolean'"
              v-model="item.value"
            />
            <el-select
              v-else-if="item.valueType === 'enum'"
              v-model="item.value"
              style="width: 240px"
            >
              <el-option v-for="opt in item.options" :key="opt" :label="optionLabel(key, opt)" :value="opt" />
            </el-select>
            <template v-else>
              <el-input
                v-model="item.value"
                :placeholder="item.description || key"
                clearable
                :style="{ maxWidth: key === FFMPEG_PATH_KEY ? '360px' : '460px' }"
              />
              <el-button
                v-if="key === FFMPEG_PATH_KEY"
                class="ffmpeg-test-btn"
                :loading="ffmpegTesting"
                @click="testFfmpeg(item.value)"
              >测试可用性</el-button>
            </template>
          </div>
        </div>
      </section>
    </template>
  </div>
</template>

<style scoped lang="scss">
.toolbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin: 16px 0 12px;
  gap: 12px;
  flex-wrap: wrap;
}
.toolbar-actions {
  display: flex;
  gap: 8px;
}
.muted {
  color: var(--el-text-color-secondary, #909399);
  font-size: 13px;
}
.changed-hint {
  color: var(--el-color-warning, #e6a23c);
  font-weight: 600;
}
.key-mono {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}
.help-link {
  margin-left: 8px;
  font-size: 11px;
  color: var(--el-color-primary, #409eff);
  text-decoration: none;
}
.help-link:hover {
  text-decoration: underline;
}
.changed-badge {
  margin-left: 8px;
  padding: 0 6px;
  border-radius: 4px;
  background: var(--el-color-warning-light-8, #faecd8);
  color: var(--el-color-warning, #e6a23c);
  font-size: 11px;
}
/* ffmpeg 路径行的「测试可用性」按钮：与输入框留间距（control 为全局 flex 容器） */
.ffmpeg-test-btn {
  margin-left: 8px;
  flex-shrink: 0;
}
</style>
