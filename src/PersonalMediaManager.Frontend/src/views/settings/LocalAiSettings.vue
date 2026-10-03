<script setup>
import { computed, onBeforeUnmount, onMounted } from 'vue';
import { localAiApi } from '@/api/localAi';
import PmmPageHeader from '@/components/PmmPageHeader.vue';
import AiBatchSettings from '@/components/AiBatchSettings.vue';
import {
  createLocalAiSettingsController, downloadPercent, localAiLimits, localAiModes, modelVerificationLabel,
} from '@/composables/useLocalAiSettings';

const controller = createLocalAiSettingsController(localAiApi);
const {
  form, saved, models, status, ready, loading, refreshing, action, error, notice, stale,
  busy, dirty, downloading, load, refresh, save, download, cancelDownload, start, stop,
} = controller;

const runtimeStates = {
  Stopped: { label: '已停止', type: 'info' },
  Starting: { label: '启动中', type: 'warning' },
  Running: { label: '健康运行', type: 'success' },
  Faulted: { label: '运行异常', type: 'danger' },
};
const downloadStates = {
  Idle: '尚未下载', Downloading: '下载并校验中', Completed: '下载完成，SHA256 校验通过',
  Cancelled: '下载已取消', Failed: '下载失败',
};
const runtimeState = computed(() => {
  if (stale.value) return { label: '状态待刷新', type: 'info' };
  if (action.value === 'start') return runtimeStates.Starting;
  return runtimeStates[status.value?.state] || { label: '状态未知', type: 'info' };
});
const savedMode = computed(() => localAiModes.find((mode) => mode.value === saved.value?.mode)?.label || '未加载');
const selectedModel = computed(() => models.value.find((model) => model.id === form.value.modelId));
const runningModel = computed(() => models.value.find((model) => model.id === status.value?.modelId)?.name || '无');
const runtimeActive = computed(() => ['Starting', 'Running'].includes(status.value?.state));
const canStart = computed(() => ready.value && !busy.value && !dirty.value && !downloading.value && !stale.value
  && status.value?.platformSupported && status.value?.runtimeConfigured && selectedModel.value?.canVerify
  && selectedModel.value?.installed && !runtimeActive.value);
const canStop = computed(() => ready.value && !busy.value && (runtimeActive.value || status.value?.state === 'Faulted' || stale.value));
const startHelp = computed(() => {
  if (!ready.value) return '请先加载设置。';
  if (dirty.value) return '有未保存的修改，请先保存；启动只使用已保存的设置。';
  if (stale.value) return '状态尚未确认，请先刷新状态。';
  if (!status.value?.platformSupported) return '当前 PMM 平台不受支持，无法启动本地运行时。';
  if (!status.value?.runtimeConfigured) return '请先在 PMM 所在电脑安装 llama-server，并保存其绝对路径。';
  if (!selectedModel.value?.canVerify) return '所选模型没有可核验的固定制品，无法启动。';
  if (!selectedModel.value?.installed) return selectedModel.value.canDownload
    ? '请先下载所选模型；下载完成后才能启动。'
    : '请把指定本地自转文件放入模型卡片的固定路径，再刷新状态；文件大小匹配后才能启动校验。';
  if (downloading.value) return '请等待下载与校验完成，或取消下载。';
  if (saved.value?.mode === 'Disabled') return '当前模式为关闭：可以启动服务检查健康状态，但本地模型不会参与解析。';
  return '服务运行后按已保存的介入模式提供建议；停止服务后解析流程自动回退。';
});

function formatSize(bytes) {
  if (bytes == null) return '未提供固定大小';
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

function sourceLink(value) {
  try {
    const url = new URL(value);
    return url.protocol === 'https:' && !url.username && !url.password ? url.href : null;
  } catch {
    return null;
  }
}

function canDownload(model) {
  return ready.value && model.canDownload && !busy.value && !downloading.value && !runtimeActive.value && !stale.value;
}

function downloadLabel(model) {
  if (status.value?.downloadModelId === model.id && ['Failed', 'Cancelled'].includes(status.value?.downloadState)) return '重试下载';
  return model.installed ? '重新下载' : '下载模型';
}

onMounted(load);
onBeforeUnmount(() => controller.dispose());
</script>

<template>
  <div class="local-ai-page">
    <PmmPageHeader eyebrow="解析设置" title="本地模型" subtitle="用本机小模型补充可追溯的检索建议，默认关闭。">
      <template #actions>
        <el-button :loading="loading || refreshing" :disabled="busy" @click="refresh(true)">刷新状态</el-button>
      </template>
    </PmmPageHeader>

    <AiBatchSettings />

    <el-alert type="info" show-icon :closable="false" class="page-alert">
      <template #title>本地模型只提供建议，规则与证据负责裁决</template>
      <p>建议必须能追溯到文件名或目录来源。模型不会覆盖规则确定的季集、年份和媒体类型，也不会自动绑定 TMDB。</p>
      <p>这里与既有 <router-link to="/settings/parse-ai-providers">AI 提供商设置</router-link> 独立。模型下载需要联网，推理只通过本机回环地址运行。</p>
    </el-alert>

    <el-alert v-if="error" type="error" show-icon :closable="false" class="page-alert" :title="error">
      <p>请检查运行路径、端口占用、磁盘空间或网络连接，再点击“刷新状态”；下载失败可在模型卡片重试。</p>
    </el-alert>
    <el-alert v-if="notice" type="success" show-icon closable class="page-alert" :title="notice" @close="notice = ''" />

    <el-skeleton v-if="loading && !ready" :rows="7" animated />
    <template v-else-if="ready">
      <el-form label-position="top" :model="form" :disabled="busy" @submit.prevent="save">
        <section class="settings-section">
          <div class="section-heading">
            <h2>介入模式</h2>
            <el-tag :type="saved?.mode === 'Disabled' ? 'info' : 'warning'">已保存：{{ savedMode }}</el-tag>
          </div>
          <el-radio-group v-model="form.mode" class="mode-options" aria-label="本地模型介入模式">
            <el-radio v-for="mode in localAiModes" :key="mode.value" :value="mode.value" border>
              <span class="mode-label">{{ mode.label }}</span>
              <span class="mode-description">{{ mode.description }}</span>
            </el-radio>
          </el-radio-group>
          <p class="hint">切换模式后需要保存。下载、启动、停止均不会改变模式；保存设置会停止旧进程，不会自动启动。</p>
        </section>

        <section class="settings-section">
          <div class="section-heading"><h2>模型文件</h2><span class="hint">官方固定下载或实验本地自转文件</span></div>
          <p class="hint">模型大小不代表识别准确率。建议先用现有测试集检查效果，再选择是否介入解析。</p>
          <div class="model-grid">
            <article v-for="model in models" :key="model.id" class="model-card" :class="{ selected: form.modelId === model.id }">
              <div class="model-heading">
                <h3>{{ model.name }}</h3>
                <el-tag v-if="model.conversionRevision" size="small" type="warning">实验自转</el-tag>
              </div>
              <div class="model-tags">
                <el-tag :type="model.installed ? 'info' : 'warning'" size="small">{{ model.installed ? '大小匹配，启动前校验' : '未找到匹配大小的文件' }}</el-tag>
                <el-tag v-if="!model.canDownload" type="warning" size="small">无官方 GGUF 下载</el-tag>
                <span class="hint">{{ formatSize(model.sizeBytes) }}</span>
              </div>
              <p class="hint">{{ modelVerificationLabel(model, status, stale) }}</p>
              <p v-if="model.unavailableReason" class="unavailable-reason">{{ model.unavailableReason }}</p>
              <dl class="model-details">
                <dt>固定版本</dt><dd>{{ model.revision || '未固定' }}</dd>
                <dt>文件名</dt><dd>{{ model.fileName }}</dd>
                <dt>PMM 所在电脑的固定路径</dt><dd>{{ model.localPath }}</dd>
                <dt>固定大小</dt><dd>{{ model.sizeBytes == null ? '未提供' : `${model.sizeBytes} 字节` }}</dd>
                <dt>SHA256</dt><dd>{{ model.sha256 || '暂无可验证哈希' }}</dd>
                <template v-if="model.conversionRevision"><dt>官方 llama.cpp 转换版本</dt><dd>{{ model.conversionRevision }}</dd></template>
              </dl>
              <el-link v-if="sourceLink(model.sourceUrl)" :href="sourceLink(model.sourceUrl)" target="_blank" rel="noopener noreferrer" type="primary">查看模型来源</el-link>
              <div class="model-actions">
                <el-button :type="form.modelId === model.id ? 'primary' : 'default'" :plain="form.modelId !== model.id" :disabled="busy || !model.canVerify" @click="form.modelId = model.id">
                  {{ form.modelId === model.id ? '当前选择' : '选择模型' }}
                </el-button>
                <el-button v-if="model.canDownload" :loading="action === 'download' && status?.downloadModelId === model.id" :disabled="!canDownload(model)" @click="download(model.id)">{{ downloadLabel(model) }}</el-button>
              </div>
              <div v-if="status?.downloadModelId === model.id && status.downloadState !== 'Idle'" class="download-status" aria-live="polite">
                <p>{{ downloadStates[status.downloadState] || status.downloadState }}</p>
                <el-progress
                  v-if="status.downloadState === 'Downloading'"
                  :percentage="downloadPercent(status)"
                  :indeterminate="!status.downloadTotalBytes"
                  :duration="2"
                />
                <p v-if="status.downloadState === 'Downloading'" class="hint">
                  {{ formatSize(status.downloadedBytes) }} / {{ formatSize(status.downloadTotalBytes) }}
                  <span v-if="downloadPercent(status) === 100"> · 正在校验，请稍候</span>
                </p>
                <p v-if="status.downloadError" class="download-error">{{ status.downloadError }}</p>
                <el-button v-if="downloading" type="warning" plain :loading="action === 'cancelDownload'" :disabled="busy" @click="cancelDownload">取消下载</el-button>
              </div>
            </article>
          </div>
          <el-empty v-if="!models.length" description="尚未取得模型列表，请刷新状态" :image-size="70" />
          <p v-if="runtimeActive" class="hint">服务运行期间请先停止服务，再下载或重新下载模型。</p>
        </section>

        <section class="settings-section">
          <div class="section-heading"><h2>运行时与资源</h2></div>
          <el-alert type="warning" show-icon :closable="false" class="runtime-help">
            <template #title>需要自行安装 llama.cpp 的 llama-server</template>
            <p>PMM 不附带运行时。请从官方渠道安装与 PMM 所在电脑系统及架构匹配的 llama-server；支持 Windows、Linux、macOS 的 x64 / arm64。</p>
            <el-link href="https://github.com/ggml-org/llama.cpp/tree/master/tools/server" target="_blank" rel="noopener noreferrer" type="primary">llama-server 官方说明</el-link>
          </el-alert>
          <el-form-item label="已安装的运行时绝对路径">
            <el-input v-model="form.runtimeExecutablePath" maxlength="1024" clearable autocomplete="off" placeholder="例如 /opt/llama.cpp/llama-server 或 C:\llama.cpp\llama-server.exe" />
            <p class="hint">填写 PMM 服务所在电脑的路径，不是浏览器所在电脑。仅填写可执行文件，不要加引号、命令参数、网络路径或凭证。</p>
          </el-form-item>
          <div class="parameter-grid">
            <el-form-item v-for="field in localAiLimits" :key="field.key" :label="field.label">
              <el-input-number v-model="form[field.key]" :min="field.min" :max="field.max" :step="field.step" :precision="0" controls-position="right" :aria-label="field.label" />
              <p class="hint">{{ field.hint }} 范围 {{ field.min }}～{{ field.max }}。</p>
            </el-form-item>
          </div>
          <p class="hint">监听地址固定为 127.0.0.1，不提供局域网暴露、凭证或额外命令参数配置。</p>
          <div class="save-actions">
            <el-button type="primary" :loading="action === 'save'" :disabled="busy || !dirty || downloading || stale" @click="save">保存设置</el-button>
            <span class="hint">{{ dirty ? '有未保存的修改' : '设置已保存' }}<span v-if="downloading"> · 下载期间请等待或取消后再保存</span></span>
          </div>
        </section>
      </el-form>

      <section class="settings-section">
        <div class="section-heading"><h2>服务状态</h2><el-tag :type="runtimeState.type">{{ runtimeState.label }}</el-tag></div>
        <div class="status-summary" aria-live="polite">
          <p>{{ status?.message || '等待服务状态' }}</p>
          <p class="hint">运行模型：{{ runningModel }} · 已保存的介入模式：{{ savedMode }}</p>
          <p class="hint">运行时：{{ status?.runtimeConfigured ? '已找到可执行文件' : '未配置或文件不存在' }} · 平台：{{ status?.platformSupported ? '支持' : '暂不支持' }}</p>
          <p v-if="stale" class="unavailable-reason">以上是上次获取的状态，不能据此判断当前服务健康情况。</p>
        </div>
        <div class="runtime-actions">
          <el-button type="primary" :loading="action === 'start'" :disabled="!canStart" @click="start">启动本地服务</el-button>
          <el-button type="danger" plain :loading="action === 'stop'" :disabled="!canStop" @click="stop">停止服务</el-button>
        </div>
        <p class="hint">{{ startHelp }}</p>
        <p v-if="action === 'start'" class="hint">正在校验模型并等待健康检查，最多需要约 {{ saved?.startupTimeoutSeconds }} 秒加载时间；完成后会刷新状态。</p>
      </section>
    </template>
    <el-empty v-else description="设置尚未加载，请点击上方“刷新状态”重试" :image-size="80" />
  </div>
</template>

<style scoped lang="scss">
.local-ai-page { max-width: 1180px; }
.page-alert { margin-bottom: 16px; }
.page-alert p, .runtime-help p { margin: 6px 0; }
.page-alert a { text-decoration: underline; }
.settings-section {
  margin-bottom: 20px;
  padding: 24px;
  border: 1px solid var(--border);
  border-radius: var(--r-3);
  background: var(--surface);
}
.section-heading, .model-heading, .model-tags, .model-actions, .save-actions, .runtime-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 10px;
}
.section-heading { justify-content: space-between; margin-bottom: 16px; }
h2 { margin: 0; font-size: 16px; font-weight: 600; }
h3 { margin: 0; font-size: 14px; font-weight: 600; }
.hint { margin: 8px 0 0; color: var(--text-mute); font-size: 12px; line-height: 1.7; }
.mode-options { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; width: 100%; }
.mode-options :deep(.el-radio) { width: 100%; height: auto; margin: 0; padding: 16px; align-items: flex-start; white-space: normal; }
.mode-options :deep(.el-radio__input) { margin-top: 3px; }
.mode-options :deep(.el-radio__label) { min-width: 0; }
.mode-label { display: block; margin-bottom: 6px; font-weight: 600; }
.mode-description { display: block; color: var(--text-mute); font-size: 12px; line-height: 1.7; }
.model-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; margin-top: 16px; }
.model-card { min-width: 0; padding: 18px; border: 1px solid var(--border); border-radius: var(--r-3); background: var(--surface-1); }
.model-card.selected { border-color: var(--accent); }
.model-tags { margin-top: 12px; }
.model-tags .hint { margin: 0; }
.model-details { font-size: 12px; }
.model-details dt { margin-top: 10px; color: var(--text-mute); }
.model-details dd { margin: 3px 0 0; overflow-wrap: anywhere; font-family: monospace; }
.model-actions { margin-top: 16px; }
.model-actions :deep(.el-button + .el-button), .runtime-actions :deep(.el-button + .el-button) { margin-left: 0; }
.download-status { margin-top: 16px; padding-top: 12px; border-top: 1px solid var(--border); font-size: 13px; }
.download-status p { margin: 6px 0; }
.download-status :deep(.el-button) { margin-top: 8px; }
.unavailable-reason { color: var(--warning); font-size: 12px; line-height: 1.7; }
.download-error { color: var(--danger); overflow-wrap: anywhere; }
.runtime-help { margin-bottom: 20px; }
.parameter-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 0 28px; }
.parameter-grid :deep(.el-form-item__content) { display: block; }
.parameter-grid :deep(.el-input-number) { width: 180px; max-width: 100%; }
.save-actions { padding-top: 20px; margin-top: 18px; border-top: 1px solid var(--border); }
.save-actions .hint { margin: 0; }
.status-summary { margin-bottom: 16px; }
.status-summary > p:first-child { margin-top: 0; overflow-wrap: anywhere; }
@media (max-width: 850px) {
  .mode-options { grid-template-columns: 1fr; }
  .model-grid { grid-template-columns: 1fr; }
}
@media (max-width: 580px) {
  .settings-section { padding: 18px; }
  .parameter-grid { grid-template-columns: 1fr; }
}
</style>
