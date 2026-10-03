export const CLEAN_DIRECTORY_KEY = 'File.CleanEmptyDir';
export const KEEP_ONGOING_KEY = 'File.CleanEmptyDirKeepOngoingSeries';

// 条件紧跟总开关；排序不改动表单对象及用户已保存的值。
export function orderCleanupSettings(items) {
  const entries = Object.entries(items);
  const child = entries.find(([key]) => key === KEEP_ONGOING_KEY);
  const result = entries.filter(([key]) => key !== KEEP_ONGOING_KEY);
  if (child) {
    const parentIndex = result.findIndex(([key]) => key === CLEAN_DIRECTORY_KEY);
    result.splice(parentIndex < 0 ? result.length : parentIndex + 1, 0, child);
  }
  return result;
}

export function isCleanupConditionDisabled(items, key) {
  return key === KEEP_ONGOING_KEY && items[CLEAN_DIRECTORY_KEY]?.value !== true;
}
