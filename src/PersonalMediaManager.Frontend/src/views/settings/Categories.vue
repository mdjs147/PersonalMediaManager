<script setup>
import { onMounted, ref } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';
import { useRouter } from 'vue-router';
import { api } from '@/api';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

const router = useRouter();

const list = ref([]);
const rules = ref([]);
const loading = ref(false);
const dialogVisible = ref(false);
const editing = ref(null);
// 与后端 CategoryDefinitionRequest 字段对齐：
// { name, mediaType(Movie/Tv/Both), targetRoot, priority, description }
const form = ref({ name: '', mediaType: 'Movie', targetRoot: '', priority: 100, description: '' });

async function load() {
  loading.value = true;
  try {
    const [c, r] = await Promise.all([api.categories.list(), api.categoryMatchRules.list()]);
    list.value = c?.items || c || [];
    rules.value = r?.items || r || [];
  } finally {
    loading.value = false;
  }
}

function openAdd() {
  editing.value = null;
  form.value = { name: '', mediaType: 'Movie', targetRoot: '', priority: 100, description: '' };
  dialogVisible.value = true;
}

function openEdit(row) {
  editing.value = row;
  form.value = {
    name: row.name,
    mediaType: row.mediaType,
    targetRoot: row.targetRoot,
    priority: row.priority ?? 100,
    description: row.description ?? '',
    rowVersion: row.rowVersion ?? 0, // 乐观并发令牌，保存时回传后端
  };
  dialogVisible.value = true;
}

async function save() {
  if (!form.value.name || !form.value.targetRoot) {
    ElMessage.warning('名称与目标根目录必填');
    return;
  }
  if (editing.value) {
    await api.categories.update(editing.value.id, form.value);
    ElMessage.success('已更新');
  } else {
    await api.categories.create(form.value);
    ElMessage.success('已添加');
  }
  dialogVisible.value = false;
  load();
}

async function remove(row) {
  await ElMessageBox.confirm(`删除分类 "${row.name}"？已归档文件不会受影响。`, '提示', { type: 'warning' });
  await api.categories.delete(row.id);
  ElMessage.success('已删除');
  load();
}

// 关联匹配规则通过 categoryId 计数（rule.categoryId === c.id）
function ruleCountOf(id) {
  return rules.value.filter((r) => r.categoryId === id).length;
}

const MEDIA_TYPE_LABEL = {
  Movie: '电影',
  Tv: '剧集',
  Both: '电影 + 剧集',
};

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="设置"
      title="媒体分类"
      subtitle="为每种媒体类型定义目标根目录与自动匹配条件。未命中规则的文件由 AI 兜底建议分类。"
    >
      <template #actions>
        <button class="btn btn-primary btn-sm" @click="openAdd">
          <PmmIcon name="plus" :size="14" /> 新增分类
        </button>
      </template>
    </PmmPageHeader>

    <div class="cat-grid">
      <div v-for="c in list" :key="c.id" class="card cat-card">
        <div class="cat-head">
          <div>
            <div class="h3">{{ c.name }}</div>
            <div class="muted small">{{ MEDIA_TYPE_LABEL[c.mediaType] || c.mediaType }} · 优先级 {{ c.priority ?? '—' }}</div>
          </div>
          <span class="tag tag-accent">{{ MEDIA_TYPE_LABEL[c.mediaType] || c.mediaType }}</span>
        </div>
        <div class="path" style="display: block; margin: 12px 0">{{ c.targetRoot || '—' }}</div>
        <div v-if="c.description" class="muted small" style="margin-bottom: 8px">{{ c.description }}</div>
        <div class="cat-rules">
          <PmmIcon name="rule" :size="12" /> {{ ruleCountOf(c.id) }} 条匹配规则
        </div>
        <div class="cat-actions">
          <button class="btn btn-ghost btn-sm" @click="openEdit(c)">
            <PmmIcon name="edit" :size="13" /> 编辑
          </button>
          <button class="btn btn-ghost btn-sm" @click="router.push(`/settings/categories/${c.id}/rules`)">
            <PmmIcon name="rule" :size="13" /> 规则
          </button>
          <button class="btn btn-ghost btn-sm" style="margin-left: auto; color: var(--danger)" @click="remove(c)">
            <PmmIcon name="trash" :size="13" /> 删除
          </button>
        </div>
      </div>

      <div v-if="!list.length && !loading" class="card empty-card">
        <PmmIcon name="layers" :size="36" style="color: var(--text-dim); margin-bottom: 10px" />
        <div class="h2">未配置任何媒体分类</div>
        <div class="muted">点击右上「新增分类」开始</div>
      </div>
    </div>

    <el-dialog v-model="dialogVisible" :title="editing ? '编辑分类' : '新增分类'" width="520px">
      <div class="vstack" style="--gap: 14px">
        <div class="field">
          <div class="field-label">名称</div>
          <input class="input" v-model="form.name" placeholder="如 电影 / 电视剧 / 动漫" />
        </div>
        <div class="field">
          <div class="field-label">媒体类型</div>
          <div class="hstack" style="gap: 16px">
            <label class="hstack" style="gap: 6px; cursor: pointer">
              <input type="radio" v-model="form.mediaType" value="Movie" /> 电影
            </label>
            <label class="hstack" style="gap: 6px; cursor: pointer">
              <input type="radio" v-model="form.mediaType" value="Tv" /> 剧集
            </label>
            <label class="hstack" style="gap: 6px; cursor: pointer">
              <input type="radio" v-model="form.mediaType" value="Both" /> 两者皆可
            </label>
          </div>
        </div>
        <div class="field">
          <div class="field-label">目标根目录</div>
          <input class="input font-mono" v-model="form.targetRoot" placeholder="如 D:\Media\Movies 或 /mnt/media/tv" />
        </div>
        <div class="field">
          <div class="field-label">优先级（越小越先匹配）</div>
          <input class="input tabular" type="number" v-model.number="form.priority" style="width: 140px" />
        </div>
        <div class="field">
          <div class="field-label">描述（可选）</div>
          <input class="input" v-model="form.description" placeholder="简短说明分类用途" />
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
.cat-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 16px;
}
.cat-card {
  padding: 18px;
  display: flex;
  flex-direction: column;
  gap: 0;
}
.cat-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  margin-bottom: 8px;
}
.cat-rules {
  font-size: 12px;
  color: var(--text-mute);
  margin-bottom: 14px;
  display: inline-flex;
  align-items: center;
  gap: 4px;
}
.cat-actions {
  display: flex;
  gap: 6px;
  align-items: center;
}
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
.empty-card {
  padding: 60px 20px;
  text-align: center;
  grid-column: 1 / -1;
}
</style>
