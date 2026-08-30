<script setup>
import { computed, onMounted, ref } from 'vue';
import { ElMessage, ElMessageBox, ElDialog } from 'element-plus';
import { api } from '@/api';
import { useScanActions } from '@/composables/useScanActions';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

const list = ref([]);
const loading = ref(false);
const dialogVisible = ref(false);
const editing = ref(null);

// rowVersion 从 list 返回的 WatchFolderResponse 拿到；编辑保存时回传，服务端比对版本号实现乐观锁
const emptyForm = () => ({
  path: '',
  alias: '',
  isTransit: false,
  isNetworkShare: false,
  enabled: true,
  autoScan: true,
  priority: 100,
  rowVersion: 0,
});
const form = ref(emptyForm());

function folderTypeTag(row) {
  if (row.isNetworkShare) return { label: '网络盘', cls: 'tag-warning' };
  if (row.isTransit) return { label: '中转盘', cls: 'tag-info' };
  return { label: '本地', cls: 'tag-neutral' };
}

async function load() {
  loading.value = true;
  try {
    // api.watchFolders.list() 后端返回直接数组（无 Items 包装）；http.js 拦截器已 return body.data
    list.value = (await api.watchFolders.list()) || [];
  } finally {
    loading.value = false;
  }
}

function openAdd() {
  editing.value = null;
  form.value = emptyForm();
  dialogVisible.value = true;
}

function openEdit(row) {
  editing.value = row;
  form.value = {
    path: row.path,
    alias: row.alias ?? '',
    isTransit: !!row.isTransit,
    isNetworkShare: !!row.isNetworkShare,
    enabled: !!row.enabled,
    autoScan: row.autoScan !== false, // 兼容旧数据缺字段时默认开启
    priority: row.priority ?? 100,
    rowVersion: row.rowVersion ?? 0,
  };
  dialogVisible.value = true;
}

async function save() {
  if (!form.value.path) {
    ElMessage.warning('请填写目录路径');
    return;
  }
  const payload = {
    path: form.value.path.trim(),
    alias: form.value.alias?.trim() || null,
    isTransit: form.value.isTransit,
    isNetworkShare: form.value.isNetworkShare,
    enabled: form.value.enabled,
    autoScan: form.value.autoScan,
    priority: Number(form.value.priority) || 100,
  };
  if (editing.value) {
    await api.watchFolders.update(editing.value.id, { ...payload, rowVersion: form.value.rowVersion });
    ElMessage.success('已更新');
  } else {
    await api.watchFolders.create(payload);
    ElMessage.success('已添加');
  }
  dialogVisible.value = false;
  load();
}

// 整行回传所有字段：Update DTO 各字段必填，漏传布尔会被后端按 false 反序列化（误关自动监控等）
async function toggleEnabled(row) {
  await api.watchFolders.update(row.id, {
    path: row.path,
    alias: row.alias,
    isTransit: row.isTransit,
    isNetworkShare: row.isNetworkShare,
    enabled: !row.enabled,
    autoScan: row.autoScan !== false,
    priority: row.priority,
    rowVersion: row.rowVersion ?? 0,
  });
  load();
}

// 行内快捷切换自动监控（无需打开编辑弹窗）
async function toggleAutoScan(row) {
  await api.watchFolders.update(row.id, {
    path: row.path,
    alias: row.alias,
    isTransit: row.isTransit,
    isNetworkShare: row.isNetworkShare,
    enabled: row.enabled,
    autoScan: !(row.autoScan !== false),
    priority: row.priority,
    rowVersion: row.rowVersion ?? 0,
  });
  load();
}

async function remove(row) {
  await ElMessageBox.confirm(`确认移除监控目录 "${row.path}"？`, '提示', { type: 'warning' });
  await api.watchFolders.delete(row.id);
  ElMessage.success('已移除');
  load();
}

async function testReach(row) {
  const data = await api.watchFolders.test(row.id);
  if (data?.reachable) {
    ElMessage.success(`可达：${data.sampleEntry ?? '(目录为空)'}`);
  } else {
    ElMessage.warning(data?.errorMessage || '目录不可达');
  }
}

// 扫描动作（全量 / 强制全扫 / 单目录）抽到 useScanActions 共享，与仪表盘复用同一套逻辑与文案
const { scanning, scanAll, scanForceAll, scanOne } = useScanActions();

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="设置"
      title="监控目录"
      subtitle="开启「自动监控」的目录会被实时监听并周期自动扫描；关闭后该目录仅在手动点击扫描时才处理。"
    >
      <template #actions>
        <button class="btn btn-sm" :disabled="scanning" @click="scanAll">
          <PmmIcon name="refresh" :size="14" /> {{ scanning ? '扫描中…' : '立即扫描全部' }}
        </button>
        <button class="btn btn-danger btn-sm" :disabled="scanning" title="重新枚举全部目录 + 自动重投 Failed 历史记录" @click="scanForceAll">
          <PmmIcon name="power" :size="14" /> 强制全扫
        </button>
        <button class="btn btn-primary btn-sm" @click="openAdd">
          <PmmIcon name="plus" :size="14" /> 添加目录
        </button>
      </template>
    </PmmPageHeader>

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>路径 / 别名</th>
            <th>类型</th>
            <th>优先级</th>
            <th>状态</th>
            <th style="width: 220px">操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in list" :key="row.id">
            <td>
              <div class="hstack">
                <PmmIcon name="folder" :size="15" :style="{ color: row.enabled ? 'var(--accent)' : 'var(--text-dim)' }" />
                <div class="vstack" style="--gap: 2px; gap: 2px; min-width: 0;">
                  <span class="font-mono" style="font-size: 12.5px">{{ row.path }}</span>
                  <span v-if="row.alias" class="muted small">{{ row.alias }}</span>
                </div>
              </div>
            </td>
            <td>
              <span class="tag" :class="folderTypeTag(row).cls">{{ folderTypeTag(row).label }}</span>
            </td>
            <td>
              <span class="font-mono">{{ row.priority }}</span>
            </td>
            <td>
              <span v-if="!row.enabled" class="tag tag-neutral">已暂停</span>
              <label
                v-else
                class="hstack"
                style="gap: 8px; cursor: pointer"
                :title="row.autoScan !== false
                  ? '自动监控开启：实时监听 + 周期自动扫描。点击切换为仅手动'
                  : '仅手动：不自动扫描，仅在手动点击扫描时处理。点击开启自动监控'"
              >
                <div class="switch" :class="{ on: row.autoScan !== false }" @click="toggleAutoScan(row)" />
                <span v-if="row.autoScan !== false" class="tag tag-success"><span class="tag-dot" />监控中</span>
                <span v-else class="tag tag-info">仅手动</span>
              </label>
            </td>
            <td>
              <div class="hstack" style="gap: 6px">
                <div class="switch" :class="{ on: row.enabled }" :title="row.enabled ? '已启用，点击暂停该目录' : '已暂停，点击启用该目录'" @click="toggleEnabled(row)" />
                <button class="icon-btn" :disabled="scanning || !row.enabled" title="立即扫描此目录（含仅手动目录）" @click="scanOne(row)"><PmmIcon name="play" :size="15" /></button>
                <button class="icon-btn" title="测试连通性" @click="testReach(row)"><PmmIcon name="link" :size="15" /></button>
                <button class="icon-btn" title="编辑" @click="openEdit(row)"><PmmIcon name="edit" :size="15" /></button>
                <button class="icon-btn" title="删除" @click="remove(row)"><PmmIcon name="trash" :size="15" /></button>
              </div>
            </td>
          </tr>
          <tr v-if="!list.length && !loading">
            <td colspan="5" class="empty">尚未配置监控目录</td>
          </tr>
        </tbody>
      </table>
    </div>

    <el-dialog v-model="dialogVisible" :title="editing ? '编辑监控目录' : '添加监控目录'" width="540px">
      <div class="vstack" style="--gap: 14px">
        <div class="field">
          <div class="field-label">目录路径</div>
          <input class="input font-mono" v-model="form.path" placeholder="例如：D:\Downloads\BT 或 /mnt/data" />
        </div>
        <div class="field">
          <div class="field-label">别名（可选）</div>
          <input class="input" v-model="form.alias" placeholder="如：下载盘、NAS-影视" />
        </div>
        <div class="field">
          <div class="field-label">优先级（越小越先扫描，范围 0–9999）</div>
          <input class="input font-mono" type="number" min="0" max="9999" v-model.number="form.priority" />
        </div>
        <div class="field">
          <label class="hstack" style="gap: 8px; cursor: pointer; align-items: center">
            <div class="switch" :class="{ on: form.autoScan }" @click="form.autoScan = !form.autoScan" />
            <span class="field-label" style="margin: 0">自动监控</span>
          </label>
          <div class="muted small">
            开启（推荐）：实时监听该目录新增文件并周期自动扫描。关闭：不自动扫描，仅在手动点击「扫描」时才处理本目录。
          </div>
        </div>
        <div class="hstack" style="gap: 14px; flex-wrap: wrap">
          <label class="hstack" style="gap: 8px; cursor: pointer">
            <div class="switch" :class="{ on: form.isTransit }" @click="form.isTransit = !form.isTransit" />
            <span>中转盘</span>
          </label>
          <label class="hstack" style="gap: 8px; cursor: pointer">
            <div class="switch" :class="{ on: form.isNetworkShare }" @click="form.isNetworkShare = !form.isNetworkShare" />
            <span>网络盘</span>
          </label>
          <label class="hstack" style="gap: 8px; cursor: pointer">
            <div class="switch" :class="{ on: form.enabled }" @click="form.enabled = !form.enabled" />
            <span>启用</span>
          </label>
        </div>
      </div>
      <template #footer>
        <button class="btn btn-ghost" @click="dialogVisible = false">取消</button>
        <button class="btn btn-primary" @click="save"><PmmIcon name="check" :size="14" /> 保存</button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped lang="scss">
.field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.field-label {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text);
}
.small { font-size: 11.5px; }
.table-card { overflow: hidden; }
</style>
