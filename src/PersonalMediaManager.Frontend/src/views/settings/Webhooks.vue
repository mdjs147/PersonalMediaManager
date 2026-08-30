<script setup>
import { onMounted, ref } from 'vue';
import { ElMessage, ElMessageBox, ElDialog } from 'element-plus';
import { useRouter } from 'vue-router';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

const router = useRouter();

const list = ref([]);
const loading = ref(false);
const dialogVisible = ref(false);
const editing = ref(null);
const form = ref({ name: '', url: '', events: [], secret: '', enabled: true, timeoutSeconds: 10 });
const globalEnabled = ref(false);

// 与后端 API 规范一致的事件值（docs/API规范.md & ArchiveService）
const EVENT_OPTIONS = [
  { value: 'media.archived', label: '媒体归档成功' },
  { value: 'media.skipped', label: '媒体已跳过' },
  { value: 'media.failed', label: '媒体处理失败' },
  { value: 'review.created', label: '待人工确认' },
  { value: 'backup.failed', label: '定时备份失败' },
  { value: 'disk.low', label: '归档磁盘将满' },
  { value: 'share.unreachable', label: '网络盘掉线' },
  { value: 'ai.all_unavailable', label: 'AI 全部不可用' },
  { value: 'ai.provider_quota_exceeded', label: 'AI 提供商套餐超限' },
];
const EVENT_LABEL = Object.fromEntries(EVENT_OPTIONS.map((o) => [o.value, o.label]));

async function load() {
  loading.value = true;
  try {
    const data = await api.webhooks.list();
    list.value = data?.items || data || [];
  } finally {
    loading.value = false;
  }
}

async function loadGlobal() {
  try {
    const data = await api.webhooks.getEnabled();
    globalEnabled.value = !!data?.enabled;
  } catch {
    globalEnabled.value = false;
  }
}

async function toggleGlobal() {
  const next = !globalEnabled.value;
  await api.webhooks.setEnabled(next);
  globalEnabled.value = next;
  ElMessage.success(next ? 'Webhook 已开启' : 'Webhook 已关闭（归档不再产生投递记录）');
}

function openAdd() {
  editing.value = null;
  form.value = { name: '', url: '', events: ['media.archived'], secret: '', enabled: true, timeoutSeconds: 10 };
  dialogVisible.value = true;
}

function openEdit(row) {
  editing.value = row;
  form.value = {
    name: row.name || '',
    url: row.url || '',
    events: Array.isArray(row.events) ? [...row.events] : [],
    // 后端 Secret 三态：null=不变；编辑回填用 null 表示「不修改」
    secret: null,
    enabled: row.enabled,
    timeoutSeconds: row.timeoutSeconds ?? 10,
  };
  dialogVisible.value = true;
}

async function save() {
  if (!form.value.url) {
    ElMessage.warning('请填写 URL');
    return;
  }
  if (!form.value.events?.length) {
    ElMessage.warning('请至少选择一个事件');
    return;
  }
  if (editing.value) {
    await api.webhooks.update(editing.value.id, form.value);
    ElMessage.success('已更新');
  } else {
    await api.webhooks.create(form.value);
    ElMessage.success('已添加');
  }
  dialogVisible.value = false;
  load();
}

async function toggle(row) {
  await api.webhooks.update(row.id, {
    name: row.name,
    url: row.url,
    secret: null,
    events: row.events || [],
    enabled: !row.enabled,
    timeoutSeconds: row.timeoutSeconds ?? 10,
  });
  load();
}

async function remove(row) {
  await ElMessageBox.confirm(`删除订阅 "${row.name || row.url}"？`, '提示', { type: 'warning' });
  await api.webhooks.delete(row.id);
  ElMessage.success('已删除');
  load();
}

const testing = ref(new Set());

async function testSend(row) {
  if (testing.value.has(row.id)) return;
  testing.value = new Set([...testing.value, row.id]);
  try {
    const data = await api.webhooks.test(row.id);
    if (data?.success) {
      ElMessage.success(
        `测试成功：HTTP ${data.httpStatus} · ${Math.round(data.elapsedMilliseconds)}ms`,
      );
    } else {
      ElMessage.error(
        `测试失败：${data?.errorMessage || `HTTP ${data?.httpStatus ?? '—'}`} · ${Math.round(data?.elapsedMilliseconds || 0)}ms`,
      );
    }
  } catch (e) {
    ElMessage.error(`测试失败：${e?.message || e}`);
  } finally {
    const next = new Set(testing.value);
    next.delete(row.id);
    testing.value = next;
  }
}

onMounted(() => {
  load();
  loadGlobal();
});
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="设置"
      title="Webhook"
      subtitle="把媒体处理事件推送到外部服务（自动化平台、Bot、监控系统）。事件 HMAC 签名后投递。"
    >
      <template #actions>
        <button class="btn btn-primary btn-sm" @click="openAdd">
          <PmmIcon name="plus" :size="14" /> 添加订阅
        </button>
      </template>
    </PmmPageHeader>

    <div class="card master-switch">
      <div class="ms-left">
        <div class="switch" :class="{ on: globalEnabled }" @click="toggleGlobal" />
        <div>
          <div class="ms-title">Webhook 总开关</div>
          <div class="muted small">
            {{ globalEnabled ? '已开启：归档事件按下方订阅推送' : '已关闭：归档不推送，也不产生投递记录' }}
          </div>
        </div>
      </div>
      <span v-if="globalEnabled" class="tag tag-success"><span class="tag-dot" />运行中</span>
      <span v-else class="tag tag-warning">全局关闭中</span>
    </div>

    <div class="vstack" style="--gap: 14px">
      <div v-for="row in list" :key="row.id" class="card webhook-card">
        <div class="wh-row">
          <div class="wh-icon">
            <PmmIcon name="webhook" :size="20" />
          </div>
          <div class="wh-meta">
            <div class="wh-head">
              <span class="font-mono wh-url">{{ row.url }}</span>
              <span v-if="row.enabled" class="tag tag-success"><span class="tag-dot" />启用</span>
              <span v-else class="tag tag-neutral">禁用</span>
            </div>
            <div class="wh-name muted small">{{ row.name || '未命名订阅' }}</div>
            <div class="wh-events">
              <span v-for="ev in (row.events || [])" :key="ev" class="tag tag-neutral">
                {{ EVENT_LABEL[ev] || ev }}
              </span>
              <span v-if="!(row.events || []).length" class="tag tag-neutral muted">未订阅事件</span>
            </div>
          </div>
          <div class="wh-stats">
            <div class="stat">
              <div class="eyebrow xs">已投递</div>
              <div class="font-display tabular val">{{ row.deliveredCount ?? 0 }}</div>
            </div>
            <div class="stat">
              <div class="eyebrow xs">失败</div>
              <div class="font-display tabular val" :class="{ warn: (row.failedCount || 0) > 0 }">{{ row.failedCount ?? 0 }}</div>
            </div>
          </div>
          <div class="wh-tools">
            <div class="switch" :class="{ on: row.enabled }" @click="toggle(row)" />
            <button class="icon-btn" @click="openEdit(row)"><PmmIcon name="edit" :size="16" /></button>
            <button class="icon-btn" @click="remove(row)"><PmmIcon name="trash" :size="16" /></button>
          </div>
        </div>
        <hr class="divider" />
        <div class="wh-actions">
          <PmmIcon name="info" :size="13" style="color: var(--text-mute)" />
          <span class="muted small">最近 50 条投递记录可查看</span>
          <button class="btn btn-ghost btn-sm" @click="router.push(`/settings/webhooks/${row.id}/deliveries`)">
            <PmmIcon name="history" :size="13" /> 出站日志
          </button>
          <button class="btn btn-ghost btn-sm" :disabled="testing.has(row.id)" @click="testSend(row)">
            <PmmIcon name="link" :size="13" /> {{ testing.has(row.id) ? '测试中…' : '测试发送' }}
          </button>
        </div>
      </div>

      <div v-if="!list.length && !loading" class="card empty-card">
        <PmmIcon name="webhook" :size="36" style="color: var(--text-dim); margin-bottom: 10px" />
        <div class="h2">尚未配置 Webhook</div>
        <div class="muted">归档成功 / 失败 / 待确认事件都可以订阅</div>
      </div>
    </div>

    <!-- 编辑对话框 -->
    <el-dialog v-model="dialogVisible" :title="editing ? '编辑订阅' : '新增订阅'" width="520px">
      <div class="vstack" style="--gap: 14px">
        <div class="field">
          <div class="field-label">名称</div>
          <input class="input" v-model="form.name" placeholder="便于识别的名字" />
        </div>
        <div class="field">
          <div class="field-label">URL</div>
          <input class="input font-mono" v-model="form.url" placeholder="https://hooks.example.com/pmm" autocomplete="off" />
        </div>
        <div class="field">
          <div class="field-label">事件（至少选 1 项）</div>
          <div class="event-checks">
            <label v-for="opt in EVENT_OPTIONS" :key="opt.value" class="event-check">
              <input type="checkbox" :value="opt.value" v-model="form.events" />
              <span class="event-check-label">{{ opt.label }}</span>
              <span class="event-check-code font-mono muted">{{ opt.value }}</span>
            </label>
          </div>
        </div>
        <div class="field">
          <div class="field-label">HMAC Secret</div>
          <input
            class="input font-mono"
            type="password"
            v-model="form.secret"
            :placeholder="editing ? '留空=不修改；填空字符串=清空；填值=替换' : '选填，用于签名验证'"
            autocomplete="new-password"
          />
        </div>
        <div class="field">
          <div class="field-label">超时（秒，1-60）</div>
          <input class="input" type="number" min="1" max="60" v-model.number="form.timeoutSeconds" />
        </div>
        <label class="hstack" style="gap: 8px; cursor: pointer">
          <div class="switch" :class="{ on: form.enabled }" @click="form.enabled = !form.enabled" />
          <span>启用</span>
        </label>
      </div>
      <template #footer>
        <button class="btn btn-ghost" @click="dialogVisible = false">取消</button>
        <button class="btn btn-primary" @click="save"><PmmIcon name="check" :size="14" /> 保存</button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped lang="scss">
.master-switch {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 16px;
  margin-bottom: 14px;
}
.ms-left { display: flex; align-items: center; gap: 12px; }
.ms-title { font-size: 13.5px; font-weight: 600; color: var(--text); }
.webhook-card { padding: 0; }
.wh-row {
  padding: 16px;
  display: flex;
  align-items: flex-start;
  gap: 14px;
}
.wh-icon {
  width: 40px;
  height: 40px;
  border-radius: 8px;
  background: var(--info-soft);
  color: var(--info);
  display: grid;
  place-items: center;
  flex-shrink: 0;
}
.wh-meta { flex: 1; min-width: 0; }
.wh-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
}
.wh-url {
  font-size: 13px;
  color: var(--text);
  word-break: break-all;
}
.wh-events {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  margin-top: 6px;
}
.wh-stats {
  display: flex;
  gap: 18px;
}
.stat { text-align: right; }
.stat .val {
  font-size: 18px;
  color: var(--text);
}
.stat .val.warn { color: var(--warning); }
.wh-tools {
  display: flex;
  align-items: center;
  gap: 4px;
}
.wh-actions {
  padding: 12px 16px;
  display: flex;
  gap: 8px;
  align-items: center;
}
.field { display: flex; flex-direction: column; gap: 6px; }
.field-label { font-size: 12.5px; font-weight: 600; color: var(--text); }
.event-checks {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 10px;
  border: 1px solid var(--border-soft);
  border-radius: 6px;
  background: var(--bg-elev);
}
.event-check {
  display: flex;
  align-items: center;
  gap: 10px;
  cursor: pointer;
  font-size: 12.5px;
}
.event-check input[type='checkbox'] { cursor: pointer; }
.event-check-label { color: var(--text); }
.event-check-code { font-size: 11px; margin-left: auto; }
.small { font-size: 11.5px; }
.xs { font-size: 10px; }
.empty-card {
  padding: 60px 20px;
  text-align: center;
}
.table-card { overflow: hidden; }
</style>
