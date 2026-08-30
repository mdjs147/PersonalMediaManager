<script setup>
import { onMounted, ref } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

const list = ref([]);
const loading = ref(false);
const dialogVisible = ref(false);
const editing = ref(null);
const form = ref({ extension: '', description: '', enabled: true });

async function load() {
  loading.value = true;
  try {
    const data = await api.mediaExtensions.list();
    list.value = data?.items || data || [];
  } finally {
    loading.value = false;
  }
}

function openAdd() {
  editing.value = null;
  form.value = { extension: '', description: '', enabled: true };
  dialogVisible.value = true;
}

function openEdit(row) {
  editing.value = row;
  form.value = { ...row };
  dialogVisible.value = true;
}

async function save() {
  if (!form.value.extension) {
    ElMessage.warning('请填写扩展名');
    return;
  }
  if (editing.value) {
    await api.mediaExtensions.update(editing.value.id, form.value);
    ElMessage.success('已更新');
  } else {
    await api.mediaExtensions.create(form.value);
    ElMessage.success('已添加');
  }
  dialogVisible.value = false;
  load();
}

async function toggle(row) {
  await api.mediaExtensions.update(row.id, { ...row, enabled: !row.enabled });
  load();
}

async function remove(row) {
  await ElMessageBox.confirm(`删除扩展名 "${row.extension}"？删除后该格式文件将不再被识别入队。`, '提示', { type: 'warning' });
  await api.mediaExtensions.delete(row.id);
  ElMessage.success('已删除');
  load();
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="设置"
      title="媒体格式"
      subtitle="仅列表中已启用的视频格式会被监控目录和全量扫描识别入队。变更即时生效，无需重启。"
    >
      <template #actions>
        <button class="btn btn-primary btn-sm" @click="openAdd">
          <PmmIcon name="plus" :size="14" /> 添加格式
        </button>
      </template>
    </PmmPageHeader>

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th style="width: 140px">扩展名</th>
            <th>说明</th>
            <th style="width: 80px">状态</th>
            <th style="width: 160px">操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in list" :key="row.id">
            <td>
              <span class="font-mono tag tag-info">{{ row.extension }}</span>
            </td>
            <td class="muted-text">{{ row.description || '—' }}</td>
            <td>
              <span v-if="row.enabled" class="tag tag-success"><span class="tag-dot" />启用</span>
              <span v-else class="tag tag-neutral">禁用</span>
            </td>
            <td>
              <div class="hstack" style="gap: 6px">
                <div class="switch" :class="{ on: row.enabled }" @click="toggle(row)" />
                <button class="icon-btn" @click="openEdit(row)"><PmmIcon name="edit" :size="15" /></button>
                <button class="icon-btn" @click="remove(row)"><PmmIcon name="trash" :size="15" /></button>
              </div>
            </td>
          </tr>
          <tr v-if="!list.length && !loading">
            <td colspan="4" class="empty">尚无媒体格式</td>
          </tr>
        </tbody>
      </table>
    </div>

    <el-dialog v-model="dialogVisible" :title="editing ? '编辑媒体格式' : '添加媒体格式'" width="480px">
      <div class="vstack" style="--gap: 14px">
        <div class="field">
          <div class="field-label">扩展名</div>
          <input
            class="input font-mono"
            v-model="form.extension"
            placeholder="含前导点，如 .mkv / .mp4"
          />
        </div>
        <div class="field">
          <div class="field-label">说明（可选）</div>
          <input class="input" v-model="form.description" placeholder="简短描述，方便后期维护" />
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
.field { display: flex; flex-direction: column; gap: 6px; }
.field-label { font-size: 12.5px; font-weight: 600; color: var(--text); }
.table-card { overflow: hidden; }
.muted-text { color: var(--text-muted); font-size: 13px; }
</style>
