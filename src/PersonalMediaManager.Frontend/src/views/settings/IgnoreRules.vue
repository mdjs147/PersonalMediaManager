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
// 与后端 WatchIgnoreRuleResponse 字段对齐：type（Extension|Keyword） / pattern / enabled / description
const form = ref({ pattern: '', type: 'Extension', enabled: true, description: '' });

async function load() {
  loading.value = true;
  try {
    const data = await api.watchIgnoreRules.list();
    list.value = data?.items || data || [];
  } finally {
    loading.value = false;
  }
}

function openAdd() {
  editing.value = null;
  form.value = { pattern: '', type: 'Extension', enabled: true, description: '' };
  dialogVisible.value = true;
}

function openEdit(row) {
  editing.value = row;
  form.value = { ...row };
  dialogVisible.value = true;
}

async function save() {
  if (!form.value.pattern) {
    ElMessage.warning('请填写匹配模式');
    return;
  }
  if (editing.value) {
    await api.watchIgnoreRules.update(editing.value.id, form.value);
    ElMessage.success('已更新');
  } else {
    await api.watchIgnoreRules.create(form.value);
    ElMessage.success('已添加');
  }
  dialogVisible.value = false;
  load();
}

async function toggle(row) {
  await api.watchIgnoreRules.update(row.id, { ...row, enabled: !row.enabled });
  load();
}

async function remove(row) {
  await ElMessageBox.confirm(`删除规则 "${row.pattern}"？`, '提示', { type: 'warning' });
  await api.watchIgnoreRules.delete(row.id);
  ElMessage.success('已删除');
  load();
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="设置"
      title="忽略规则"
      subtitle="命中下列规则的文件不会进入处理队列。建议添加：临时文件、字幕、缩略图等。"
    >
      <template #actions>
        <button class="btn btn-primary btn-sm" @click="openAdd">
          <PmmIcon name="plus" :size="14" /> 添加规则
        </button>
      </template>
    </PmmPageHeader>

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>类型</th>
            <th>匹配模式</th>
            <th>状态</th>
            <th style="width: 180px">操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in list" :key="row.id">
            <td>
              <span class="tag" :class="row.type === 'Extension' ? 'tag-info' : 'tag-neutral'">
                {{ row.type }}
              </span>
            </td>
            <td>
              <span class="font-mono">{{ row.pattern }}</span>
            </td>
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
            <td colspan="4" class="empty">尚无忽略规则</td>
          </tr>
        </tbody>
      </table>
    </div>

    <el-dialog v-model="dialogVisible" :title="editing ? '编辑忽略规则' : '添加忽略规则'" width="520px">
      <div class="vstack" style="--gap: 14px">
        <div class="field">
          <div class="field-label">匹配模式</div>
          <input class="input font-mono" v-model="form.pattern" placeholder="Extension：.part / .tmp；Keyword：sample" />
        </div>
        <div class="field">
          <div class="field-label">匹配类型</div>
          <select class="select" v-model="form.type">
            <option value="Extension">Extension（按扩展名，含点如 .part）</option>
            <option value="Keyword">Keyword（按关键词子串）</option>
          </select>
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
</style>
