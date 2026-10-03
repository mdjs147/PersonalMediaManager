<script setup>
import { onMounted, reactive, ref, toRefs } from 'vue';
import { aiDiagnosticsApi } from '@/api/aiDiagnostics';
import { createAiDiagnosticSettings } from '@/composables/aiDiagnosticSettings';
const { state, load, save } = createAiDiagnosticSettings(aiDiagnosticsApi, reactive);
const { settings, level, busy, error, notice, uncertain } = toRefs(state);
const mediaItemId = ref(null);
async function download() {
  if (busy.value || !Number.isSafeInteger(mediaItemId.value) || mediaItemId.value <= 0) return;
  busy.value = true; error.value = ''; notice.value = '';
  try {
    const data = await aiDiagnosticsApi.export(mediaItemId.value);
    const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json;charset=utf-8' }));
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = `pmm-diagnostics-${mediaItemId.value}.json`;
    document.body.appendChild(anchor); anchor.click(); anchor.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    notice.value = '已生成诊断导出，请查看 completeness 与各正文状态；保留期外的内容无法恢复';
  } catch { error.value = '诊断导出失败，请重试'; }
  finally { busy.value = false; }
}
onMounted(load);
</script>

<template>
  <section class="diagnostics card" aria-label="AI 完整诊断设置">
    <h2>AI 诊断日志</h2>
    <p>默认 Standard 只保存摘要。Full 在下列容量和隐私边界内，分别保存输入与上下文、实际提示词、HTTP 原格式响应、抽取内容、清理步骤和最终结构化结果；每段是否完整以导出状态为准。</p>
    <p>始终脱敏密钥与认证信息，并明确省略私有推理。Full 会记录媒体名称等内容，仅管理员可导出；请在排查结束后恢复 Standard。</p>
    <p v-if="settings">正文每段上限 {{ Math.round(settings.maxArtifactUtf8Bytes / 1024) }} KiB，共 {{ Math.round(settings.maxArtifactTotalBytes / 1048576) }} MiB / {{ settings.maxArtifacts }} 段，保留 {{ settings.retentionDays }} 天。超限不保存整段，导出明确标记缺失原因；已有正文不会因为切换级别立即删除。</p>
    <p v-if="error" role="alert">{{ error }}</p><p v-if="notice" role="status">{{ notice }}</p>
    <div class="controls">
      <el-select v-model="level" aria-label="诊断级别" :disabled="busy || !settings || uncertain" style="width: 220px">
        <el-option label="Off · 关闭" value="Off" /><el-option label="Standard · 摘要" value="Standard" />
        <el-option label="Detailed · 有界正文摘要" value="Detailed" /><el-option label="Full · 完整脱敏诊断" value="Full" />
      </el-select>
      <el-button type="primary" :disabled="busy || !settings || uncertain || level === settings.level" :loading="busy" @click="save">保存诊断级别</el-button>
      <el-button :disabled="busy" @click="load">刷新设置</el-button>
    </div>
    <div class="controls">
      <el-input-number v-model="mediaItemId" aria-label="导出的媒体项 ID" :min="1" :precision="0" :disabled="busy" />
      <el-button :disabled="busy || !settings || !mediaItemId" @click="download">按媒体项 ID 导出</el-button>
    </div>
  </section>
</template>
<style scoped>
.diagnostics { padding: 18px; margin-bottom: 20px; }
h2 { margin: 0; font-size: 16px; }
p { font-size: 12px; color: var(--text-muted); line-height: 1.7; }
.controls { display: flex; flex-wrap: wrap; gap: 12px; margin-top: 12px; }
[role="alert"] { color: var(--danger); }
</style>
