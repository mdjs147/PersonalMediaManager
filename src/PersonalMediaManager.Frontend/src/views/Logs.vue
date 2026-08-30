<script setup>
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { api } from '@/api';
import { tokenStorage } from '@/api/http';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

const route = useRoute();
const lines = ref([]);
const paused = ref(false);
const keyword = ref(typeof route.query.keyword === 'string' ? route.query.keyword : '');
const levels = ref({ Information: true, Warning: true, Error: true, Debug: false });
let conn = null;

const visible = computed(() => {
  return lines.value.filter((l) => {
    const lv = (l.level || '').replace(/^./, (c) => c.toUpperCase());
    if (!levels.value[lv]) return false;
    if (keyword.value) {
      const k = keyword.value.toLowerCase();
      if (!(l.message?.toLowerCase().includes(k) || l.source?.toLowerCase().includes(k))) return false;
    }
    return true;
  });
});

const counts = computed(() => {
  const c = { Information: 0, Warning: 0, Error: 0, Debug: 0 };
  for (const l of lines.value) {
    const lv = (l.level || '').replace(/^./, (x) => x.toUpperCase());
    if (c[lv] != null) c[lv]++;
  }
  return c;
});

async function loadHistory() {
  // 拼接当前勾选的级别（首字母大写）→ 后端 LevelFilter 支持单值传参；多选时取第一个
  const enabled = Object.keys(levels.value).filter((k) => levels.value[k]);
  const data = await api.logs.list({
    page: 1,
    pageSize: 100,
    keyword: keyword.value || undefined,
    level: enabled.length === 1 ? enabled[0] : undefined,
  });
  lines.value = (data?.items || []).slice().reverse();
}

// 等价于「loadInit」：level 切换时清空已有缓存重新拉取，避免旧缓存与新过滤错位
function loadInit() {
  return loadHistory();
}

// 监听 level 切换 → 重拉日志（深监听 levels 对象）
watch(levels, () => { loadInit(); }, { deep: true });

function appendLive(entry) {
  if (paused.value) return;
  lines.value.unshift(entry);
  if (lines.value.length > 500) lines.value = lines.value.slice(0, 500);
}

async function connect() {
  conn = new HubConnectionBuilder()
    .withUrl('/hubs/logs', { accessTokenFactory: () => tokenStorage.get() || '' })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
  conn.on('log', appendLive);
  try {
    await conn.start();
  } catch {
    /* 后端未启动时静默，自动重连交给 withAutomaticReconnect */
  }
}

function fmtClock(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return d.toLocaleTimeString('zh-CN', { hour12: false });
}

function levelClass(level) {
  const v = (level || '').toLowerCase();
  if (v.startsWith('err')) return 'lvl-error';
  if (v.startsWith('warn')) return 'lvl-warn';
  if (v.startsWith('debug')) return 'lvl-debug';
  return 'lvl-info';
}

function shortLevel(level) {
  const v = (level || '').toLowerCase();
  if (v.startsWith('err')) return 'ERROR';
  if (v.startsWith('warn')) return 'WARN';
  if (v.startsWith('debug')) return 'DEBUG';
  return 'INFO';
}

function fmtTimestampForFile(d) {
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}`;
}

function fmtIsoFull(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${String(d.getMilliseconds()).padStart(3, '0')}`;
}

// 导出当前过滤条件下可见的日志为 .log 文本：所看即所得，按时间正序排列
function onExport() {
  const rows = visible.value.slice().reverse(); // visible 是倒序（新→旧），导出按时间正序更易读
  const now = new Date();
  const header = [
    `# PMM 日志导出 @ ${fmtIsoFull(now.toISOString())}`,
    `# 过滤条件：keyword="${keyword.value}" levels=[${Object.keys(levels.value).filter((k) => levels.value[k]).join(',')}]`,
    `# 条目数：${rows.length}`,
    '',
  ].join('\n');
  const body = rows
    .map((l) => {
      const ts = fmtIsoFull(l.timestamp);
      const lv = shortLevel(l.level).padEnd(5, ' ');
      const src = l.source ? `[${l.source}] ` : '';
      return `${ts} ${lv} ${src}${l.message ?? ''}`;
    })
    .join('\n');
  const blob = new Blob([header + body + '\n'], { type: 'text/plain;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `pmm-logs-${fmtTimestampForFile(now)}.log`;
  a.click();
  URL.revokeObjectURL(url);
}

onMounted(async () => {
  await loadHistory();
  await connect();
});
onUnmounted(async () => {
  try {
    await conn?.stop();
  } catch {
    /* */
  }
});
</script>

<template>
  <div class="page logs-page">
    <PmmPageHeader eyebrow="实时日志" title="服务日志流">
      <template #actions>
        <button
          class="btn btn-sm"
          :disabled="!visible.length"
          :title="visible.length ? `导出当前 ${visible.length} 条可见日志为 .log 文件` : '当前无可导出的日志'"
          @click="onExport"
        >
          <PmmIcon name="download" :size="14" />
          导出
        </button>
        <button
          class="btn btn-sm"
          :class="{ 'btn-primary': paused }"
          @click="paused = !paused"
        >
          <PmmIcon :name="paused ? 'play' : 'pause'" :size="14" />
          {{ paused ? '恢复' : '暂停' }}
        </button>
      </template>
    </PmmPageHeader>

    <!-- 工具栏 -->
    <div class="card toolbar">
      <div class="search-input">
        <PmmIcon name="search" :size="14" />
        <input v-model="keyword" placeholder="过滤消息 / 来源…" class="font-mono" />
      </div>
      <div class="divider-v" style="height: 22px" />
      <button
        v-for="(_, lv) in levels"
        :key="lv"
        class="btn btn-sm level-toggle"
        :class="[levels[lv] && `active tone-${lv.toLowerCase()}`]"
        @click="levels[lv] = !levels[lv]"
      >
        <span class="lv-dot" :class="`tone-${lv.toLowerCase()}`" />
        {{ shortLevel(lv) }}
        <span class="lv-count">{{ counts[lv] }}</span>
      </button>
      <span class="live-indicator" v-if="!paused" title="实时跟随">
        <span class="ping-dot" />实时
      </span>
    </div>

    <!-- 日志窗 -->
    <div class="card log-viewport">
      <div v-if="!visible.length" class="empty">
        <PmmIcon name="info" :size="28" style="color: var(--text-dim); margin-bottom: 8px" />
        <div>无匹配的日志条目</div>
      </div>
      <div v-for="(l, i) in visible" :key="i" class="log-line">
        <span class="ts">{{ l.timestamp ? fmtClock(l.timestamp) : '' }}</span>
        <span class="lvl" :class="levelClass(l.level)">{{ shortLevel(l.level) }}</span>
        <span class="content">
          <!-- source 可能缺失（如 EF Core 的 DbCommand 行）；缺失时不渲染空 [] 占位，避免视觉噪声 -->
          <span v-if="l.source" class="src">[{{ l.source }}]</span>
          <span class="msg">{{ l.message }}</span>
        </span>
      </div>
    </div>
  </div>
</template>

<style scoped lang="scss">
.logs-page {
  display: flex;
  flex-direction: column;
  height: calc(100vh - var(--topbar-h));
}
.toolbar {
  padding: 12px;
  display: flex;
  gap: 10px;
  align-items: center;
  margin-bottom: 14px;
  flex-wrap: wrap;
}
.search-input {
  display: flex;
  align-items: center;
  gap: 8px;
  background: var(--surface-2);
  border: 1px solid var(--border);
  border-radius: var(--r-2);
  padding: 6px 10px;
  flex: 1;
  min-width: 220px;
  color: var(--text-mute);
}
.search-input input {
  background: transparent;
  border: 0;
  outline: 0;
  flex: 1;
  font: inherit;
  font-size: 12px;
  color: var(--text);
}
.search-input input::placeholder { color: var(--text-dim); }
.level-toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
.level-toggle.active.tone-information { background: var(--info-soft); color: var(--info); border-color: transparent; }
.level-toggle.active.tone-warning { background: var(--warning-soft); color: var(--warning); border-color: transparent; }
.level-toggle.active.tone-error { background: var(--danger-soft); color: var(--danger); border-color: transparent; }
.level-toggle.active.tone-debug { background: var(--neutral-soft); color: var(--neutral); border-color: transparent; }
.lv-dot {
  width: 6px;
  height: 6px;
  border-radius: 999px;
  background: var(--text-dim);
}
.level-toggle.active .lv-dot.tone-information { background: var(--info); }
.level-toggle.active .lv-dot.tone-warning { background: var(--warning); }
.level-toggle.active .lv-dot.tone-error { background: var(--danger); }
.level-toggle.active .lv-dot.tone-debug { background: var(--neutral); }
.lv-count {
  font-size: 10.5px;
  color: var(--text-mute);
  margin-left: 4px;
}
.live-indicator {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 11.5px;
  color: var(--success);
  font-weight: 600;
}
.ping-dot {
  width: 8px;
  height: 8px;
  border-radius: 999px;
  background: var(--success);
  box-shadow: 0 0 0 4px var(--success-soft);
  animation: pmm-pulse 2s infinite;
}
.log-viewport {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 0;
  background: var(--bg-elev);
}
.log-line {
  display: grid;
  grid-template-columns: 80px 70px 1fr;
  gap: 12px;
  padding: 4px 16px;
  font-family: 'JetBrains Mono', monospace;
  font-size: 12.5px;
  border-bottom: 1px solid var(--border-soft);
  color: var(--text-mute);
}
.log-line:hover { background: var(--surface-2); }
.log-line .ts { color: var(--text-dim); }
.log-line .lvl { font-weight: 700; letter-spacing: 0.04em; }
.log-line .lvl.lvl-info { color: var(--info); }
.log-line .lvl.lvl-warn { color: var(--warning); }
.log-line .lvl.lvl-error { color: var(--danger); }
.log-line .lvl.lvl-debug { color: var(--text-dim); }
.log-line .src { color: var(--text-dim); margin-right: 6px; }
.log-line .msg { color: var(--text); }
</style>
