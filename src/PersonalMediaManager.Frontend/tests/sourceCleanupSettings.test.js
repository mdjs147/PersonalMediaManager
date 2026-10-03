import test from 'node:test';
import assert from 'node:assert/strict';
import { CLEAN_DIRECTORY_KEY, KEEP_ONGOING_KEY, orderCleanupSettings, isCleanupConditionDisabled } from '../src/utils/sourceCleanupSettings.js';

test('可选条件紧跟清理总开关，不受返回键名排序影响', () => {
  const items = {
    [KEEP_ONGOING_KEY]: { value: false },
    [CLEAN_DIRECTORY_KEY]: { value: true },
    'File.CleanEmptyDirIgnoreExts': { value: '.torrent' },
    Other: { value: 'x' },
  };
  assert.deepEqual(orderCleanupSettings(items).map(([key]) => key), [CLEAN_DIRECTORY_KEY, KEEP_ONGOING_KEY, 'File.CleanEmptyDirIgnoreExts', 'Other']);
});

test('总开关关闭和缺失时禁用条件，重启总开关不丢失已保存选择', () => {
  const items = { [CLEAN_DIRECTORY_KEY]: { value: false }, [KEEP_ONGOING_KEY]: { value: true } };
  assert.equal(isCleanupConditionDisabled(items, KEEP_ONGOING_KEY), true);
  assert.equal(isCleanupConditionDisabled({}, KEEP_ONGOING_KEY), true);
  assert.equal(isCleanupConditionDisabled(items, CLEAN_DIRECTORY_KEY), false);
  assert.equal(items[KEEP_ONGOING_KEY].value, true);
  items[CLEAN_DIRECTORY_KEY].value = true;
  assert.equal(isCleanupConditionDisabled(items, KEEP_ONGOING_KEY), false);
  assert.equal(items[KEEP_ONGOING_KEY].value, true);
});

test('旧服务没有新条件时表单顺序不变，排序不修改原对象', () => {
  const items = { Other: { value: 'x' }, [CLEAN_DIRECTORY_KEY]: { value: false } };
  const before = JSON.stringify(items);
  assert.deepEqual(orderCleanupSettings(items).map(([key]) => key), Object.keys(items));
  assert.equal(JSON.stringify(items), before);
});
