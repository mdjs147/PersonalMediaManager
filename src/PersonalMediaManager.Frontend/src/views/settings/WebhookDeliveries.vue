<script setup>
import { computed, onMounted, ref } from 'vue';
import { ElMessage } from 'element-plus';
import { useRouter } from 'vue-router';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';
import { timeAgo, fmtClock } from '@/utils/format';

const props = defineProps({ id: { type: [String, Number], required: true } });
const router = useRouter();

const sub = ref(null);
const loading = ref(false);
const deliveries = ref([]);
const openId = ref(null);
const statusF = ref('all');
const eventF = ref('all');

// 与 Domain.Enums.WebhookDeliveryStatus 对齐：Pending / Retrying / Success / Failed
const STATUS_VARIANT = { Pending: 'neutral', Retrying: 'warning', Success: 'success', Failed: 'danger' };
const STATUS_LABEL = { Pending: '待发送', Retrying: '重试中', Success: '成功', Failed: '失败' };

async function load() {
  loading.value = true;
  try {
    const list = await api.webhooks.list();
    const items = list?.items || list || [];
    sub.value = items.find((s) => String(s.id) === String(props.id)) || items[0] || null;
    // 后端真实投递日志；空 items 直接显示空态（不再用前端 mock 兜底）
    const backend = await api.webhooks.listDeliveries(props.id, { skip: 0, take: 50 });
    const raw = backend?.items || [];
    deliveries.value = raw.map((d) => normalize(d));
    openId.value = deliveries.value[0]?.id ?? null;
  } finally {
    loading.value = false;
  }
}

// 后端 WebhookDeliveryResponse 真实字段（含义见 Domain.Entities.WebhookDelivery）：
// Id / SubscriptionId / Event / Status / Attempts / LastTriedAt / NextRetryAt /
// LastStatusCode / LastError / RequestId / CreatedAt / UpdatedAt
//
// 出于敏感 / 设计原因，后端**不暴露** Payload / Signature / ResponseBody / 每次尝试明细 / 媒体关联 —
// 前端不再展示这些字段（取代旧版的 mock 兜底）
function normalize(d) {
  return {
    id: d.id,
    subscriptionId: d.subscriptionId,
    event: d.event,
    status: d.status,
    statusCode: d.lastStatusCode ?? null,
    attempts: d.attempts ?? 0,
    lastTriedAt: d.lastTriedAt || null,
    nextRetryAt: d.nextRetryAt || null,
    lastError: d.lastError || null,
    requestId: d.requestId || '',
    createdAt: d.createdAt,
    updatedAt: d.updatedAt,
  };
}

const filtered = computed(() =>
  deliveries.value.filter((d) => {
    if (statusF.value !== 'all' && d.status !== statusF.value) return false;
    if (eventF.value !== 'all' && d.event !== eventF.value) return false;
    return true;
  }),
);

const open = computed(() => deliveries.value.find((d) => d.id === openId.value));

const testing = ref(false);

async function testSend() {
  if (!sub.value?.id || testing.value) return;
  testing.value = true;
  try {
    const data = await api.webhooks.test(sub.value.id);
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
    testing.value = false;
  }
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <div class="back-bar">
      <button class="btn btn-ghost btn-sm" @click="router.push('/settings/webhooks')">
        <PmmIcon name="chevronLeft" :size="14" /> 返回 Webhook 列表
      </button>
    </div>

    <!-- 头部 -->
    <div v-if="sub" class="card sub-head">
      <div class="sub-icon"><PmmIcon name="webhook" :size="22" /></div>
      <div class="sub-meta">
        <div class="eyebrow xs">Webhook 订阅</div>
        <div class="font-mono sub-url">{{ sub.url }}</div>
        <div class="sub-tags">
          <span v-for="ev in (sub.events || [])" :key="ev" class="tag tag-info">{{ ev }}</span>
          <span v-if="sub.enabled" class="tag tag-success"><span class="tag-dot" />启用</span>
          <span v-else class="tag tag-neutral">禁用</span>
        </div>
      </div>
      <div class="sub-actions">
        <button class="btn btn-ghost btn-sm" @click="router.push('/settings/webhooks')">
          <PmmIcon name="edit" :size="14" /> 编辑订阅
        </button>
        <button class="btn btn-ghost btn-sm" :disabled="testing" @click="testSend">
          <PmmIcon name="link" :size="14" /> {{ testing ? '测试中…' : '测试发送' }}
        </button>
      </div>
    </div>

    <!-- 顶部统计：仅展示后端真实有的两个计数（延迟数据未持久化，留待 OutboxWorker 落地后补） -->
    <div class="stats-row">
      <div class="card mini-stat">
        <div class="eyebrow xs">已投递</div>
        <div class="font-display tabular val" style="color: var(--success)">{{ (sub?.deliveredCount || 0).toLocaleString() }}</div>
      </div>
      <div class="card mini-stat">
        <div class="eyebrow xs">失败</div>
        <div class="font-display tabular val" :style="{ color: (sub?.failedCount || 0) > 0 ? 'var(--warning)' : 'var(--text-mute)' }">{{ sub?.failedCount || 0 }}</div>
      </div>
    </div>

    <!-- 双栏：列表 + 详情 -->
    <div class="dlv-row">
      <section class="card dlv-list-card">
        <header class="dlv-list-head">
          <h3 class="h3" style="margin-right: auto">投递列表</h3>
          <select class="select" v-model="statusF" style="width: auto; padding: 4px 8px; font-size: 11.5px">
            <option value="all">全部状态</option>
            <option value="Pending">待发送</option>
            <option value="Retrying">重试中</option>
            <option value="Success">成功</option>
            <option value="Failed">失败</option>
          </select>
          <select class="select" v-model="eventF" style="width: auto; padding: 4px 8px; font-size: 11.5px">
            <option value="all">全部事件</option>
            <option value="media.archived">archived</option>
            <option value="media.skipped">skipped</option>
            <option value="media.failed">failed</option>
            <option value="review.created">review</option>
            <option value="backup.failed">backup</option>
          </select>
        </header>
        <div class="dlv-list-body">
          <div
            v-for="d in filtered"
            :key="d.id"
            class="dlv-row-item"
            :class="{ active: d.id === openId }"
            @click="openId = d.id"
          >
            <div class="dlv-line1">
              <span class="tag" :class="`tag-${STATUS_VARIANT[d.status]}`"><span class="tag-dot" />{{ STATUS_LABEL[d.status] }}</span>
              <span class="tag tag-neutral">{{ d.event }}</span>
              <span class="muted xs" style="margin-left: auto">{{ timeAgo(d.lastTriedAt || d.createdAt) }}</span>
            </div>
            <div class="dlv-line2">
              <span class="cell-strong">#{{ d.id }}</span>
              <span v-if="d.attempts > 1" class="tag tag-warning">尝试 {{ d.attempts }} 次</span>
              <span class="muted tabular small dlv-http">HTTP {{ d.statusCode ?? '—' }}</span>
            </div>
          </div>
          <div v-if="!filtered.length" class="empty" style="padding: 30px">暂无投递记录</div>
        </div>
      </section>

      <section class="card dlv-detail-card">
        <div v-if="!open" class="empty" style="padding: 60px">选择左侧投递记录查看详情</div>
        <template v-else>
          <header class="detail-head">
            <div class="detail-title-row">
              <h3 class="h3">投递 #{{ open.id }}</h3>
              <span class="tag" :class="`tag-${STATUS_VARIANT[open.status]}`"><span class="tag-dot" />{{ STATUS_LABEL[open.status] }}</span>
              <span class="tag tag-info">{{ open.event }}</span>
            </div>
          </header>

          <div class="dt-content">
            <div class="kv-grid">
              <div class="kv-cell">
                <div class="eyebrow xs">订阅 ID</div>
                <div class="font-mono">{{ open.subscriptionId }}</div>
              </div>
              <div class="kv-cell">
                <div class="eyebrow xs">尝试次数</div>
                <div class="font-mono tabular">{{ open.attempts }}</div>
              </div>
              <div class="kv-cell">
                <div class="eyebrow xs">最后 HTTP 状态码</div>
                <div class="font-mono tabular">{{ open.statusCode ?? '—' }}</div>
              </div>
              <div class="kv-cell">
                <div class="eyebrow xs">最后尝试时间</div>
                <div class="font-mono">{{ open.lastTriedAt ? fmtClock(open.lastTriedAt) : '—' }}</div>
              </div>
              <div class="kv-cell">
                <div class="eyebrow xs">下次重试时间</div>
                <div class="font-mono">{{ open.nextRetryAt ? fmtClock(open.nextRetryAt) : '—' }}</div>
              </div>
              <div class="kv-cell">
                <div class="eyebrow xs">创建时间</div>
                <div class="font-mono">{{ fmtClock(open.createdAt) }}</div>
              </div>
              <div class="kv-cell kv-wide">
                <div class="eyebrow xs">RequestId（跨日志追踪）</div>
                <div class="font-mono small" style="word-break: break-all">{{ open.requestId || '—' }}</div>
              </div>
            </div>

            <div v-if="open.lastError" class="card error-card">
              <div class="eyebrow xs" style="margin-bottom: 6px; color: var(--danger)">最后错误</div>
              <pre class="font-mono small" style="margin: 0; white-space: pre-wrap; word-break: break-all">{{ open.lastError }}</pre>
            </div>

            <div class="muted small" style="padding: 12px 18px; line-height: 1.6">
              出于设计原因，Payload / Signature / Response Body 不在出站日志中持久化（避免敏感数据落库），
              如需排错可按 RequestId 查后端运行日志。
            </div>
          </div>
        </template>
      </section>
    </div>
  </div>
</template>

<style scoped lang="scss">
.back-bar { margin-bottom: 14px; }

.sub-head { padding: 20px; margin-bottom: 18px; display: flex; gap: 16px; align-items: flex-start; }
.sub-icon {
  width: 44px; height: 44px; border-radius: 10px;
  background: var(--info-soft); color: var(--info);
  display: grid; place-items: center; flex-shrink: 0;
}
.sub-meta { flex: 1; min-width: 0; }
.sub-url { font-size: 15px; font-weight: 600; margin: 4px 0 6px; word-break: break-all; }
.sub-tags { display: flex; gap: 6px; flex-wrap: wrap; }
.sub-actions { display: flex; gap: 8px; }

.stats-row {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 14px;
  margin-bottom: 18px;
  max-width: 480px;
}
.mini-stat { padding: 14px; }
.mini-stat .val { font-size: 22px; margin-top: 4px; }

.dlv-row { display: grid; grid-template-columns: 1fr 1.3fr; gap: 16px; }
.dlv-list-card, .dlv-detail-card { display: flex; flex-direction: column; max-height: 700px; overflow: hidden; }
.dlv-list-head {
  padding: 12px 14px;
  border-bottom: 1px solid var(--border-soft);
  display: flex; gap: 8px; align-items: center;
}
.dlv-list-body { flex: 1; overflow-y: auto; }
.dlv-row-item {
  padding: 12px 14px;
  border-bottom: 1px solid var(--border-soft);
  cursor: pointer;
  border-left: 3px solid transparent;
}
.dlv-row-item.active {
  background: var(--accent-soft);
  border-left-color: var(--accent);
}
.dlv-line1 { display: flex; align-items: center; gap: 8px; margin-bottom: 4px; }
.dlv-line2 { display: flex; align-items: center; gap: 8px; font-size: 12px; flex-wrap: wrap; }
.dlv-http { margin-left: auto; }

.detail-head {
  padding: 14px 18px;
  border-bottom: 1px solid var(--border-soft);
}
.detail-title-row { display: flex; align-items: center; gap: 10px; margin-bottom: 8px; flex-wrap: wrap; }

.dt-content { flex: 1; overflow: auto; padding: 14px 18px; }
.kv-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px 18px;
  margin-bottom: 16px;
}
.kv-cell { padding: 6px 0; }
.kv-cell .font-mono { font-size: 12px; font-weight: 600; margin-top: 3px; }
.kv-cell.kv-wide { grid-column: 1 / -1; }

.error-card {
  padding: 12px 14px;
  background: var(--danger-soft);
  border-color: color-mix(in oklab, var(--danger) 30%, transparent);
  margin-bottom: 12px;
}

.small { font-size: 11.5px; }
.xs { font-size: 10.5px; }
</style>
