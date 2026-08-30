<script setup>
import { computed, ref, watch } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';
import { api } from '@/api';
import { useAuthStore } from '@/stores/auth';

/**
 * 关于对话框：展示 4 套版本号 + commit + buildTime + 数据库 target vs applied 对比 + 升级提示
 * 数据三源：
 *   1. 构建期常量 __APP_*__（vite define 注入）— 离线 fallback
 *   2. 运行时 GET /api/system/version（匿名）— 真实生效版本号
 *   3. 运行时 GET /api/system/update-check（Admin）— 升级状态（仅 Admin 登录后可用）
 */

const props = defineProps({
  modelValue: { type: Boolean, default: false },
});
const emit = defineEmits(['update:modelValue']);

const visible = ref(props.modelValue);
const auth = useAuthStore();
watch(() => props.modelValue, (v) => { visible.value = v; });
watch(visible, (v) => {
  emit('update:modelValue', v);
  if (v) {
    loadRuntime();
    if (auth.isLoggedIn) loadUpdateInfo();
  }
});

// 构建期常量（离线 fallback）
const buildtime = {
  product: __APP_PRODUCT_VERSION__,
  backend: __APP_BACKEND_VERSION__,
  frontend: __APP_FRONTEND_VERSION__,
  dbTarget: __APP_DB_VERSION__,
  commit: __APP_COMMIT__,
  buildTime: __APP_BUILD_TIME__,
};

const runtime = ref(null);
const loadError = ref(null);
const updateInfo = ref(null);   // { hasPat, lastCheck, skippedVersion, ... }
const updateLoading = ref(false);

async function loadRuntime() {
  loadError.value = null;
  try {
    runtime.value = await api.system.version();
  } catch (e) {
    loadError.value = e?.message || '加载失败';
  }
}

async function loadUpdateInfo() {
  updateLoading.value = true;
  try {
    updateInfo.value = await api.systemUpdateCheck.get();
  } catch {
    // 失败静默：未登录 / 401 等情况下不阻断关于对话框
    updateInfo.value = null;
  } finally {
    updateLoading.value = false;
  }
}

const newVersion = computed(() => {
  const lc = updateInfo.value?.lastCheck;
  if (!lc || !lc.success || !lc.hasNewVersion) return null;
  if (updateInfo.value?.skippedVersion === lc.latestVersion) return null;
  return lc;
});

async function runCheck() {
  updateLoading.value = true;
  try {
    await api.systemUpdateCheck.run();
    updateInfo.value = await api.systemUpdateCheck.get();
    const lc = updateInfo.value?.lastCheck;
    if (lc?.success) {
      ElMessage.success(lc.hasNewVersion ? `发现新版本 v${lc.latestVersion}` : '已是最新版本');
    } else if (lc) {
      ElMessage.warning(`检查失败：${lc.errorMessage || lc.errorCategory || '未知错误'}`);
    }
  } finally {
    updateLoading.value = false;
  }
}

async function skipNewVersion() {
  const lc = newVersion.value;
  if (!lc) return;
  try {
    await ElMessageBox.confirm(
      `跳过 v${lc.latestVersion} 后不再提示此版本（仍可在「设置 → 软件更新」立即检查）。`,
      '跳过此版本',
      { confirmButtonText: '跳过', cancelButtonText: '取消', type: 'warning' },
    );
  } catch {
    return;
  }
  await api.systemUpdateCheck.skip(lc.latestVersion);
  ElMessage.success(`已跳过 v${lc.latestVersion}`);
  updateInfo.value = await api.systemUpdateCheck.get();
}

function close() { visible.value = false; }
</script>

<template>
  <el-dialog
    v-model="visible"
    title="关于 PersonalMediaManager"
    width="520"
    :destroy-on-close="false"
    align-center
  >
    <div class="about">
      <div class="hero">
        <div class="title">PersonalMediaManager</div>
        <div class="product-row">
          <span class="product">v{{ runtime?.product || buildtime.product }}</span>
          <el-tag v-if="newVersion" type="warning" size="small" effect="dark" class="new-tag">
            v{{ newVersion.latestVersion }} 可用
          </el-tag>
        </div>
      </div>

      <!-- 升级提示卡片（仅在检测到新版本且未跳过时显示） -->
      <el-alert
        v-if="newVersion"
        type="warning"
        :closable="false"
        show-icon
        class="update-alert"
      >
        <template #title>
          <span>发现新版本 v{{ newVersion.latestVersion }}</span>
        </template>
        <template #default>
          <div class="update-body">
            <span v-if="newVersion.latestName">{{ newVersion.latestName }}</span>
            <div class="update-actions">
              <a
                v-if="newVersion.latestUrl"
                :href="newVersion.latestUrl"
                target="_blank"
                rel="noopener noreferrer"
                class="el-button el-button--primary el-button--small"
              >前往 GitHub 下载</a>
              <el-button size="small" @click="skipNewVersion">跳过此版本</el-button>
            </div>
          </div>
        </template>
      </el-alert>

      <div class="kvs">
        <div class="row">
          <span class="k">后端版本</span>
          <span class="v font-mono">{{ runtime?.backend || buildtime.backend }}</span>
        </div>
        <div class="row">
          <span class="k">前端版本</span>
          <span class="v font-mono">{{ runtime?.frontend || buildtime.frontend }}</span>
        </div>
        <div class="row">
          <span class="k">数据库目标</span>
          <span class="v font-mono">{{ runtime?.database?.target || buildtime.dbTarget }}</span>
        </div>
        <div class="row">
          <span class="k">数据库实际</span>
          <span class="v font-mono">
            {{ runtime?.database?.applied || '加载中…' }}
            <el-tag v-if="runtime?.database?.needsMigration" type="warning" size="small" effect="dark">需要迁移</el-tag>
          </span>
        </div>
        <div class="row">
          <span class="k">Commit</span>
          <span class="v font-mono">
            {{ runtime?.commit || buildtime.commit }}
            <el-tag v-if="runtime?.dirty" type="danger" size="small" effect="dark">dirty</el-tag>
          </span>
        </div>
        <div class="row">
          <span class="k">构建时间</span>
          <span class="v font-mono">{{ runtime?.buildTime || buildtime.buildTime }}</span>
        </div>
        <div v-if="runtime?.framework" class="row">
          <span class="k">运行时</span>
          <span class="v font-mono">{{ runtime.framework }}</span>
        </div>
      </div>

      <el-alert
        v-if="loadError"
        type="warning"
        :closable="false"
        show-icon
        :title="`运行时版本号加载失败：${loadError}（显示构建期常量作为兜底）`"
      />
    </div>
    <template #footer>
      <el-button v-if="auth.isLoggedIn" :loading="updateLoading" @click="runCheck">立即检查更新</el-button>
      <el-button @click="close">关闭</el-button>
    </template>
  </el-dialog>
</template>

<style scoped lang="scss">
.about {
  display: flex;
  flex-direction: column;
  gap: 18px;
}
.hero {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 12px;
  padding-bottom: 12px;
  border-bottom: 1px solid var(--border-soft);
}
.hero .title {
  font-size: 18px;
  font-weight: 700;
  letter-spacing: 0.02em;
}
.hero .product-row {
  display: flex;
  align-items: center;
  gap: 8px;
}
.hero .product {
  font-size: 16px;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  color: var(--accent);
}
.new-tag {
  font-weight: 600;
}
.update-alert {
  margin-top: 4px;
}
.update-body {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 8px;
}
.update-actions {
  display: flex;
  gap: 6px;
}
.kvs {
  display: grid;
  grid-template-columns: 110px 1fr;
  row-gap: 8px;
  column-gap: 12px;
  font-size: 13px;
}
.kvs .row {
  display: contents;
}
.kvs .k {
  color: var(--text-2);
}
.kvs .v {
  color: var(--text-1);
  display: flex;
  align-items: center;
  gap: 8px;
  word-break: break-all;
}
.font-mono {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
}
</style>
