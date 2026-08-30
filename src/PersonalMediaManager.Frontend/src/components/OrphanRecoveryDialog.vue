<script setup>
/**
 * 孤儿文件找回（OrphanRecoveryDialog）
 *
 * 扫描各分类归档根，列出「磁盘有、DB 无记录」的媒体文件（典型：删了记录但文件留在库里）。
 * 多选后「认领」= 重新投入处理管线重新登记入库；文件已在规范位的由归档层识别「已就位」直接登记、不搬动。
 */
import { ref, watch } from 'vue';
import { ElMessage } from 'element-plus';
import { api } from '@/api';
import { fmtSize } from '@/utils/format';
import PmmIcon from '@/components/PmmIcon.vue';

const props = defineProps({ modelValue: { type: Boolean, default: false } });
const emit = defineEmits(['update:modelValue', 'claimed']);

const loading = ref(false);
const claiming = ref(false);
const scanned = ref(false);
const orphans = ref([]);
const selected = ref([]);

async function scan() {
  loading.value = true;
  selected.value = [];
  try {
    const r = await api.library.scanOrphans();
    orphans.value = r?.items || [];
    scanned.value = true;
  } finally {
    loading.value = false;
  }
}

function onSelectionChange(rows) {
  selected.value = rows;
}

async function claim() {
  if (!selected.value.length) return;
  claiming.value = true;
  try {
    const paths = selected.value.map((o) => o.path);
    const r = await api.library.claimOrphans({ paths });
    ElMessage.success(`已入队认领 ${r.admitted} 个${r.skipped ? `，跳过 ${r.skipped} 个（文件不存在或已在处理）` : ''}，可在「处理队列」查看进度`);
    emit('claimed');
    await scan(); // 重新扫描刷新（已认领的下次不再是孤儿）
  } finally {
    claiming.value = false;
  }
}

// 打开时自动扫描一次
watch(
  () => props.modelValue,
  (v) => {
    if (v) {
      scanned.value = false;
      orphans.value = [];
      selected.value = [];
      scan();
    }
  },
);
</script>

<template>
  <el-dialog
    :model-value="modelValue"
    title="孤儿文件找回"
    width="760px"
    @update:model-value="(v) => emit('update:modelValue', v)"
  >
    <div class="orphan-intro muted small">
      列出各分类归档目录里「磁盘有文件、但已无处理记录」的孤儿（典型：删了记录但文件留在库里）。
      选中后「认领」会重新登记入库——文件已在规范位的不会移动。
    </div>

    <div v-loading="loading" class="orphan-body">
      <el-table
        v-if="orphans.length"
        :data="orphans"
        height="380"
        size="small"
        @selection-change="onSelectionChange"
      >
        <el-table-column type="selection" width="42" />
        <el-table-column label="文件" min-width="220">
          <template #default="{ row }">
            <div class="of-name">{{ row.fileName }}</div>
            <div class="of-path font-mono">{{ row.path }}</div>
          </template>
        </el-table-column>
        <el-table-column label="分类" width="90" prop="categoryName" />
        <el-table-column label="大小" width="92">
          <template #default="{ row }">{{ fmtSize(row.size) }}</template>
        </el-table-column>
        <el-table-column label="TMDB" width="80">
          <template #default="{ row }">
            <span v-if="row.tmdbId" class="tag tag-accent">{{ row.tmdbId }}</span>
            <span v-else class="muted small">—</span>
          </template>
        </el-table-column>
      </el-table>

      <div v-else-if="scanned && !loading" class="orphan-empty">
        <PmmIcon name="check" :size="28" />
        <p>未发现孤儿文件，归档库与记录一致。</p>
      </div>
    </div>

    <template #footer>
      <div class="orphan-footer">
        <span v-if="orphans.length" class="muted small">共 {{ orphans.length }} 个孤儿 · 已选 {{ selected.length }}</span>
        <div class="spacer" />
        <button class="btn btn-sm" :disabled="loading" @click="scan">
          <PmmIcon name="refresh" :size="14" /> 重新扫描
        </button>
        <button class="btn btn-primary btn-sm" :disabled="!selected.length || claiming" @click="claim">
          认领选中（{{ selected.length }}）
        </button>
      </div>
    </template>
  </el-dialog>
</template>

<style scoped lang="scss">
.orphan-intro { margin-bottom: 12px; line-height: 1.6; }
.orphan-body { min-height: 200px; }
.of-name { font-size: 13px; color: var(--text); }
.of-path { font-size: 11px; color: var(--text-dim); margin-top: 2px; word-break: break-all; }
.orphan-empty {
  display: flex; flex-direction: column; align-items: center; justify-content: center;
  gap: 8px; padding: 48px 0; color: var(--text-mute);
}
.orphan-footer { display: flex; align-items: center; gap: 10px; width: 100%; }
.orphan-footer .spacer { flex: 1; }
</style>
