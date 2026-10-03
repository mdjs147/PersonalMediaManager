<script setup>
import { onMounted } from 'vue';
import { aiBatchApi } from '@/api/aiBatch';
import { batchSizeFields, batchBudgetFields, batchWaitField, providerBatchFields, maxProviderOverrides, useAiBatchSettings } from '@/composables/useAiBatchSettings';
const { form, saved, ready, busy, error, notice, uncertain, dirty, providerOptions, providerError, providerLimitReached,
  selectedProviderId, selectedProvider, selectedProviderSettings, selectedSavedSettings, effectiveProviderSettings,
  providerConfigurationChanged, providerNeedsRebind, load, reset, save, setProviderOverride, setProviderField,
  rebindProvider, applyProviderPreset } = useAiBatchSettings(aiBatchApi);
onMounted(() => load());
</script>

<template>
  <section class="batch-settings card" aria-label="AI 批量解析设置">
    <div class="batch-heading"><h2>批量解析设置</h2><el-tag type="warning" size="small">实验性</el-tag></div>
    <p class="batch-hint">外部与内置模型默认每批 1 个文件；内置最多 2 个，外部最多 128 个。只有手动修改并保存才生效，不改变模型启用状态。</p>
    <p class="batch-caution">文件数是每批上限，并非每次必发数量；上下文、输出与响应预算会让长内容拆成更小批次。调大不保证更准确或更快，候选作品身份仍须核验。</p>
    <p class="batch-hint">独立 synthetic（合成样例）评测不代表 PMM 产品或你的私有媒体准确率，请先验证自己的样例。</p>
    <p v-if="error" class="batch-error" role="alert">{{ error }}</p>
    <p v-if="notice" class="batch-notice" role="status">{{ notice }}</p>
    <p v-if="uncertain" class="batch-caution" role="status">提交结果尚未核实，编辑已锁定。请刷新批量设置，确认服务端当前值后再操作。</p>
    <template v-if="ready">
      <h3>默认批量大小</h3>
      <div class="batch-controls">
        <label v-for="field in [...batchSizeFields, batchWaitField]" :key="field.key" class="batch-field">
          <span>{{ field.label }}</span>
          <el-input-number v-model="form[field.key]" :min="field.min" :max="field.max" :precision="0" :disabled="busy || uncertain" controls-position="right" :aria-label="field.label" />
          <span class="batch-hint">已保存 {{ saved?.[field.key] }} · 范围 {{ field.min }}～{{ field.max }}</span>
        </label>
      </div>
      <h3>默认预算</h3>
      <p class="batch-hint">未单独覆盖的提供商继承这些预算。请按实际模型和服务限制填写，预算值不会扩展服务本身的能力。</p>
      <div class="batch-controls">
        <label v-for="field in batchBudgetFields" :key="field.key" class="batch-field">
          <span>{{ field.label }}</span>
          <el-input-number v-model="form[field.key]" :min="field.min" :max="field.max" :precision="0" :disabled="busy || uncertain" controls-position="right" :aria-label="`默认${field.label}`" />
          <span class="batch-hint">已保存 {{ saved?.[field.key] }} · 范围 {{ field.min }}～{{ field.max }}</span>
        </label>
      </div>
      <div class="batch-provider">
        <h3>外部提供商单独设置</h3>
        <p :class="providerLimitReached ? 'batch-caution' : 'batch-hint'">已设置 {{ form.providerSettings.length }}/{{ maxProviderOverrides }} 条覆盖。{{ providerLimitReached ? '已达到上限，可继续编辑或删除已有覆盖；请移除不再使用的覆盖后再添加。' : '无需单独设置的提供商会继承默认值。' }}</p>
        <p v-if="providerError" class="batch-error" role="alert">{{ providerError }}</p>
        <p v-if="!providerOptions.length && !providerError" class="batch-hint">暂无外部提供商。添加提供商后刷新，即可单独设置大小和预算。</p>
        <template v-if="providerOptions.length">
          <label class="batch-provider-select">
            <span>选择提供商</span>
            <el-select v-model="selectedProviderId" :disabled="busy || uncertain" aria-label="选择批量设置提供商">
              <el-option v-for="provider in providerOptions" :key="provider.providerId" :value="provider.providerId" :label="`${provider.name}${provider.model ? ` · ${provider.model}` : ''} (#${provider.providerId})`" />
            </el-select>
          </label>
          <div class="batch-override">
            <el-switch :model-value="Boolean(selectedProviderSettings)" :disabled="busy || uncertain || ((!selectedProvider || providerLimitReached) && !selectedProviderSettings)" aria-label="单独覆盖此提供商的批量设置" @update:model-value="setProviderOverride" />
            <span>单独覆盖此提供商</span>
            <span class="batch-hint">{{ selectedProviderSettings ? '关闭并保存后恢复继承默认设置' : '继承默认大小与预算' }}</span>
          </div>
          <p v-if="!selectedProvider" class="batch-caution">当前提供商目录不可用，无法核对配置。可关闭此覆盖后保存以恢复继承，或刷新目录后再编辑。</p>
          <p v-else-if="providerConfigurationChanged" class="batch-caution" role="status">配置已变化，请核对并重新保存。旧覆盖不会应用；请核对下方全部大小和预算，或关闭覆盖以恢复继承。</p>
          <p v-else-if="providerNeedsRebind && selectedProviderSettings" class="batch-caution" role="status">覆盖草稿已绑定当前配置，尚未保存。请确认全部大小和预算适合当前模型后保存。</p>
          <div v-if="selectedProvider?.recommendedSettings || selectedProvider?.advancedSettings" class="batch-presets">
            <el-button v-if="selectedProvider?.recommendedSettings" :disabled="busy || uncertain || (providerLimitReached && !selectedProviderSettings)" @click="applyProviderPreset('recommendedSettings')">填入 {{ selectedProvider.recommendedSettings.batchSize }} {{ selectedProvider.recommendedSettings.disableThinking ? '非思考模式' : '' }}推荐配置</el-button>
            <el-button v-if="selectedProvider?.advancedSettings" type="warning" plain :disabled="busy || uncertain || (providerLimitReached && !selectedProviderSettings)" @click="applyProviderPreset('advancedSettings')">填入 {{ selectedProvider.advancedSettings.batchSize }} {{ selectedProvider.advancedSettings.disableThinking ? '非思考模式' : '' }}高级配置</el-button>
            <span class="batch-hint">仅填入此提供商的草稿，明确保存后生效。高级设置请先用自己的样例验证。</span>
          </div>
          <p v-if="selectedProviderSettings?.disableThinking" class="batch-hint">{{ providerConfigurationChanged || !selectedProvider ? '旧覆盖曾选择非思考模式；当前配置需要重新核对。' : '已选非思考模式配置；未保存修改仅影响草稿，保存后生效。' }}</p>
          <div v-if="selectedProviderSettings" class="batch-controls">
            <label v-for="field in providerBatchFields" :key="field.key" class="batch-field">
              <span>{{ field.label }}</span>
              <el-input-number :model-value="selectedProviderSettings[field.key]" :min="field.min" :max="field.max" :precision="0" :disabled="busy || uncertain || !selectedProvider" controls-position="right" :aria-label="`提供商${field.label}`" @update:model-value="setProviderField(field.key, $event)" />
              <span class="batch-hint">{{ selectedSavedSettings ? `已保存 ${selectedSavedSettings[field.key]}` : '尚无已保存覆盖' }} · 范围 {{ field.min }}～{{ field.max }}</span>
            </label>
          </div>
          <p v-else class="batch-hint">当前草稿继承：每批最多 {{ effectiveProviderSettings.batchSize }} 个文件，上下文 {{ effectiveProviderSettings.contextTokenBudget }} token，输出 {{ effectiveProviderSettings.maxOutputTokens }} token，响应 {{ effectiveProviderSettings.maxResponseBytes }} 字节。</p>
          <el-button v-if="providerConfigurationChanged" :disabled="busy || uncertain" @click="rebindProvider">已核对全部预算，绑定当前配置草稿</el-button>
        </template>
      </div>
    </template>
    <div class="batch-actions">
      <el-button v-if="ready" type="primary" :loading="busy" :disabled="busy || !dirty || uncertain" @click="save">保存批量设置</el-button>
      <el-button v-if="ready && dirty && !uncertain" :disabled="busy" @click="reset">撤销未保存修改</el-button>
      <el-button :disabled="busy" :loading="busy && !ready" @click="load(true)">{{ ready ? '刷新批量设置' : '加载批量设置' }}</el-button>
      <span class="batch-hint">外部设置可独立保存，不需要内置运行时或模型开启</span>
    </div>
  </section>
</template>

<style scoped>
.batch-settings { padding: 18px; margin-bottom: 20px; }
.batch-heading, .batch-actions, .batch-override, .batch-presets { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
.batch-heading h2 { margin: 0; font-size: 16px; }
h3 { margin: 18px 0 10px; font-size: 14px; }
.batch-controls { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); margin: 14px 0; gap: 16px 24px; }
.batch-field { display: flex; align-items: flex-start; gap: 6px; flex-direction: column; font-size: 13px; min-width: 0; }
.batch-field :deep(.el-input-number) { width: min(100%, 220px); }
.batch-provider { margin: 18px 0; padding-top: 1px; border-top: 1px solid var(--border-color, #dcdfe6); }
.batch-provider-select { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; font-size: 13px; }
.batch-provider-select :deep(.el-select) { width: min(100%, 460px); }
.batch-override, .batch-presets { margin-top: 12px; font-size: 13px; }
.batch-actions { margin-top: 20px; }
.batch-hint { font-size: 12px; color: var(--text-muted); line-height: 1.7; }
.batch-caution { font-size: 12px; color: var(--warning, #b78b31); line-height: 1.7; }
.batch-error { color: var(--danger); font-size: 12px; }
.batch-notice { color: var(--success); font-size: 12px; }
</style>
