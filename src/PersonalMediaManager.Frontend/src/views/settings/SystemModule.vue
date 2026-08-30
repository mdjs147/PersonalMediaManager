<script setup>
import { computed, onMounted, ref } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

const info = ref(null);
const loading = ref(false);
const importing = ref(false);
const fileInput = ref(null);

async function load() {
  loading.value = true;
  try {
    info.value = await api.system.info();
  } finally {
    loading.value = false;
  }
}

function fmt(bytes) {
  if (!bytes) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  let v = bytes;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(1)} ${units[i]}`;
}

async function onExport() {
  try {
    const blob = await api.system.export();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `pmm-export-${Date.now()}.zip`;
    a.click();
    URL.revokeObjectURL(url);
    ElMessage.success('导出完成');
  } catch {
    // http.js 拦截器已弹 ElMessage.error；这里 catch 仅拦截 reject 避免控制台报错
  }
}

const backingUp = ref(false);
const backups = ref([]);
const loadingBackups = ref(false);
const restoring = ref(false);

/** 立即备份：VACUUM INTO 在线快照 + 密钥环打包到 backups 目录，按保留份数清理旧备份 */
async function onBackup() {
  backingUp.value = true;
  try {
    const r = await api.system.backup();
    ElMessage.success(`备份完成：${r.fileName}${r.prunedCount ? `（已清理 ${r.prunedCount} 份旧备份）` : ''}`);
    await Promise.all([load(), loadBackups()]);
  } catch {
    // http.js 拦截器已弹 ElMessage.error；这里 catch 仅拦截 reject 避免控制台报错
  } finally {
    backingUp.value = false;
  }
}

/** 加载 backups/ 内备份列表（时间倒序），供「从备份恢复」选择 */
async function loadBackups() {
  loadingBackups.value = true;
  try {
    backups.value = await api.system.backups();
  } catch {
    // http.js 拦截器已弹错；catch 仅拦 reject
  } finally {
    loadingBackups.value = false;
  }
}

/** 从指定备份恢复：暂存待重启换库（复用导入流程的「退出重启」语义） */
async function onRestore(file) {
  try {
    await ElMessageBox.confirm(
      `将用备份「${file.fileName}」恢复数据库：恢复包会先暂存，待你完整退出并重启应用后再替换当前库（旧库自动备份为 pmm.db.preimport-*，密钥环立即合并）。是否继续？`,
      '从备份恢复',
      { type: 'warning', confirmButtonText: '恢复并待重启', cancelButtonText: '取消' });
  } catch {
    return;
  }
  restoring.value = true;
  try {
    const data = await api.system.restore(file.fileName);
    ElMessage.success('恢复包已暂存，重启后生效');
    ElMessageBox.alert(
      data?.message || '请从托盘完整退出后重启 PersonalMediaManager 以完成换库（旧库会自动备份为 pmm.db.preimport-*）',
      '需要重启才能生效',
      { type: 'warning' });
  } catch {
    // http.js 拦截器已弹错；catch 仅拦 reject
  } finally {
    restoring.value = false;
  }
}

function fmtTime(t) {
  if (!t) return '从未备份';
  try { return new Date(t).toLocaleString(); } catch { return String(t); }
}

/** 久未备份：从未备份，或最近一次成功备份超过 36 小时（日备 + 容差）→ 飘红提醒 */
const backupStale = computed(() => {
  const t = info.value?.lastSuccessfulBackupAt;
  if (!t) return true;
  return Date.now() - new Date(t).getTime() > 36 * 3600 * 1000;
});

const clearingHistory = ref(false);
const resettingConfig = ref(false);

async function onClearHistory() {
  try {
    await ElMessageBox.confirm(
      '将永久删除所有「处理历史」（媒体记录 / 时间线步骤 / AI 调用 / 操作审计 / 调度任务记录 / Webhook 投递日志）。\n配置、订阅、用户与 TMDB 缓存不会被动。\n此操作不可撤销，请先 Export 备份！\n是否继续？',
      '危险操作：清空处理历史',
      { type: 'error', confirmButtonText: '我已备份，确认清空', cancelButtonText: '取消' });
  } catch {
    return;
  }
  clearingHistory.value = true;
  try {
    const data = await api.system.clearHistory();
    const total = (data?.mediaItems || 0) + (data?.processSteps || 0) + (data?.aiCalls || 0)
      + (data?.operations || 0) + (data?.scheduledTaskRuns || 0) + (data?.webhookDeliveries || 0);
    ElMessage.success(`已清空处理历史，共删除 ${total} 行`);
  } finally {
    clearingHistory.value = false;
  }
}

async function onResetConfig() {
  try {
    await ElMessageBox.confirm(
      '将永久删除所有「配置」（监控目录 / 忽略规则 / 解析规则 / AI 提供商 / TMDB 设置 / 分类 / 分类匹配规则 / Webhook 订阅 / 系统设置）。\n媒体处理历史与用户账号不会被动。\n操作完成后会自动重新种子默认分类与解析规则。\n此操作不可撤销，请先 Export 备份！\n是否继续？',
      '危险操作：重置所有配置',
      { type: 'error', confirmButtonText: '我已备份，确认重置', cancelButtonText: '取消' });
  } catch {
    return;
  }
  resettingConfig.value = true;
  try {
    const data = await api.system.resetConfig();
    const total = (data?.systemSettings || 0) + (data?.watchFolders || 0) + (data?.watchIgnoreRules || 0)
      + (data?.parseRules || 0) + (data?.parseAiProviders || 0) + (data?.tmdbSettings || 0)
      + (data?.categories || 0) + (data?.categoryMatchRules || 0) + (data?.webhookSubscriptions || 0);
    ElMessage.success(`已重置所有配置，共删除 ${total} 行${data?.reseededDefaults ? '，并已重新种子默认值' : ''}`);
    // 配置变更后建议立刻刷新当前页（其他设置子页用户手动刷新即可）
    await load();
  } finally {
    resettingConfig.value = false;
  }
}

async function onPickImport(ev) {
  const file = ev.target?.files?.[0];
  if (!file) return;
  try {
    await ElMessageBox.confirm('导入会先暂存上传的库，待你完整退出并重启应用后再替换当前数据库（密钥环立即合并）。建议先导出备份。是否继续？', '警告', {
      type: 'warning',
    });
  } catch {
    // 用户取消（ElMessageBox 取消会 reject）
    if (ev.target) ev.target.value = '';
    return;
  }
  importing.value = true;
  try {
    // http.js 拦截器自动解包 body.data → 这里 data = ImportResult { success, stagedPath, requiresRestart, message }
    const data = await api.system.import(file);
    ElMessage.success('导入包已暂存，重启后生效');
    // 必须完整退出重启进程才会换库：仅停/起 Kestrel（进程未退）可能因连接占用换库失败，文案明确强调「退出托盘后重启」
    ElMessageBox.alert(
      data?.message || '请从托盘完整退出后重启 PersonalMediaManager 以完成换库（旧库会自动备份为 pmm.db.preimport-*）',
      '需要重启才能生效',
      { type: 'warning' },
    );
  } catch {
    // http.js 拦截器对 code !== 0 已 ElMessage.error；这里 catch 仅拦截 reject 避免控制台报错
  } finally {
    importing.value = false;
    if (ev.target) ev.target.value = '';
  }
}

onMounted(() => { load(); loadBackups(); });
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader eyebrow="设置" title="系统" subtitle="版本与平台信息、配置导入导出、危险操作。" />

    <div class="sys-grid">
      <!-- 版本信息 -->
      <section class="section-card">
        <header class="section-head"><h3>版本信息</h3></header>
        <div class="kv-list" v-if="info">
          <div class="kv"><span class="k">版本</span><span class="v font-mono tabular">{{ info.version }}</span></div>
          <div class="kv"><span class="k">操作系统</span><span class="v font-mono">{{ info.os }} ({{ info.osVersion }})</span></div>
          <div class="kv"><span class="k">架构</span><span class="v font-mono">{{ info.architecture }}</span></div>
          <div class="kv"><span class="k">运行时</span><span class="v font-mono">{{ info.runtimeVersion }}</span></div>
          <div class="kv"><span class="k">启动时间</span><span class="v font-mono">{{ info.startedAt }}</span></div>
          <div class="kv"><span class="k">数据目录</span><span class="v font-mono">{{ info.dataRoot }}</span></div>
          <div class="kv"><span class="k">数据库大小</span><span class="v tabular">{{ fmt(info.dbSizeBytes) }}</span></div>
          <div class="kv"><span class="k">日志目录</span><span class="v tabular">{{ fmt(info.logDirSizeBytes) }}</span></div>
          <div class="kv"><span class="k">海报缓存</span><span class="v tabular">{{ fmt(info.postersCacheSizeBytes) }}</span></div>
          <div class="kv"><span class="k">最近备份</span><span class="v tabular" :class="{ 'backup-stale': backupStale }">{{ fmtTime(info.lastSuccessfulBackupAt) }}</span></div>
        </div>
      </section>

      <!-- 导入 / 导出 -->
      <section class="section-card">
        <header class="section-head"><h3>配置导入 / 导出</h3></header>
        <div class="form-row">
          <div>
            <div class="label">导出当前配置</div>
            <div class="hint">包含 pmm.db、appsettings.json、DataProtection 密钥环。可用于换机迁移或本地备份。</div>
          </div>
          <div class="control">
            <button class="btn btn-primary" @click="onExport">
              <PmmIcon name="download" :size="14" /> 导出 .zip
            </button>
          </div>
        </div>
        <div class="form-row">
          <div>
            <div class="label">立即备份数据库</div>
            <div class="hint">在线快照（VACUUM INTO）数据库 + 密钥环，落到数据目录下 backups\，并按保留份数清理旧备份。自动备份默认每日 04:00 执行（可在「设置 → 常规 → 备份」调整开关与保留份数）。</div>
          </div>
          <div class="control">
            <button class="btn" :disabled="backingUp" @click="onBackup">
              <PmmIcon name="download" :size="14" /> {{ backingUp ? '备份中…' : '立即备份' }}
            </button>
          </div>
        </div>
        <div class="form-row">
          <div>
            <div class="label">导入配置</div>
            <div class="hint">导入前会自动备份当前 pmm.db，无需担心覆盖。</div>
          </div>
          <div class="control">
            <input ref="fileInput" type="file" accept=".zip" style="display: none" @change="onPickImport" />
            <button class="btn" :disabled="importing" @click="fileInput?.click()">
              <PmmIcon name="upload" :size="14" /> 选择 .zip 文件
            </button>
          </div>
        </div>
      </section>

      <!-- 备份与恢复 -->
      <section class="section-card">
        <header class="section-head">
          <h3>备份与恢复</h3>
          <div class="hint">从下方任一备份恢复数据库（含密钥环）。恢复为「暂存 + 重启换库」，不会即时覆盖当前库。自动备份默认每日 04:00。</div>
        </header>
        <div class="backup-toolbar">
          <button class="btn" :disabled="loadingBackups" @click="loadBackups">
            {{ loadingBackups ? '加载中…' : '刷新列表' }}
          </button>
        </div>
        <div v-if="!backups.length" class="backup-empty">暂无备份。可点上方「立即备份」，或等每日自动备份。</div>
        <ul v-else class="backup-list">
          <li v-for="b in backups" :key="b.fileName" class="backup-item">
            <div class="backup-meta">
              <span class="backup-name font-mono">{{ b.fileName }}</span>
              <span class="backup-sub">{{ fmtTime(b.createdAt) }} · {{ fmt(b.sizeBytes) }}</span>
            </div>
            <button class="btn btn-sm" :disabled="restoring" @click="onRestore(b)">恢复</button>
          </li>
        </ul>
      </section>

      <!-- 危险操作区 -->
      <section class="section-card danger-card">
        <header class="section-head">
          <h3 class="danger-title">危险操作</h3>
          <div class="hint">以下操作不可撤销。强烈建议先「导出配置」做一次完整备份后再继续。</div>
        </header>
        <div class="form-row">
          <div>
            <div class="label">清空处理历史</div>
            <div class="hint">删除所有媒体记录 / 时间线步骤 / AI 调用 / 操作审计 / 调度任务记录 / Webhook 投递日志。不动配置 / 订阅 / 用户 / TMDB 缓存。</div>
          </div>
          <div class="control">
            <button class="btn btn-danger" :disabled="clearingHistory" @click="onClearHistory">
              <PmmIcon name="trash" :size="14" /> {{ clearingHistory ? '清空中…' : '清空处理历史' }}
            </button>
          </div>
        </div>
        <div class="form-row">
          <div>
            <div class="label">重置所有配置</div>
            <div class="hint">删除所有监控目录 / 忽略规则 / 解析规则 / AI 提供商 / TMDB 设置 / 分类 / 分类匹配规则 / Webhook 订阅 / 系统设置，并重新种子默认分类与解析规则。不动历史与用户账号。</div>
          </div>
          <div class="control">
            <button class="btn btn-danger" :disabled="resettingConfig" @click="onResetConfig">
              <PmmIcon name="warning" :size="14" /> {{ resettingConfig ? '重置中…' : '重置所有配置' }}
            </button>
          </div>
        </div>
      </section>

    </div>
  </div>
</template>

<style scoped lang="scss">
.sys-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
}
.kv-list {
  padding: 18px 22px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.kv {
  display: flex;
  gap: 18px;
  align-items: baseline;
}
.kv .k {
  color: var(--text-mute);
  font-size: 12px;
  width: 100px;
  flex-shrink: 0;
}
.kv .v {
  color: var(--text);
  font-size: 13px;
}
.danger-card {
  padding: 22px;
  border-color: color-mix(in oklab, var(--danger) 40%, var(--border));
  background: color-mix(in oklab, var(--danger-soft) 60%, transparent);
}
.danger-title {
  font-weight: 700;
  color: var(--danger);
  margin-bottom: 4px;
}
.backup-stale {
  color: var(--danger);
  font-weight: 600;
}
.backup-toolbar {
  display: flex;
  gap: 8px;
  padding: 14px 22px 0;
}
.backup-empty {
  padding: 16px 22px;
  color: var(--text-mute);
  font-size: 13px;
}
.backup-list {
  list-style: none;
  margin: 0;
  padding: 12px 22px 18px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.backup-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: 8px;
}
.backup-meta {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.backup-name {
  font-size: 13px;
  color: var(--text);
  overflow: hidden;
  text-overflow: ellipsis;
}
.backup-sub {
  font-size: 12px;
  color: var(--text-mute);
}
.btn-sm {
  padding: 4px 12px;
  font-size: 12px;
}
</style>
