<script setup>
import { computed, onMounted, ref } from 'vue';
import { ElMessage } from 'element-plus';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

const form = ref({});
const original = ref({});
const loading = ref(false);
const testing = ref(false);
// 编辑 ApiKey 开关：默认 false（显示「已配置（点击修改）」按钮，保存时不传 apiKey 让后端保持原值）
const editKey = ref(false);

// 即时识别用户粘贴的密钥类型：v4 Bearer Token（JWT，eyJ 开头）才正确；v3 ApiKey（32 位 hex）会被搜索接口拒绝。
const apiKeyFormatWarning = computed(() => {
  const raw = (form.value.apiKey || '').trim();
  if (!raw) return '';
  if (/^[a-f0-9]{32}$/i.test(raw)) {
    return '检测到 v3 API Key 格式（32 位 hex），TMDB 搜索接口将返回 401。请改粘 v4 Read Access Token（以 eyJ 开头的长串 JWT）。';
  }
  if (!raw.startsWith('eyJ')) {
    return '该字符串不像 v4 Read Access Token（应以 eyJ 开头）。请确认你复制的是「API 读访问令牌」而非「API 密钥」。';
  }
  return '';
});

// 四项评分权重之和；≈1.0 时绿色提示，偏差超 0.05 时橙色警告
const weightSum = computed(() => {
  const t = Number(form.value.scoreWeightTitle) || 0;
  const y = Number(form.value.scoreWeightYear) || 0;
  const p = Number(form.value.scoreWeightPopularity) || 0;
  const l = Number(form.value.scoreWeightLanguage) || 0;
  return +(t + y + p + l).toFixed(4);
});
const weightSumOk = computed(() => Math.abs(weightSum.value - 1.0) <= 0.05);

async function load() {
  loading.value = true;
  try {
    const data = (await api.tmdbSetting.get()) || {};
    form.value = { ...data };
    original.value = { ...data };
    // 加载后默认进入「已配置」展示态；若未配置（hasApiKey === false）则自动允许编辑
    editKey.value = !data?.hasApiKey;
  } finally {
    loading.value = false;
  }
}

async function save() {
  // editKey=false 时不传 apiKey 字段，避免覆盖后端已存的密钥
  const payload = { ...form.value };
  if (!editKey.value) {
    delete payload.apiKey;
  }
  await api.tmdbSetting.update(payload);
  ElMessage.success('已保存');
  original.value = { ...form.value };
  editKey.value = false;
}

async function test() {
  // 后端 /test 只测「已落库」的 Key，不接受 body 内的临时 Key；前端必须先拦截
  // 「文本框已粘贴但未保存」与「DB 与文本框都为空」两种情况，避免甩出后端 1000 错误。
  if (editKey.value && form.value.apiKey) {
    ElMessage.warning('检测到未保存的 API Key，请先点击「保存配置」后再测试');
    return;
  }
  if (!original.value.hasApiKey) {
    ElMessage.warning('请先粘贴 API Key 并保存配置后再测试');
    return;
  }
  // 后端 /api/settings/tmdb/test 已 catch 网络/鉴权异常，统一返 200 + data.success；
  // 所以 try/catch 永远不抛（除非后端连接失败），必须看 data.success 才能区分成功 / 失败。
  // 历史回归：旧版直接 ElMessage.success 不看 success，无效 Key 时也提示「连接正常」。
  testing.value = true;
  try {
    const result = (await api.tmdbSetting.test()) || {};
    if (result.success) {
      const elapsed = Number(result.elapsedMilliseconds) || 0;
      ElMessage.success(`TMDB 连接正常 · ${elapsed.toFixed(0)}ms`);
    } else {
      // 401 / 403 → ApiKey 无效；其它 → 网络 / 超时
      const status = result.httpStatus != null ? `HTTP ${result.httpStatus} · ` : '';
      const reason = result.errorMessage || '未知错误';
      ElMessage.error(`TMDB 连接失败：${status}${reason}`);
    }
  } finally {
    testing.value = false;
  }
}

async function clearCache() {
  await api.tmdbSetting.clearCache();
  ElMessage.success('缓存已清空');
}

function revert() {
  form.value = { ...original.value };
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="设置"
      title="TMDB"
      subtitle="The Movie Database 是 PMM 唯一的元数据来源。请使用 v4 API Read Access Token（非 v3 API Key），加密后落地数据库。"
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
          <div class="label">API Read Access Token (v4)</div>
          <div class="hint">
            必须使用 themoviedb.org/settings/api 页面 <b>「API 读访问令牌」</b>（v4，长串 JWT，以 <code>eyJ</code> 开头）；<br />
            <b>不要</b>使用下方「API 密钥」（v3，32 位 hex 短串）—— 搜索接口会返 401。
          </div>
        </div>
        <div class="control control-stack">
          <div class="control-row">
            <template v-if="editKey">
              <input class="input font-mono" type="password" v-model="form.apiKey" placeholder="eyJhbGciOi... (粘贴 v4 Read Access Token)" style="flex: 1; max-width: 420px" autocomplete="new-password" />
              <button v-if="original.hasApiKey" class="btn btn-ghost" @click="editKey = false; form.apiKey = original.apiKey">
                取消修改
              </button>
            </template>
            <template v-else>
              <button class="btn" @click="editKey = true; form.apiKey = ''" style="flex: 1; max-width: 420px; justify-content: flex-start">
                <PmmIcon name="edit" :size="14" /> 已配置（点击修改）
              </button>
            </template>
            <button class="btn" @click="test" :disabled="testing">
              <PmmIcon name="link" :size="14" /> 测试
            </button>
          </div>
          <div v-if="editKey && apiKeyFormatWarning" class="key-format-warning">⚠ {{ apiKeyFormatWarning }}</div>
        </div>
      </div>
      <div class="form-row">
        <div>
          <div class="label">状态</div>
          <div class="hint">最近一次「测试」结果。</div>
        </div>
        <div class="control">
          <span v-if="original.hasApiKey || form.apiKey" class="tag tag-success"><span class="tag-dot" />已配置</span>
          <span v-else class="tag tag-danger"><span class="tag-dot" />未配置</span>
        </div>
      </div>
    </section>

    <!-- ── 查询行为 ── -->
    <section class="section-card">
      <header class="section-head"><h3>查询行为</h3></header>

      <div class="form-row">
        <div>
          <div class="label">优先语言</div>
          <div class="hint">返回此语言标题；缺失时回退到回退语言。</div>
        </div>
        <div class="control">
          <select class="select" v-model="form.language" style="width: 220px">
            <option value="zh-CN">简体中文 zh-CN</option>
            <option value="zh-TW">繁体中文 zh-TW</option>
            <option value="en-US">English en-US</option>
            <option value="ja-JP">日本語 ja-JP</option>
          </select>
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">回退语言</div>
          <div class="hint">优先语言缺失时使用；通常保持 en-US 以保证覆盖率。</div>
        </div>
        <div class="control">
          <select class="select" v-model="form.fallbackLanguage" style="width: 220px">
            <option value="en-US">English en-US</option>
            <option value="zh-CN">简体中文 zh-CN</option>
            <option value="zh-TW">繁体中文 zh-TW</option>
            <option value="ja-JP">日本語 ja-JP</option>
          </select>
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">候选结果数</div>
          <div class="hint">搜索时最多取前 N 条候选进行评分排序，越大越准但越慢（建议 5–20）。</div>
        </div>
        <div class="control">
          <input class="input" type="number" min="1" max="50" v-model.number="form.candidateThreshold" style="width: 120px" />
          <span class="unit">条</span>
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">API 速率限制</div>
          <div class="hint">每秒最多发出的 TMDB API 请求数（免费账户默认 50/s）。</div>
        </div>
        <div class="control">
          <input class="input" type="number" min="1" max="200" v-model.number="form.rateLimitPerSecond" style="width: 120px" />
          <span class="unit">次 / 秒</span>
        </div>
      </div>
    </section>

    <!-- ── 缓存 ── -->
    <section class="section-card">
      <header class="section-head"><h3>缓存</h3></header>

      <div class="form-row">
        <div>
          <div class="label">元数据缓存时长</div>
          <div class="hint">电影 / 剧集详情的本地缓存有效期；到期后下次查询重新拉取。</div>
        </div>
        <div class="control">
          <input class="input" type="number" min="1" max="8760" v-model.number="form.metadataCacheHours" style="width: 120px" />
          <span class="unit">小时</span>
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">搜索缓存时长</div>
          <div class="hint">搜索关键词结果的本地缓存有效期；减少重复搜索的 API 消耗。</div>
        </div>
        <div class="control">
          <input class="input" type="number" min="1" max="1440" v-model.number="form.searchCacheMinutes" style="width: 120px" />
          <span class="unit">分钟</span>
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">清空缓存</div>
          <div class="hint">存储位置 &lt;AppData&gt;/cache。清空后下次查询会重新拉取。</div>
        </div>
        <div class="control">
          <button class="btn" @click="clearCache">
            <PmmIcon name="trash" :size="14" /> 清空 TMDB 缓存
          </button>
        </div>
      </div>
    </section>

    <!-- ── 评分权重 ── -->
    <section class="section-card">
      <header class="section-head">
        <h3>评分权重</h3>
        <span class="section-hint">四项权重决定候选匹配的最终得分排序，建议保持总和 ≈ 1.0</span>
      </header>

      <div class="form-row">
        <div>
          <div class="label">标题权重</div>
          <div class="hint">文件名与 TMDB 标题相似度（编辑距离）的得分占比。</div>
        </div>
        <div class="control">
          <input class="input" type="number" min="0" max="1" step="0.05" v-model.number="form.scoreWeightTitle" style="width: 120px" />
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">年份权重</div>
          <div class="hint">解析年份与 TMDB 发行年份一致时的得分加成占比。</div>
        </div>
        <div class="control">
          <input class="input" type="number" min="0" max="1" step="0.05" v-model.number="form.scoreWeightYear" style="width: 120px" />
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">热度权重</div>
          <div class="hint">TMDB 综合热度分数（popularity）归一化后的得分占比。</div>
        </div>
        <div class="control">
          <input class="input" type="number" min="0" max="1" step="0.05" v-model.number="form.scoreWeightPopularity" style="width: 120px" />
        </div>
      </div>

      <div class="form-row">
        <div>
          <div class="label">语言权重</div>
          <div class="hint">结果原始语言与优先语言匹配时的得分加成占比。</div>
        </div>
        <div class="control">
          <input class="input" type="number" min="0" max="1" step="0.05" v-model.number="form.scoreWeightLanguage" style="width: 120px" />
        </div>
      </div>

      <!-- 权重总和提示行 -->
      <div class="weight-sum-row">
        <span class="weight-sum-label">当前总和：</span>
        <span :class="['weight-sum-value', weightSumOk ? 'ok' : 'warn']">{{ weightSum }}</span>
        <span v-if="!weightSumOk" class="weight-sum-tip">⚠ 偏离 1.0 超过 0.05，建议调整以获得最佳匹配效果</span>
        <span v-else class="weight-sum-tip ok">✓ 总和正常</span>
      </div>
    </section>
  </div>
</template>

<style scoped lang="scss">
/* 单位文本（条 / 小时 / 分钟）*/
.unit {
  color: var(--text-mute);
  font-size: 13px;
  white-space: nowrap;
}

/* section-head 内联 hint */
.section-head {
  display: flex;
  align-items: baseline;
  gap: 12px;
}
.section-hint {
  font-size: 12px;
  color: var(--text-dim);
}

/* 权重总和提示行 */
.weight-sum-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 0 4px 0;
  border-top: 1px solid var(--border);
  margin-top: 4px;
}
.weight-sum-label {
  font-size: 13px;
  color: var(--text-mute);
}
.weight-sum-value {
  font-size: 14px;
  font-weight: 600;
  min-width: 52px;
  &.ok   { color: var(--success); }
  &.warn { color: var(--warning); }
}
.weight-sum-tip {
  font-size: 12px;
  color: var(--warning);
  &.ok { color: var(--success); }
}

/* API Key 区块：竖向堆叠（控件行 + 实时格式警告行）*/
.control-stack {
  display: flex;
  flex-direction: column;
  gap: 6px;
  align-items: stretch !important;
}
.control-row {
  display: flex;
  align-items: center;
  gap: 8px;
}
.key-format-warning {
  font-size: 12px;
  color: var(--warning);
  background: rgba(217, 119, 6, 0.08);
  border: 1px solid rgba(217, 119, 6, 0.25);
  border-radius: 6px;
  padding: 6px 10px;
  max-width: 560px;
  line-height: 1.5;
}

/* hint 内嵌 code 样式 */
.hint code {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  background: var(--surface-2);
  padding: 1px 4px;
  border-radius: 3px;
  font-size: 12px;
}
</style>
