<script setup>
// 归档命名模板自定义（识别归类 → 归档命名）
// 数据契约：
//   GET  /api/settings/archive-naming        → { movieNameTemplate, tvShowFolderTemplate, tvEpisodeNameTemplate, seasonFolderIncludeTitle }
//   POST /api/settings/archive-naming/update  → 同上字段（非法模板后端返 1000 + message）
//   POST /api/settings/archive-naming/preview → { movieErrors[], tvShowFolderErrors[], tvEpisodeErrors[], samples:[{label,relativePath,error}] }
// 安全边界：{tmdb-NNN} 锚点 / Season NN 季目录 / SxxEyy 集号 / (year) 年份 / 伴随文件名（tvshow.nfo/poster.jpg）均由系统锁定，不进模板。
// 仅对今后新归档生效；已归档文件保持原命名（横幅明确告知）。
import { computed, onMounted, ref, watch } from 'vue';
import { ElMessage } from 'element-plus';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

const form = ref({
  movieNameTemplate: '{title} ({year})',
  tvShowFolderTemplate: '{title} ({year})',
  tvEpisodeNameTemplate: '{title} ({year}) - {episode}',
  seasonFolderIncludeTitle: true,
});
const original = ref({});
const loading = ref(false);
const saving = ref(false);

// 预览结果（后端固定示例渲染 + 各模板校验错误清单）
const preview = ref({ movieErrors: [], tvShowFolderErrors: [], tvEpisodeErrors: [], samples: [] });

// 预设（纯前端常量，不入库）：三套根目录/季目录/{tmdb-NNN}/SxxEyy 结构完全一致，仅单集文件名主体差异
const PRESETS = {
  plex: {
    label: 'Plex 默认',
    movieNameTemplate: '{title} ({year})',
    tvShowFolderTemplate: '{title} ({year})',
    tvEpisodeNameTemplate: '{title} ({year}) - {episode}',
  },
  emby: {
    label: 'Emby 风格（单集去「 - 」）',
    movieNameTemplate: '{title} ({year})',
    tvShowFolderTemplate: '{title} ({year})',
    tvEpisodeNameTemplate: '{title} ({year}) {episode}',
  },
  jellyfin: {
    label: 'Jellyfin 风格（单集省年）',
    movieNameTemplate: '{title} ({year})',
    tvShowFolderTemplate: '{title} ({year})',
    tvEpisodeNameTemplate: '{title} - {episode}',
  },
};

// 可用占位符（点击插入到聚焦的输入框）；episode 仅单集、edition 仅电影、seasonyear 仅单集
const TOKENS = {
  movie: [
    { t: '{title}', d: 'TMDB 中文名（缺回退原文 / 解析名）' },
    { t: '{originaltitle}', d: 'TMDB 原文名（空回退中文名）' },
    { t: '{year}', d: '发行年（裸数字，括号写模板里）' },
    { t: '{tmdbid}', d: 'TMDB 数字 id（≠ 锚点标记）' },
    { t: '{edition}', d: '版本（导演剪辑版等；空则省略所在段）' },
  ],
  tvShow: [
    { t: '{title}', d: 'TMDB 中文名（缺回退原文 / 解析名）' },
    { t: '{originaltitle}', d: 'TMDB 原文名（空回退中文名）' },
    { t: '{year}', d: '整剧首播年' },
    { t: '{tmdbid}', d: 'TMDB 数字 id' },
  ],
  tvEpisode: [
    { t: '{title}', d: 'TMDB 中文名' },
    { t: '{originaltitle}', d: 'TMDB 原文名' },
    { t: '{year}', d: '该季首播年（缺回退整剧年）' },
    { t: '{seasonyear}', d: '该季首播年（同上，语义更明确）' },
    { t: '{episode}', d: 'SxxEyy 集号（必含，格式锁定）' },
  ],
};

// 当前聚焦的模板字段名（用于占位符 chip 插入到正确的输入框）
const focusedField = ref('movieNameTemplate');
const fieldRefs = { movieNameTemplate: ref(null), tvShowFolderTemplate: ref(null), tvEpisodeNameTemplate: ref(null) };

const dirty = computed(() =>
  JSON.stringify(form.value) !== JSON.stringify(original.value));

const hasErrors = computed(() =>
  (preview.value.movieErrors?.length || 0)
  + (preview.value.tvShowFolderErrors?.length || 0)
  + (preview.value.tvEpisodeErrors?.length || 0) > 0);

async function load() {
  loading.value = true;
  try {
    const data = (await api.archiveNaming.get()) || {};
    form.value = { ...form.value, ...data };
    original.value = { ...form.value };
    await runPreview();
  } finally {
    loading.value = false;
  }
}

let previewTimer = null;
function schedulePreview() {
  if (previewTimer) clearTimeout(previewTimer);
  previewTimer = setTimeout(runPreview, 300); // 防抖 300ms
}

async function runPreview() {
  try {
    preview.value = (await api.archiveNaming.preview({ ...form.value })) || preview.value;
  } catch {
    // 预览失败（如三模板任一为空被绑定层拦）不打断编辑，仅保留上次结果
  }
}

watch(form, schedulePreview, { deep: true });

async function save() {
  if (hasErrors.value) {
    ElMessage.warning('存在校验未通过的模板，请先修正再保存');
    return;
  }
  saving.value = true;
  try {
    await api.archiveNaming.update({ ...form.value });
    ElMessage.success('已保存；仅对今后新归档生效');
    original.value = { ...form.value };
  } finally {
    saving.value = false;
  }
}

function revert() {
  form.value = { ...original.value };
}

function applyPreset(key) {
  const p = PRESETS[key];
  if (!p) return;
  form.value = {
    ...form.value,
    movieNameTemplate: p.movieNameTemplate,
    tvShowFolderTemplate: p.tvShowFolderTemplate,
    tvEpisodeNameTemplate: p.tvEpisodeNameTemplate,
  };
}

// 把占位符插入当前聚焦的模板输入框光标处（无光标信息则追加到末尾）
function insertToken(token) {
  const field = focusedField.value;
  const el = fieldRefs[field]?.value;
  const cur = form.value[field] ?? '';
  if (el && typeof el.selectionStart === 'number') {
    const s = el.selectionStart;
    const e = el.selectionEnd;
    form.value[field] = cur.slice(0, s) + token + cur.slice(e);
    // 还原光标到插入点之后
    requestAnimationFrame(() => {
      el.focus();
      const pos = s + token.length;
      el.setSelectionRange(pos, pos);
    });
  } else {
    form.value[field] = cur + token;
  }
}

const currentTokens = computed(() => {
  if (focusedField.value === 'movieNameTemplate') return TOKENS.movie;
  if (focusedField.value === 'tvShowFolderTemplate') return TOKENS.tvShow;
  return TOKENS.tvEpisode;
});

// 把预览路径里「系统锁定的刮削必需片段」高亮：先 HTML 转义防注入，再用 span 包住锁定片段
// 锁定片段：{tmdb-数字} / Season 两位数字 / SxxEyy(可带 -Ezz)
function escapeHtml(s) {
  return String(s ?? '')
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}
function highlightLocked(path) {
  const re = /(\{tmdb-\d+\})|(Season \d{2})|(S\d{2}E\d{2}(?:-E\d{2})?)/g;
  const raw = String(path ?? '');
  let out = '';
  let last = 0;
  let m;
  while ((m = re.exec(raw)) !== null) {
    if (m.index > last) out += escapeHtml(raw.slice(last, m.index));
    out += `<span class="lock-seg" title="系统锁定·刮削必需">${escapeHtml(m[0])}</span>`;
    last = m.index + m[0].length;
  }
  if (last < raw.length) out += escapeHtml(raw.slice(last));
  return out;
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="识别归类"
      title="归档命名"
      subtitle="自定义归档后的 Plex 命名模板。可锚定标记 {tmdb-NNN}、季目录 Season NN、集号 SxxEyy 等刮削必需结构由系统锁定，不可改。"
    >
      <template #actions>
        <button class="btn btn-ghost btn-sm" @click="revert" :disabled="!dirty">撤销</button>
        <button class="btn btn-primary btn-sm" @click="save" :disabled="saving || hasErrors">
          <PmmIcon name="check" :size="14" /> 保存配置
        </button>
      </template>
    </PmmPageHeader>

    <!-- 横幅：仅对今后新归档生效 -->
    <div class="notice">
      <PmmIcon name="info" :size="16" />
      <span>修改命名模板<b>仅对今后新归档生效</b>；已归档文件保持原命名，不会自动重整。{tmdb-NNN} 复用兜底仍生效，改模板后新文件仍落进已存在的旧目录、不裂第二个文件夹。</span>
    </div>

    <!-- 预设 -->
    <section class="section-card">
      <header class="section-head"><h3>快速预设</h3><span class="section-hint">三套结构一致，仅单集文件名主体差异；选后仍可手动微调</span></header>
      <div class="preset-row">
        <button v-for="(p, k) in PRESETS" :key="k" class="btn" @click="applyPreset(k)">{{ p.label }}</button>
      </div>
    </section>

    <!-- 模板输入 -->
    <section class="section-card">
      <header class="section-head"><h3>命名模板</h3></header>

      <div class="form-row">
        <div>
          <div class="label">电影目录 + 文件名</div>
          <div class="hint">电影目录名与文件名主体同源。系统在末尾自动追加 <code>{tmdb-NNN}</code> 与扩展名。</div>
        </div>
        <div class="control control-stack">
          <input
            class="input font-mono" :ref="fieldRefs.movieNameTemplate" v-model="form.movieNameTemplate"
            @focus="focusedField = 'movieNameTemplate'" placeholder="{title} ({year})" style="max-width: 480px" />
          <div v-if="preview.movieErrors?.length" class="field-errors">
            <div v-for="(e, i) in preview.movieErrors" :key="i">⚠ {{ e }}</div>
          </div>
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">剧集根目录</div>
          <div class="hint">整部剧的顶层文件夹主体。系统在末尾自动追加 <code>{tmdb-NNN}</code>。</div>
        </div>
        <div class="control control-stack">
          <input
            class="input font-mono" :ref="fieldRefs.tvShowFolderTemplate" v-model="form.tvShowFolderTemplate"
            @focus="focusedField = 'tvShowFolderTemplate'" placeholder="{title} ({year})" style="max-width: 480px" />
          <div v-if="preview.tvShowFolderErrors?.length" class="field-errors">
            <div v-for="(e, i) in preview.tvShowFolderErrors" :key="i">⚠ {{ e }}</div>
          </div>
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">单集文件名</div>
          <div class="hint">单集 / 双集文件名主体。<b>必须包含</b> <code>{episode}</code>（渲染为 <code>SxxEyy</code>，集号格式锁定）。</div>
        </div>
        <div class="control control-stack">
          <input
            class="input font-mono" :ref="fieldRefs.tvEpisodeNameTemplate" v-model="form.tvEpisodeNameTemplate"
            @focus="focusedField = 'tvEpisodeNameTemplate'" placeholder="{title} ({year}) - {episode}" style="max-width: 480px" />
          <div v-if="preview.tvEpisodeErrors?.length" class="field-errors">
            <div v-for="(e, i) in preview.tvEpisodeErrors" :key="i">⚠ {{ e }}</div>
          </div>
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">季目录追加季标题</div>
          <div class="hint">开启 → <code>Season 03 锻刀村篇</code>；关闭 → 纯 <code>Season 03</code>。<b>Season NN 前缀本身锁定</b>，不可改。</div>
        </div>
        <div class="control">
          <label class="toggle-label">
            <input type="checkbox" v-model="form.seasonFolderIncludeTitle" />
            <span>{{ form.seasonFolderIncludeTitle ? '追加篇章名后缀' : '纯 Season NN' }}</span>
          </label>
        </div>
      </div>

      <!-- 可用占位符 chips（点击插入到聚焦输入框）-->
      <div class="tokens-row">
        <span class="tokens-label">可用占位符（点击插入到上方聚焦的输入框）：</span>
        <button v-for="tk in currentTokens" :key="tk.t" class="chip" :title="tk.d" @click="insertToken(tk.t)">{{ tk.t }}</button>
      </div>
    </section>

    <!-- 实时预览 -->
    <section class="section-card">
      <header class="section-head">
        <h3>实时预览</h3>
        <span class="section-hint">用固定示例渲染真实归档落点形态（与实际归档同一管道）</span>
      </header>
      <div class="preview-area">
        <div v-for="(s, i) in preview.samples" :key="i" class="preview-line">
          <div class="preview-label">{{ s.label }}</div>
          <div v-if="s.error" class="preview-path err">⚠ {{ s.error }}</div>
          <!-- eslint-disable-next-line vue/no-v-html — 内容已 escapeHtml 转义，仅锁定片段包 span -->
          <div v-else class="preview-path" v-html="highlightLocked(s.relativePath)"></div>
        </div>
        <div v-if="!preview.samples?.length" class="preview-empty">填入模板后这里显示示例落点。</div>
      </div>
      <div class="locked-note">
        <PmmIcon name="shield" :size="14" />
        高亮片段（<code class="lock">{tmdb-NNN}</code>、<code class="lock">Season NN</code>、<code class="lock">SxxEyy</code>）为系统锁定的刮削必需结构，不受模板影响。
      </div>
    </section>
  </div>
</template>

<style scoped lang="scss">
.notice {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  background: var(--info-soft);
  border: 1px solid color-mix(in oklab, var(--info) 28%, transparent);
  border-radius: 8px;
  padding: 10px 12px;
  font-size: 13px;
  line-height: 1.6;
  color: var(--text);
  margin-bottom: 12px;
}
/* info 图标用主题信息色着色，并防止在多行文本旁被挤压 */
.notice :deep(svg) { color: var(--info); flex-shrink: 0; margin-top: 1px; }
.section-head { display: flex; align-items: baseline; gap: 12px; }
.section-hint { font-size: 12px; color: var(--text-dim); }

.preset-row, .tokens-row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
.tokens-row { padding-top: 10px; border-top: 1px solid var(--border); margin-top: 6px; }
.tokens-label { font-size: 12px; color: var(--text-mute); }

.control-stack { display: flex; flex-direction: column; gap: 6px; align-items: stretch !important; }

.chip {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 12px;
  padding: 3px 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--surface-2);
  color: var(--text);
  cursor: pointer;
  &:hover { background: var(--surface-3); border-color: var(--border-strong); }
}

/* 季标题开关：原 class="switch" 与全局 .switch（34×20 拨动胶囊）撞名，
   会继承固定宽高 + ::after 圆点把标题文字挤成竖排，故改用独立类名 */
.toggle-label { display: inline-flex; align-items: center; gap: 8px; font-size: 13px; cursor: pointer; }

.field-errors {
  font-size: 12px;
  color: var(--danger);
  background: var(--danger-soft);
  border: 1px solid color-mix(in oklab, var(--danger) 30%, transparent);
  border-radius: 6px;
  padding: 5px 9px;
  max-width: 560px;
  line-height: 1.5;
}

.preview-area { display: flex; flex-direction: column; gap: 8px; }
.preview-line { display: flex; flex-direction: column; gap: 2px; }
.preview-label { font-size: 12px; color: var(--text-dim); }
.preview-path {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 13px;
  word-break: break-all;
  color: var(--text);
  &.err { color: var(--danger); }
}
.preview-empty { font-size: 13px; color: var(--text-dim); }

.locked-note {
  display: flex; align-items: center; gap: 6px;
  font-size: 12px; color: var(--text-mute);
  margin-top: 10px; padding-top: 8px;
  border-top: 1px dashed var(--border);
}

code, .hint code {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  background: var(--surface-2);
  color: var(--text);
  padding: 1px 4px; border-radius: 3px; font-size: 12px;
}
code.lock { background: var(--warning-soft); color: var(--warning); }

/* 预览路径里锁定片段高亮 */
:deep(.lock-seg) {
  background: var(--warning-soft);
  color: var(--warning);
  border-radius: 3px;
  padding: 0 2px;
}
</style>
