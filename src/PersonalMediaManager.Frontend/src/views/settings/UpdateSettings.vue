<script setup>
import { computed, onMounted, ref } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

/**
 * 软件升级检查设置页（仿 Tmdb.vue 结构）
 *
 * 数据双源：
 *   1. GET /api/system/update-check → 当前配置 + 上次检查结果（lastCheck 快照）
 *   2. POST /api/system/update-check/run → 立即触发新一次检查 + 写库
 *
 * 字段约束：
 *   - GitHubOwner / GitHubRepo：明文存 System_Setting
 *   - GitHubPat：加密存（HasPat 暴露布尔），三态写入：null=不变 / ""=清空 / 非空=替换
 *   - IsPrivateRepo：仅 UI 层切换 PAT 字段必填校验，不持久化
 */

const form = ref({
  gitHubOwner: '',
  gitHubRepo: '',
  gitHubPat: '',
  isPrivateRepo: false,
  enabled: true,
  checkIntervalHours: 24,
});
const original = ref({});
const lastCheck = ref(null);
const skippedVersion = ref(null);

const loading = ref(false);
const saving = ref(false);
const testing = ref(false);
const running = ref(false);

// 编辑 PAT 开关：未配置时自动允许；已配置时默认展示「已配置（点击修改）」按钮
const editPat = ref(false);

const hasNewVersion = computed(() => !!lastCheck.value?.hasNewVersion);
const newVersionSkipped = computed(() =>
  hasNewVersion.value
  && skippedVersion.value
  && lastCheck.value?.latestVersion === skippedVersion.value);

async function load() {
  loading.value = true;
  try {
    const data = await api.systemUpdateCheck.get();
    skippedVersion.value = data?.skippedVersion || null;
    lastCheck.value = data?.lastCheck || null;
    form.value = {
      gitHubOwner: data?.gitHubOwner || '',
      gitHubRepo: data?.gitHubRepo || '',
      gitHubPat: '',
      isPrivateRepo: !!data?.hasPat,
      enabled: !!data?.enabled,
      checkIntervalHours: Number(data?.checkIntervalHours) || 24,
    };
    original.value = { ...form.value, hasPat: !!data?.hasPat };
    editPat.value = !data?.hasPat;
  } finally {
    loading.value = false;
  }
}

async function save() {
  if (!form.value.gitHubOwner?.trim()) {
    ElMessage.error('GitHub Owner 不能为空');
    return;
  }
  if (!form.value.gitHubRepo?.trim()) {
    ElMessage.error('GitHub Repo 不能为空');
    return;
  }
  const intervalNum = Number(form.value.checkIntervalHours);
  if (!Number.isFinite(intervalNum) || intervalNum < 1 || intervalNum > 720) {
    ElMessage.error('检查周期必须在 1~720 小时之间');
    return;
  }
  saving.value = true;
  try {
    const payload = {
      gitHubOwner: form.value.gitHubOwner.trim(),
      gitHubRepo: form.value.gitHubRepo.trim(),
      isPrivateRepo: !!form.value.isPrivateRepo,
      enabled: !!form.value.enabled,
      checkIntervalHours: Math.round(intervalNum),
    };
    // 三态：editPat=true 且非空 → 替换；editPat=true 且空 → 清空；editPat=false → 不传（保留旧值）
    if (editPat.value) {
      payload.gitHubPat = form.value.gitHubPat ?? '';
    }
    const data = await api.systemUpdateCheck.updateSettings(payload);
    ElMessage.success('已保存');
    lastCheck.value = data?.lastCheck || lastCheck.value;
    skippedVersion.value = data?.skippedVersion || null;
    if (data) {
      form.value.enabled = !!data.enabled;
      form.value.checkIntervalHours = Number(data.checkIntervalHours) || 24;
    }
    form.value.gitHubPat = '';
    editPat.value = !data?.hasPat;
    original.value = { ...form.value, hasPat: !!data?.hasPat };
  } finally {
    saving.value = false;
  }
}

async function test() {
  if (!form.value.gitHubOwner?.trim() || !form.value.gitHubRepo?.trim()) {
    ElMessage.error('请先填写 Owner 与 Repo');
    return;
  }
  testing.value = true;
  try {
    const payload = {
      gitHubOwner: form.value.gitHubOwner.trim(),
      gitHubRepo: form.value.gitHubRepo.trim(),
      gitHubPat: editPat.value ? (form.value.gitHubPat || null) : null,
    };
    const result = await api.systemUpdateCheck.test(payload);
    if (result?.success) {
      const elapsed = Number(result.elapsedMilliseconds) || 0;
      const latest = result.latestVersion ? ` · 最新 v${result.latestVersion}` : '';
      ElMessage.success(`GitHub 连接正常 · ${elapsed.toFixed(0)}ms${latest}`);
    } else {
      const status = result?.httpStatus != null ? `HTTP ${result.httpStatus} · ` : '';
      const reason = result?.errorMessage || '未知错误';
      ElMessage.error(`GitHub 连接失败：${status}${reason}`);
    }
  } finally {
    testing.value = false;
  }
}

async function run() {
  running.value = true;
  try {
    const result = await api.systemUpdateCheck.run();
    if (result?.success) {
      if (result.hasNewVersion) {
        ElMessage.success(`已检查：发现新版本 v${result.latestVersion}`);
      } else {
        ElMessage.success(`已检查：当前 v${result.currentVersion} 已是最新`);
      }
    } else {
      const status = result?.httpStatus != null ? `HTTP ${result.httpStatus} · ` : '';
      const reason = result?.errorMessage || '未知错误';
      ElMessage.warning(`检查失败：${status}${reason}`);
    }
    // 刷快照
    const data = await api.systemUpdateCheck.get();
    lastCheck.value = data?.lastCheck || null;
    skippedVersion.value = data?.skippedVersion || null;
  } finally {
    running.value = false;
  }
}

async function skipCurrent() {
  const v = lastCheck.value?.latestVersion;
  if (!v) return;
  try {
    await ElMessageBox.confirm(
      `跳过版本 v${v} 后，本机将不再气泡 / Tag 提示此版本（仍可手动「立即检查」）。`,
      '跳过此版本',
      { confirmButtonText: '跳过', cancelButtonText: '取消', type: 'warning' },
    );
  } catch {
    return; // 取消
  }
  await api.systemUpdateCheck.skip(v);
  ElMessage.success(`已跳过 v${v}`);
  skippedVersion.value = v;
}

function formatDate(iso) {
  if (!iso) return '';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="设置"
      title="软件更新"
      subtitle="从 GitHub Releases 检查新版本；本期仅提示，不自动下载 / 安装。"
    >
      <template #actions>
        <button class="btn btn-primary btn-sm" @click="save" :disabled="saving">
          <PmmIcon name="check" :size="14" /> 保存配置
        </button>
      </template>
    </PmmPageHeader>

    <!-- ── 仓库与凭据 ── -->
    <section class="section-card">
      <header class="section-head"><h3>GitHub 仓库</h3></header>

      <div class="form-row">
        <div>
          <div class="label">Owner</div>
          <div class="hint">仓库所有者，例如 <code>mdjs147</code>。</div>
        </div>
        <div class="control">
          <input class="input font-mono" v-model="form.gitHubOwner" placeholder="mdjs147" style="width: 320px" autocomplete="off" />
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">Repo</div>
          <div class="hint">仓库名，例如 <code>PersonalMediaManager</code>。</div>
        </div>
        <div class="control">
          <input class="input font-mono" v-model="form.gitHubRepo" placeholder="PersonalMediaManager" style="width: 320px" autocomplete="off" />
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">仓库可见性</div>
        </div>
        <div class="control">
          <div class="hstack" style="gap: 16px">
            <label class="hstack" style="gap: 6px; cursor: pointer">
              <input type="radio" :value="false" v-model="form.isPrivateRepo" /> 公开仓库（PAT 可选，速率 60 req/h）
            </label>
            <label class="hstack" style="gap: 6px; cursor: pointer">
              <input type="radio" :value="true" v-model="form.isPrivateRepo" /> 私有仓库（必须配置 PAT）
            </label>
          </div>
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">PAT</div>
          <div class="hint">推荐 Fine-grained PAT，Permissions: <code>Contents: Read-only</code>；加密存入 SQLite。</div>
        </div>
        <div class="control">
          <template v-if="editPat">
            <input
              class="input font-mono"
              type="password"
              v-model="form.gitHubPat"
              placeholder="github_pat_…"
              style="flex: 1; max-width: 460px"
              autocomplete="new-password"
            />
            <button v-if="original.hasPat" class="btn btn-ghost" @click="editPat = false; form.gitHubPat = ''">
              取消修改
            </button>
          </template>
          <template v-else>
            <button class="btn" @click="editPat = true; form.gitHubPat = ''" style="flex: 1; max-width: 460px; justify-content: flex-start">
              <PmmIcon name="edit" :size="14" /> 已配置（点击修改）
            </button>
          </template>
          <button class="btn" @click="test" :disabled="testing">
            <PmmIcon name="link" :size="14" /> 测试连接
          </button>
        </div>
      </div>
    </section>

    <!-- ── 自动检查 ── -->
    <section class="section-card">
      <header class="section-head"><h3>自动检查</h3></header>
      <div class="form-row">
        <div>
          <div class="label">已启用</div>
          <div class="hint">关闭后 Worker 跳过周期检查；可随时切回开启，下一轮 5 分钟内生效。</div>
        </div>
        <div class="control">
          <el-switch v-model="form.enabled" active-text="开" inactive-text="关" inline-prompt />
        </div>
      </div>
      <div class="form-row">
        <div>
          <div class="label">检查周期</div>
          <div class="hint">单位：小时；范围 1~720。改后下一轮 Worker tick 生效。</div>
        </div>
        <div class="control">
          <el-input-number
            v-model="form.checkIntervalHours"
            :min="1"
            :max="720"
            :step="1"
            :controls-position="'right'"
            style="width: 200px"
          />
        </div>
      </div>
      <div class="form-row">
        <div>
          <div class="label">手动触发</div>
          <div class="hint">立即调一次 GitHub API；结果写入数据库。</div>
        </div>
        <div class="control">
          <button class="btn" @click="run" :disabled="running">
            <PmmIcon name="link" :size="14" /> 立即检查
          </button>
        </div>
      </div>
    </section>

    <!-- ── 上次检查结果 ── -->
    <section class="section-card">
      <header class="section-head"><h3>上次检查结果</h3></header>
      <template v-if="lastCheck">
        <div class="form-row">
          <div>
            <div class="label">检查时间</div>
          </div>
          <div class="control"><span class="font-mono">{{ formatDate(lastCheck.checkedAt) }}</span></div>
        </div>

        <div class="form-row">
          <div>
            <div class="label">状态</div>
          </div>
          <div class="control">
            <template v-if="lastCheck.success">
              <span v-if="hasNewVersion" class="tag tag-warning"><span class="tag-dot" />发现新版本 v{{ lastCheck.latestVersion }}</span>
              <span v-else class="tag tag-success"><span class="tag-dot" />当前已是最新（v{{ lastCheck.currentVersion }}）</span>
            </template>
            <span v-else class="tag tag-danger">
              <span class="tag-dot" />
              失败 · {{ lastCheck.errorCategory || '' }} {{ lastCheck.httpStatus ? `HTTP ${lastCheck.httpStatus}` : '' }}
            </span>
          </div>
        </div>

        <div v-if="lastCheck.errorMessage" class="form-row">
          <div>
            <div class="label">错误信息</div>
          </div>
          <div class="control"><span class="error-text">{{ lastCheck.errorMessage }}</span></div>
        </div>

        <div v-if="lastCheck.latestVersion" class="form-row">
          <div>
            <div class="label">最新版本</div>
          </div>
          <div class="control">
            <span class="font-mono">v{{ lastCheck.latestVersion }}</span>
            <a v-if="lastCheck.latestUrl" :href="lastCheck.latestUrl" target="_blank" rel="noopener noreferrer" class="btn btn-ghost">
              <PmmIcon name="link" :size="14" /> 在 GitHub 查看
            </a>
            <button v-if="hasNewVersion && !newVersionSkipped" class="btn" @click="skipCurrent">
              <PmmIcon name="check" :size="14" /> 跳过此版本
            </button>
            <span v-if="newVersionSkipped" class="tag tag-default"><span class="tag-dot" />已跳过</span>
          </div>
        </div>

        <div v-if="lastCheck.publishedAt" class="form-row">
          <div>
            <div class="label">发布时间</div>
          </div>
          <div class="control"><span class="font-mono">{{ formatDate(lastCheck.publishedAt) }}</span></div>
        </div>
      </template>
      <template v-else>
        <div class="form-row">
          <div>
            <div class="label">尚未检查</div>
            <div class="hint">点击「立即检查」触发首次拉取。</div>
          </div>
          <div class="control" />
        </div>
      </template>
    </section>
  </div>
</template>

<style scoped lang="scss">
.toggle-row {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  cursor: pointer;
}
.error-text {
  color: var(--danger);
  font-size: 13px;
}
code {
  background: var(--surface-2);
  padding: 1px 6px;
  border-radius: 4px;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 12px;
}
</style>
