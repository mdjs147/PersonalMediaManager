// @ts-check
import { ref } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';
import { api } from '@/api';

/**
 * 监控目录扫描动作的共享封装：立即扫描全部 / 强制全扫 / 扫描单个目录。
 *
 * @remarks
 * 三类扫描共享一个 in-flight 标识 `scanning`，避免并发触发：后端有全局锁会抛「已有扫描在进行中」，
 * 前端这层只是提前抑制 UI 重复点击，体验更顺滑。可选 `onScanned` 回调在每次成功触发后调用
 * （如仪表盘扫完刷新队列数字）；其异常被吞掉，不影响「扫描已成功触发」的结果提示。
 *
 * @param {() => (void | Promise<void>)} [onScanned] 每次扫描成功触发后的回调（可选）
 */
export function useScanActions(onScanned) {
  const scanning = ref(false);

  async function notifyScanned() {
    if (typeof onScanned !== 'function') return;
    try {
      await onScanned();
    } catch {
      /* 刷新失败不影响扫描已成功触发的事实 */
    }
  }

  // 立即扫描全部：枚举所有启用目录的新增文件入队
  async function scanAll() {
    if (scanning.value) return;
    scanning.value = true;
    try {
      const data = await api.scan.trigger({ force: false });
      ElMessage.success(`已触发全量扫描：入队 ${data?.filesEnqueued ?? 0} 个文件（覆盖 ${data?.folderCount ?? 0} 个目录）`);
      await notifyScanned();
    } finally {
      scanning.value = false;
    }
  }

  // 强制全扫：重新枚举所有目录 + 自动重投 Failed 历史记录（带二次确认）
  async function scanForceAll() {
    if (scanning.value) return;
    try {
      await ElMessageBox.confirm(
        '「强制全扫」会重新枚举所有监控目录，并把状态为 Failed 的历史记录自动重投处理；正在处理中（Queued / Processing）与已成功 / 已归档的记录仍会跳过，不会被覆盖。\n\n确认要触发吗？',
        '强制重新全扫',
        { type: 'warning', confirmButtonText: '强制全扫', cancelButtonText: '取消' },
      );
    } catch {
      return; // 用户取消
    }
    scanning.value = true;
    try {
      const data = await api.scan.trigger({ force: true });
      ElMessage.success(
        `已触发强制全扫：入队 ${data?.filesEnqueued ?? 0} 个文件（其中重投 Failed ${data?.failedRescanned ?? 0} 条，覆盖 ${data?.folderCount ?? 0} 个目录）`,
      );
      await notifyScanned();
    } finally {
      scanning.value = false;
    }
  }

  // 扫描单个目录（含仅手动目录）：暂停的目录直接拒绝
  async function scanOne(row) {
    if (scanning.value) return;
    if (!row.enabled) {
      ElMessage.warning('目录已暂停，请先启用再扫描');
      return;
    }
    scanning.value = true;
    try {
      const data = await api.scan.folder(row.id);
      ElMessage.success(`已触发目录扫描：入队 ${data?.filesEnqueued ?? 0} 个文件（${row.alias || row.path}）`);
      await notifyScanned();
    } finally {
      scanning.value = false;
    }
  }

  return { scanning, scanAll, scanForceAll, scanOne };
}
