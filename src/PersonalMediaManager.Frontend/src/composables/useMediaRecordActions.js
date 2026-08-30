// @ts-check
import { ElMessage, ElMessageBox } from 'element-plus';
import { api } from '@/api';

/**
 * 媒体记录管理操作（重试 / 重新处理 / 删除 / 整剧 / 批量）公共逻辑。
 *
 * 「作品详情页」与「处理历史页」共用同一套写操作（均打到后端 /history/* 端点），
 * 抽到此 composable 消除重复：History 用子集（rescan / del / rescanFailed），
 * LibraryDetail 用全集（含 reprocess / 整剧 / 批量）。
 *
 * 约定：reprocess / delete 系列接口返回 { succeeded:[], failed:[{id,message}] }，
 * 部分失败也算 HTTP 200，按明细分级汇报。
 *
 * @param {{
 *   reload: () => (void | Promise<void>),
 *   onSeriesGone?: (reason: 'deleted' | 'reprocessed') => (void | Promise<void>),
 * }} opts
 *   reload 在操作成功后刷新当前视图；
 *   onSeriesGone（可选）整剧记录被「删除/重投」后该作品已从媒体库消失（终态记录清零），
 *   此时 reload 当前详情页会拿到「作品不存在或无记录」而停在陈旧数据 —— 提供此回调改为离开详情页：
 *   reason='deleted'（删整剧）应回媒体库列表，reason='reprocessed'（重处理整剧）应去处理队列。
 *   详情页传入，历史页不传（留在列表 reload 即可）。
 */
export function useMediaRecordActions({ reload, onSeriesGone }) {
  const REPROCESS_HINT = '可在「处理队列」查看进度';
  const titleOf = (row) => row.title || row.fileName || `#${row.id}`;

  /**
   * 统一：确认 → 调接口 → 按 succeeded/failed 汇报 → 收尾；用户取消确认时静默返回。
   * onDone（可选）覆盖默认的 reload 收尾——整剧操作后当前详情页已无记录可显示，改为离开页面。
   */
  async function runAction({ apiFn, payload, title, body, confirmBtn, danger = false, successHint = '', onDone }) {
    try {
      await ElMessageBox.confirm(body, title, {
        type: 'warning',
        confirmButtonText: confirmBtn,
        cancelButtonText: '取消',
        confirmButtonClass: danger ? 'el-button--danger' : '',
      });
    } catch {
      return; // 取消
    }
    const r = await apiFn(payload);
    const ok = r?.succeeded?.length || 0;
    const fail = r?.failed?.length || 0;
    if (!fail) {
      ElMessage.success(`${confirmBtn}成功 ${ok} 条${successHint ? '，' + successHint : ''}`);
    } else if (!ok) {
      ElMessage.error(`${confirmBtn}失败：${r.failed[0]?.message || '未知错误'}${fail > 1 ? ` 等 ${fail} 条` : ''}`);
    } else {
      ElMessage.warning(`成功 ${ok} 条，失败 ${fail} 条：${r.failed[0]?.message || ''}`);
    }
    // 整剧操作至少删/投成功 1 条时走离场收尾（全失败则记录仍在，留在原页让用户排查）
    if (onDone && ok > 0) await onDone();
    else await reload();
  }

  /** 单条重试（保留记录，仅 Failed 可用）：走 /history/{id}/rescan */
  async function rescan(row) {
    await api.history.rescan(row.id);
    ElMessage.success('已重新入队');
    await reload();
  }

  /** 撤销归档（仅 Completed）：归档文件移回源位置 / 删归档副本，记录回退为「已跳过」 */
  async function undoArchive(row) {
    try {
      await ElMessageBox.confirm(
        `将撤销归档《${titleOf(row)}》：归档文件移回源位置（当初为复制则删除归档副本），记录回退为「已跳过」、不再自动重新归档。确认？`,
        '撤销归档',
        { type: 'warning', confirmButtonText: '撤销归档', cancelButtonText: '取消' },
      );
    } catch {
      return; // 取消
    }
    const r = await api.history.undoArchive(row.id);
    const opText = r?.operation === 'DELETE_COPY' ? '已删除归档副本（源文件保留）' : '归档文件已移回源位置';
    ElMessage.success(`撤销归档成功：${opText}`);
    await reload();
  }

  /** 退回重改（仅 Completed/Skipped）：撤销归档并回退到「待人工确认」队列重新填资料 */
  async function reopen(row) {
    try {
      await ElMessageBox.confirm(
        `将《${titleOf(row)}》退回到「待人工确认」队列重新填资料：已归档文件会移回源位置，记录回到待确认状态。确认？`,
        '退回重改',
        { type: 'warning', confirmButtonText: '退回重改', cancelButtonText: '取消' },
      );
    } catch {
      return; // 取消
    }
    await api.history.reopen(row.id);
    ElMessage.success('已退回待确认队列，可在「人工确认」重新填资料');
    await reload();
  }

  /** 重试全部失败：把所有 Failed 重投回队列（源文件不存在的跳过） */
  async function rescanFailed() {
    try {
      await ElMessageBox.confirm('确认重投所有失败记录？源文件已不存在的会自动跳过。', '批量重试', {
        type: 'warning', confirmButtonText: '重试全部', cancelButtonText: '取消',
      });
    } catch {
      return;
    }
    const r = await api.history.rescanFailed();
    ElMessage.success(`已重投 ${r.requeued} 条${r.skipped ? `，跳过 ${r.skipped} 条（源文件不存在）` : ''}`);
    await reload();
  }

  /** 单条重新处理（删旧记录后全新流程重投） */
  const reprocess = (row) => runAction({
    apiFn: api.history.reprocess,
    payload: { ids: [row.id] },
    title: '重新处理',
    body: `将删除该记录并以全新流程重新解析归档：${titleOf(row)}`,
    confirmBtn: '重新处理',
    successHint: REPROCESS_HINT,
  });

  /** 单条删除（含处理时间线级联删）；已归档记录强警示「文件会成孤儿、无法一键移回」 */
  const del = (row) => runAction({
    apiFn: api.history.delete,
    payload: { ids: [row.id] },
    title: '删除记录',
    body: row.status === 'Completed'
      ? `该记录已归档，「删除」只删记录、磁盘文件会留成「孤儿」（媒体库不再显示、日后无法一键移回，只能靠「孤儿文件找回」重新认领）。若只是想撤出或改正归档，请改用「撤销归档」或「退回重改」。仍要永久删除《${titleOf(row)}》的记录吗？`
      : `将永久删除该记录（含处理时间线，仅删记录、磁盘文件保留）：${titleOf(row)}`,
    confirmBtn: '删除',
    danger: true,
  });

  /** 整剧重新处理（按 TmdbId 展开该剧全部终态记录）：删旧重投后记录回非终态，详情页已无可展示记录 → 离场 */
  const reprocessSeries = ({ tmdbId, tmdbMediaType, title }) => runAction({
    apiFn: api.history.reprocess,
    payload: { tmdbId, tmdbMediaType },
    title: '重新处理整剧',
    body: `将对《${title}》在库的全部终态记录删旧重投，确认？`,
    confirmBtn: '重新处理整剧',
    successHint: REPROCESS_HINT,
    onDone: onSeriesGone ? () => onSeriesGone('reprocessed') : undefined,
  });

  /** 整剧删除（按 TmdbId 展开该剧全部终态记录）：删空后作品从媒体库消失 → 离场（回列表） */
  const deleteSeries = ({ tmdbId, tmdbMediaType, title }) => runAction({
    apiFn: api.history.delete,
    payload: { tmdbId, tmdbMediaType },
    title: '删除整剧',
    body: `将永久删除《${title}》在库的全部终态记录（仅删记录、磁盘文件保留），确认？`,
    confirmBtn: '删除整剧',
    danger: true,
    onDone: onSeriesGone ? () => onSeriesGone('deleted') : undefined,
  });

  /** 批量重新处理（选中 ids） */
  const batchReprocess = (ids) => runAction({
    apiFn: api.history.reprocess,
    payload: { ids: [...ids] },
    title: '批量重新处理',
    body: `将对选中的 ${ids.length} 条记录删旧重投，确认？`,
    confirmBtn: '重新处理',
    successHint: REPROCESS_HINT,
  });

  /** 批量删除（选中 ids）：已归档记录删后磁盘文件成孤儿，统一强警示 */
  const batchDelete = (ids) => runAction({
    apiFn: api.history.delete,
    payload: { ids: [...ids] },
    title: '批量删除',
    body: `将永久删除选中的 ${ids.length} 条记录（仅删记录、磁盘文件保留）。其中已归档的记录删后磁盘文件会留成「孤儿」（媒体库不再显示、无法一键移回，只能靠「孤儿文件找回」重新认领）；如需撤出或改正归档请改用「撤销归档 / 退回重改」。确认删除？`,
    confirmBtn: '删除',
    danger: true,
  });

  /** 批量退回重改（选中 ids）：仅 Completed/Skipped 可退回，其余自动跳过（计入失败明细） */
  const batchReopen = (ids) => runAction({
    apiFn: api.history.batchReopen,
    payload: { ids: [...ids] },
    title: '批量退回重改',
    body: `将把选中的 ${ids.length} 条记录退回「待人工确认」队列重新填资料：已归档文件会移回源位置，可在「人工确认」改正匹配后重新归档。仅「已完成 / 已跳过」记录可退回，其余自动跳过。确认？`,
    confirmBtn: '退回重改',
  });

  /** 批量撤销归档（选中 ids）：仅 Completed 可撤销，其余自动跳过（计入失败明细） */
  const batchUndoArchive = (ids) => runAction({
    apiFn: api.history.batchUndoArchive,
    payload: { ids: [...ids] },
    title: '批量撤销归档',
    body: `将撤销选中的 ${ids.length} 条记录的归档：归档文件移回源位置（当初为复制则删归档副本），记录回退为「已跳过」、不再自动重新归档。仅「已完成」记录可撤销，其余自动跳过。确认？`,
    confirmBtn: '撤销归档',
  });

  return { runAction, rescan, undoArchive, reopen, rescanFailed, reprocess, del, reprocessSeries, deleteSeries, batchReprocess, batchDelete, batchReopen, batchUndoArchive };
}
