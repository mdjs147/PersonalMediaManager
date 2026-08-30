<script setup>
import { computed, onMounted, ref } from 'vue';
import { ElMessage } from 'element-plus';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

/**
 * 代理设置页（仿 UpdateSettings.vue 结构）
 *
 * 数据源：通用 System_Setting KV（/api/settings/general），5 个 Proxy_* key
 *   - Proxy_Enabled           总开关
 *   - Proxy_HttpUrl            代理地址，如 http://127.0.0.1:7890
 *   - Proxy_BypassList         额外 bypass 规则（逗号/换行分隔，支持 *）
 *   - Proxy_UseForTmdb         TMDB 客户端是否走代理
 *   - Proxy_UseForUpdateCheck  GitHub 升级检查是否走代理
 *
 * AI 提供商「是否走代理」落在 Parse_AiProvider.UseProxy 列，由 AiProviders.vue 编辑表单维护，
 * 本页只读统计（启用代理的 Provider 数），并提供跳转入口。
 *
 * 内置不可关 bypass：loopback / 私有网段 / *.local —— 保护内网流量不被送到代理。
 * 保存后 60 秒内（IProxyResolver 缓存 TTL）所有出站请求生效；后端写入时主动 Invalidate。
 */

const BUILTIN_BYPASS = [
  'loopback',
  '10.0.0.0/8',
  '172.16.0.0/12',
  '192.168.0.0/16',
  '169.254.0.0/16',
  '*.local',
];

const form = ref({
  enabled: false,
  httpUrl: '',
  bypassList: '',
  useForTmdb: true,
  useForUpdateCheck: true,
});

const loading = ref(false);
const saving = ref(false);

// AI 提供商「通过代理访问」的统计（只读展示用）
const aiProxyStats = ref({ total: 0, useProxy: 0 });

const fieldsDisabled = computed(() => !form.value.enabled);

/**
 * URL 实时校验：返回 { ok, level, message }
 *   - level: 'success' | 'error' | 'idle'
 *   - 空 URL 在 enabled=true 时为 error，enabled=false 时为 idle
 */
const urlValidation = computed(() => {
  const raw = (form.value.httpUrl || '').trim();
  if (!raw) {
    return form.value.enabled
      ? { ok: false, level: 'error', message: '代理地址不能为空' }
      : { ok: true, level: 'idle', message: '' };
  }
  let u;
  try {
    u = new URL(raw);
  } catch {
    return { ok: false, level: 'error', message: '不是合法 URL（示例 http://127.0.0.1:7890）' };
  }
  if (u.protocol !== 'http:' && u.protocol !== 'https:') {
    return { ok: false, level: 'error', message: `协议必须是 http/https（当前 ${u.protocol}）` };
  }
  if (!u.hostname) {
    return { ok: false, level: 'error', message: '缺少主机名' };
  }
  if (u.port) {
    const p = Number(u.port);
    if (!Number.isInteger(p) || p < 1 || p > 65535) {
      return { ok: false, level: 'error', message: '端口必须在 1~65535' };
    }
  }
  return { ok: true, level: 'success', message: `格式合法 · ${u.protocol}//${u.host}` };
});

const statusBanner = computed(() => {
  if (!form.value.enabled) {
    return { tone: 'neutral', title: '代理已禁用', desc: '所有出站请求直连，本地与私有网段始终生效。' };
  }
  if (!urlValidation.value.ok) {
    return { tone: 'warning', title: '代理已开启但地址无效', desc: urlValidation.value.message };
  }
  const url = (form.value.httpUrl || '').trim();
  return { tone: 'success', title: '代理已启用', desc: `受控出站经 ${url} 转发；本地与私有网段直连。` };
});

async function load() {
  loading.value = true;
  try {
    const [settingsData, aiProviders] = await Promise.all([
      api.generalSettings.get().catch(() => null),
      api.parseAiProviders.list().catch(() => []),
    ]);
    const items = [];
    if (settingsData?.groups) {
      Object.values(settingsData.groups).forEach((arr) => arr.forEach((it) => items.push(it)));
    }
    const kv = Object.fromEntries(items.map((it) => [it.key, it.value]));
    form.value = {
      enabled: kv.Proxy_Enabled === 'true',
      httpUrl: kv.Proxy_HttpUrl || '',
      bypassList: kv.Proxy_BypassList || '',
      useForTmdb: kv.Proxy_UseForTmdb !== 'false', // 默认 true
      useForUpdateCheck: kv.Proxy_UseForUpdateCheck !== 'false',
    };
    const list = Array.isArray(aiProviders) ? aiProviders : [];
    aiProxyStats.value = {
      total: list.length,
      useProxy: list.filter((p) => p?.useProxy === true).length,
    };
  } finally {
    loading.value = false;
  }
}

async function save() {
  if (form.value.enabled && !urlValidation.value.ok) {
    ElMessage.error(urlValidation.value.message || '代理地址不合法');
    return;
  }
  saving.value = true;
  try {
    const url = (form.value.httpUrl || '').trim();
    const payload = {
      items: [
        { key: 'Proxy_Enabled', value: form.value.enabled ? 'true' : 'false' },
        { key: 'Proxy_HttpUrl', value: url || null },
        { key: 'Proxy_BypassList', value: (form.value.bypassList || '').trim() || null },
        { key: 'Proxy_UseForTmdb', value: form.value.useForTmdb ? 'true' : 'false' },
        { key: 'Proxy_UseForUpdateCheck', value: form.value.useForUpdateCheck ? 'true' : 'false' },
      ],
    };
    await api.generalSettings.update(payload);
    ElMessage.success('已保存（60 秒内对所有出站请求生效）');
  } finally {
    saving.value = false;
  }
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="设置"
      title="代理"
      subtitle="为出站流量配置 HTTP 代理；本地与私有网段自动直连，不会被送到代理。"
    >
      <template #actions>
        <button class="btn btn-primary btn-sm" @click="save" :disabled="saving">
          <PmmIcon name="check" :size="14" /> 保存配置
        </button>
      </template>
    </PmmPageHeader>

    <!-- ── 顶部状态横幅 ── -->
    <div class="status-banner" :class="`status-banner--${statusBanner.tone}`">
      <PmmIcon
        :name="statusBanner.tone === 'success' ? 'shield' : statusBanner.tone === 'warning' ? 'warning' : 'globe'"
        :size="18"
      />
      <div class="status-banner__body">
        <div class="status-banner__title">{{ statusBanner.title }}</div>
        <div class="status-banner__desc">{{ statusBanner.desc }}</div>
      </div>
    </div>

    <!-- ── 总开关与代理地址 ── -->
    <section class="section-card">
      <header class="section-head"><h3>代理服务器</h3></header>

      <div class="form-row">
        <div>
          <div class="label">启用代理</div>
          <div class="hint">关闭后下面的所有选项无效；本地地址（127.0.0.1 / 私有网段 / *.local）始终直连。</div>
        </div>
        <div class="control">
          <el-switch v-model="form.enabled" active-text="开" inactive-text="关" inline-prompt />
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">代理地址</div>
          <div class="hint">支持 http / https；如需账密用 <code>http://user:pass@host:port</code> 形式。</div>
        </div>
        <div class="control">
          <input
            class="input font-mono"
            v-model="form.httpUrl"
            placeholder="http://127.0.0.1:7890"
            style="flex: 1; max-width: 460px"
            autocomplete="off"
            :disabled="fieldsDisabled"
          />
          <span
            v-if="urlValidation.level === 'success'"
            class="tag tag-success"
          >
            <PmmIcon name="check" :size="11" />
            合法
          </span>
          <span
            v-else-if="urlValidation.level === 'error'"
            class="tag tag-danger"
          >
            <PmmIcon name="warning" :size="11" />
            {{ urlValidation.message }}
          </span>
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">额外 Bypass</div>
          <div class="hint">逗号或换行分隔的 host 通配符（如 <code>*.cn,corp.local</code>），写在这里的会追加到内置规则后面。</div>
        </div>
        <div class="control control--stack">
          <textarea
            class="input font-mono"
            v-model="form.bypassList"
            rows="3"
            style="width: 100%; max-width: 460px; resize: vertical"
            placeholder="*.cn, corp.local"
            :disabled="fieldsDisabled"
          />
          <div class="bypass-builtin">
            <span class="bypass-builtin__label">内置规则（不可关）</span>
            <span v-for="rule in BUILTIN_BYPASS" :key="rule" class="tag tag-neutral font-mono">
              {{ rule }}
            </span>
          </div>
        </div>
      </div>
    </section>

    <!-- ── 按使用方分桶开关 ── -->
    <section class="section-card">
      <header class="section-head"><h3>哪些出站走代理</h3></header>

      <div class="form-row">
        <div>
          <div class="label">TMDB</div>
          <div class="hint">TMDB v3 客户端 / 连通性探测 / 海报下载，统一受此开关控制。</div>
        </div>
        <div class="control">
          <el-switch
            v-model="form.useForTmdb"
            active-text="开" inactive-text="关" inline-prompt
            :disabled="fieldsDisabled"
          />
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">GitHub 升级检查</div>
          <div class="hint">api.github.com /releases/latest 周期检查；国内访问常需代理。</div>
        </div>
        <div class="control">
          <el-switch
            v-model="form.useForUpdateCheck"
            active-text="开" inactive-text="关" inline-prompt
            :disabled="fieldsDisabled"
          />
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">AI 提供商</div>
          <div class="hint">
            AI 是否走代理是<strong>每个 Provider 单独配置</strong>的（本地 Ollama 通常不需要，
            云端 OpenAI 兼容端点常需要），在 AI 提供商页编辑各条记录的「通过代理访问」复选框。
          </div>
        </div>
        <div class="control">
          <span class="tag tag-info">
            <span class="tag-dot" />
            {{ aiProxyStats.useProxy }} / {{ aiProxyStats.total }} 已启用代理
          </span>
          <router-link to="/settings/parse-ai-providers" class="btn">
            前往配置
            <PmmIcon name="chevronRight" :size="14" />
          </router-link>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped lang="scss">
/* 顶部状态横幅 —— success/warning/neutral 三态，色块取自全局 *-soft / * 变量 */
.status-banner {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  padding: 12px 16px;
  border-radius: var(--r-3, 12px);
  border: 1px solid var(--border-soft);
  margin-bottom: 16px;
  background: var(--surface-2);
}
.status-banner__body {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.status-banner__title {
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
}
.status-banner__desc {
  font-size: 12px;
  color: var(--text-mute);
  word-break: break-all;
}
.status-banner--success {
  background: var(--success-soft);
  border-color: transparent;
  color: var(--success);
}
.status-banner--success .status-banner__title { color: var(--success); }
.status-banner--success .status-banner__desc { color: var(--text); opacity: 0.85; }

.status-banner--warning {
  background: var(--warning-soft);
  border-color: transparent;
  color: var(--warning);
}
.status-banner--warning .status-banner__title { color: var(--warning); }
.status-banner--warning .status-banner__desc { color: var(--text); opacity: 0.85; }

.status-banner--neutral {
  background: var(--surface-2);
  color: var(--text-mute);
}

/* bypass：编辑区与内置规则上下排列，textarea 占满，标签紧贴下方 */
.control--stack {
  flex-direction: column;
  align-items: flex-start;
  gap: 8px;
}
.bypass-builtin {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}
.bypass-builtin__label {
  font-size: 12px;
  color: var(--text-mute);
  margin-right: 4px;
}

code {
  background: var(--surface-2);
  padding: 1px 6px;
  border-radius: 4px;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 12px;
}
.link {
  color: var(--accent, #3b82f6);
  text-decoration: underline;
}
</style>
