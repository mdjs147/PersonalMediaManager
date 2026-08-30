<script setup>
import { onMounted, ref } from 'vue';
import { ElMessage } from 'element-plus';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

const form = ref({ token: '', baseUrl: 'https://api.assrt.net', timeoutSeconds: 30, useProxy: false, hasToken: false });
const original = ref({});
const loading = ref(false);
const testing = ref(false);
// 编辑 Token 开关：默认 false（显示「已配置（点击修改）」按钮，保存时不传 token 字段让后端保持原值）
const editKey = ref(false);

async function load() {
  loading.value = true;
  try {
    const data = (await api.subtitles.setting()) || {};
    form.value = {
      token: '',
      baseUrl: data.baseUrl || 'https://api.assrt.net',
      timeoutSeconds: data.timeoutSeconds ?? 30,
      useProxy: !!data.useProxy,
      hasToken: !!data.hasToken,
    };
    original.value = { ...form.value };
    // 加载后默认进入「已配置」展示态；若未配置则自动允许编辑
    editKey.value = !data.hasToken;
  } finally {
    loading.value = false;
  }
}

async function save() {
  // token 三态契约：editKey=false 不传字段（后端保持原值）；editKey=true 传文本框值（空串=清空，非空=设置）
  const payload = {
    baseUrl: form.value.baseUrl,
    timeoutSeconds: form.value.timeoutSeconds,
    useProxy: form.value.useProxy,
  };
  if (editKey.value) {
    payload.token = form.value.token ?? '';
  }
  const data = (await api.subtitles.updateSetting(payload)) || {};
  ElMessage.success('已保存');
  form.value = { ...form.value, token: '', hasToken: !!data.hasToken };
  original.value = { ...form.value };
  editKey.value = !form.value.hasToken;
}

async function test() {
  // 后端 /test 只测「已落库」的 Token，不接受临时值；先拦截「已粘贴未保存」与「从未配置」两种情况
  if (editKey.value && form.value.token) {
    ElMessage.warning('检测到未保存的 Token，请先点击「保存配置」后再测试');
    return;
  }
  if (!original.value.hasToken) {
    ElMessage.warning('请先粘贴 Assrt Token 并保存配置后再测试');
    return;
  }
  // 后端 test 已 catch 网络 / 鉴权异常，统一返 200 + data.success；try/catch 不抛，必须看 success 字段分支
  testing.value = true;
  try {
    const result = (await api.subtitles.testSetting()) || {};
    if (result.success) {
      const elapsed = Number(result.elapsedMilliseconds) || 0;
      ElMessage.success(`连接成功，剩余配额 ${result.quota ?? '—'} · ${elapsed.toFixed(0)}ms`);
    } else {
      ElMessage.error(`连接失败：${result.errorMessage || '未知错误'}`);
    }
  } finally {
    testing.value = false;
  }
}

function revert() {
  form.value = { ...original.value };
  editKey.value = !original.value.hasToken;
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="设置"
      title="字幕下载"
      subtitle="字幕下载为手动触发：在历史记录或媒体详情中对已归档记录选择『下载字幕』。首发支持 Assrt（伪·射手网）字幕源。"
    >
      <template #actions>
        <button class="btn btn-ghost btn-sm" @click="revert">撤销</button>
        <button class="btn btn-primary btn-sm" @click="save">
          <PmmIcon name="check" :size="14" /> 保存配置
        </button>
      </template>
    </PmmPageHeader>

    <!-- ── API 凭据 ── -->
    <section class="section-card">
      <header class="section-head"><h3>API 凭据</h3></header>
      <div class="form-row">
        <div>
          <div class="label">Assrt Token</div>
          <div class="hint">
            在 assrt.net 注册后于个人页获取 API Token：
            <a href="https://secure.assrt.net/user/profile" target="_blank" rel="noopener">secure.assrt.net/user/profile</a>
          </div>
        </div>
        <div class="control">
          <template v-if="editKey">
            <input
              class="input font-mono"
              type="password"
              v-model="form.token"
              placeholder="粘贴 Assrt API Token"
              style="flex: 1; max-width: 420px"
              autocomplete="new-password"
            />
            <button v-if="original.hasToken" class="btn btn-ghost" @click="editKey = false; form.token = ''">
              取消修改
            </button>
          </template>
          <template v-else>
            <button class="btn" @click="editKey = true; form.token = ''" style="flex: 1; max-width: 420px; justify-content: flex-start">
              <PmmIcon name="edit" :size="14" /> 已配置（点击修改）
            </button>
          </template>
          <button class="btn" @click="test" :disabled="testing">
            <PmmIcon name="link" :size="14" /> {{ testing ? '测试中…' : '测试连接' }}
          </button>
        </div>
      </div>
      <div class="form-row">
        <div>
          <div class="label">状态</div>
          <div class="hint">Token 配置状态；连接结果以「测试连接」为准。</div>
        </div>
        <div class="control">
          <span v-if="original.hasToken || form.token" class="tag tag-success"><span class="tag-dot" />已配置</span>
          <span v-else class="tag tag-danger"><span class="tag-dot" />未配置</span>
        </div>
      </div>
    </section>

    <!-- ── 连接行为 ── -->
    <section class="section-card">
      <header class="section-head"><h3>连接行为</h3></header>

      <div class="form-row">
        <div>
          <div class="label">API 地址</div>
          <div class="hint">默认 https://api.assrt.net；直连不稳定时可填镜像地址。</div>
        </div>
        <div class="control">
          <input class="input font-mono" type="text" v-model="form.baseUrl" placeholder="https://api.assrt.net" style="width: 320px" />
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">超时秒数</div>
          <div class="hint">单次搜索 / 下载请求的超时时间（5–120 秒）。</div>
        </div>
        <div class="control">
          <input class="input" type="number" min="5" max="120" v-model.number="form.timeoutSeconds" style="width: 120px" />
          <span class="unit">秒</span>
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">走代理</div>
          <div class="hint">使用系统代理设置中的代理出网。</div>
        </div>
        <div class="control">
          <el-switch v-model="form.useProxy" active-text="开" inactive-text="关" inline-prompt />
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped lang="scss">
/* 单位文本（秒）*/
.unit {
  color: var(--text-mute);
  font-size: 13px;
  white-space: nowrap;
}

/* hint 内外链样式 */
.hint a {
  color: var(--accent, #4d8df0);
  text-decoration: none;
  &:hover { text-decoration: underline; }
}
</style>
