import assert from 'node:assert/strict';
import test from 'node:test';
import { createAiBatchSettingsController, defaultAiBatchSettings, validateBatchSizes, validateAiBatchSettings } from '../src/composables/useAiBatchSettings.js';

const keyA = 'a'.repeat(64); const keyB = 'b'.repeat(64);
const copy = (value) => JSON.parse(JSON.stringify(value));
const row = (providerId, values = {}) => ({ providerId, configurationKey: keyA, disableThinking: false, batchSize: 1, contextTokenBudget: 8192, maxOutputTokens: 2048, maxResponseBytes: 65536, ...values });
const provider = (providerId, values = {}) => ({ providerId, configurationKey: keyA, name: `提供商 ${providerId}`, model: 'model', recommendedSettings: null, advancedSettings: null, ...values });
const officialProvider = () => provider(1, { name: '官方服务', model: 'deepseek-flash',
  recommendedSettings: row(1, { disableThinking: true, batchSize: 32, contextTokenBudget: 65536, maxOutputTokens: 32768, maxResponseBytes: 1048576 }),
  advancedSettings: row(1, { disableThinking: true, batchSize: 128, contextTokenBudget: 131072, maxOutputTokens: 65536, maxResponseBytes: 2097152 }) });
function harness(initial = {}, overrides = {}, directory = [provider(1), provider(2), provider(3)]) {
  let stored = copy({ ...defaultAiBatchSettings(), ...initial }); const writes = [];
  const api = { get: async () => copy(stored), getProviders: async () => copy(directory), update: async (value) => { writes.push(copy(value)); stored = copy(value); }, ...overrides };
  return { controller: createAiBatchSettingsController(api), writes, api, set(value) { stored = copy({ ...stored, ...value }); } };
}
function deferred() { let resolve; const promise = new Promise((done) => { resolve = done; }); return { resolve, promise }; }

test('默认各1，外部1～128、内置1～2及预算范围与后端一致', () => {
  const defaults = defaultAiBatchSettings();
  assert.equal(defaults.externalBatchSize, 1); assert.equal(defaults.localBatchSize, 1);
  assert.equal(defaults.maxResponseBytes, 65536); assert.deepEqual(defaults.providerSettings, []);
  for (const [key, invalid] of [['externalBatchSize', [0, 129, 1.5, null]], ['localBatchSize', [0, 3, 1.5, undefined]]]) {
    for (const value of invalid) assert.notEqual(validateBatchSizes({ ...defaults, [key]: value }), '');
  }
  for (const [key, min, max] of [['contextTokenBudget', 512, 1048576], ['maxOutputTokens', 64, 262144], ['maxResponseBytes', 1024, 8388608], ['maxWaitMilliseconds', 0, 1000]]) {
    for (const value of [min, max]) assert.equal(validateAiBatchSettings({ ...defaults, [key]: value }), '');
    for (const value of [min - 1, max + 1, 1.5, null]) assert.notEqual(validateAiBatchSettings({ ...defaults, [key]: value }), '');
  }
  assert.equal(validateAiBatchSettings({ ...defaults, externalBatchSize: 128, localBatchSize: 2 }), '');
});

test('校验provider编号、绑定key及每行预算，拒绝重复和不完整覆盖', () => {
  for (const providerSettings of [[row(0)], [row(Number.MAX_SAFE_INTEGER + 1)], [row(1), row(1)], [row(1, { batchSize: 129 })], [row(1, { configurationKey: '' })], [row(1, { disableThinking: 'true' })], [row(1, { maxResponseBytes: 8388609 })], Array.from({ length: 17 }, (_, index) => row(index + 1)), null]) {
    assert.notEqual(validateAiBatchSettings({ ...defaultAiBatchSettings(), providerSettings }), '');
  }
  assert.equal(validateAiBatchSettings({ ...defaultAiBatchSettings(), providerSettings: [row(1, { batchSize: 128 })] }), '');
});

test('历史配置缺新字段回退默认，加载不自动写设置或启用模型', async () => {
  const h = harness({}, { get: async () => ({}) }, [officialProvider()]);
  await h.controller.load(); assert.equal(h.controller.form.value.externalBatchSize, 1); assert.equal(h.controller.form.value.localBatchSize, 1);
  assert.deepEqual(h.controller.form.value.providerSettings, []);
  assert.equal(h.controller.dirty.value, false); assert.equal(await h.controller.save(), false); assert.deepEqual(h.writes, []);
});

test('外部大小独立保存保留最新本地、预算及其他页面新增provider', async () => {
  const h = harness(); await h.controller.load(); h.controller.form.value.externalBatchSize = 32;
  h.set({ localBatchSize: 2, contextTokenBudget: 12000, maxWaitMilliseconds: 80, providerSettings: [row(2, { batchSize: 8 })] });
  assert.equal(await h.controller.save(), true);
  assert.equal(h.writes[0].externalBatchSize, 32); assert.equal(h.writes[0].localBatchSize, 2); assert.equal(h.writes[0].contextTokenBudget, 12000); assert.equal(h.writes[0].maxWaitMilliseconds, 80);
  assert.deepEqual(h.writes[0].providerSettings, [row(2, { batchSize: 8 })]);
  for (const key of ['mode', 'enabled', 'runtimeExecutablePath']) assert.equal(key in h.writes[0], false);
  assert.equal(h.controller.saved.value.externalBatchSize, 32); assert.equal(h.controller.dirty.value, false);
});

test('本地与默认预算可独立修改，不覆盖其他默认字段', async () => {
  const h = harness({ externalBatchSize: 3, maxOutputTokens: 1024 }); await h.controller.load(); h.controller.form.value.localBatchSize = 2; h.controller.form.value.maxResponseBytes = 131072;
  h.set({ maxOutputTokens: 4096, maxWaitMilliseconds: 0 });
  assert.equal(await h.controller.save(), true); assert.equal(h.writes[0].externalBatchSize, 3); assert.equal(h.writes[0].localBatchSize, 2);
  assert.equal(h.writes[0].maxResponseBytes, 131072); assert.equal(h.writes[0].maxOutputTokens, 4096); assert.equal(h.writes[0].maxWaitMilliseconds, 0);
});

test('全局同字段被另一页面更新时阻止陈旧覆盖', async () => {
  for (const [key, desired, latest] of [['externalBatchSize', 32, 8], ['maxResponseBytes', 131072, 262144]]) {
    const h = harness(); await h.controller.load(); h.controller.form.value[key] = desired; h.set({ [key]: latest });
    assert.equal(await h.controller.save(), false); assert.match(h.controller.error.value, /其他页面修改/); assert.equal(h.writes.length, 0); assert.equal(h.controller.uncertain.value, false);
    h.controller.reset(); assert.equal(await h.controller.load(true), true); assert.equal(h.controller.form.value[key], latest);
  }
});

test('推荐与高级预设仅填所选provider草稿，需保存且可撤销', async () => {
  const h = harness({ providerSettings: [row(2, { batchSize: 2 })] }, {}, [officialProvider(), provider(2)]); await h.controller.load();
  const baseline = copy(h.controller.saved.value);
  assert.equal(h.controller.applyProviderPreset('recommendedSettings'), true);
  assert.equal(h.controller.selectedProviderSettings.value.batchSize, 32); assert.equal(h.controller.selectedProviderSettings.value.contextTokenBudget, 65536);
  assert.equal(h.controller.selectedProviderSettings.value.disableThinking, true); assert.match(h.controller.notice.value, /非思考模式推荐配置/);
  assert.equal(h.writes.length, 0); assert.deepEqual(h.controller.saved.value, baseline); assert.equal(h.controller.form.value.externalBatchSize, 1);
  assert.equal(h.controller.applyProviderPreset('advancedSettings'), true); assert.equal(h.controller.selectedProviderSettings.value.batchSize, 128);
  h.controller.reset(); assert.deepEqual(h.controller.form.value, baseline);
  h.controller.applyProviderPreset('recommendedSettings'); assert.equal(await h.controller.save(), true);
  assert.deepEqual(h.writes[0].providerSettings, [row(2, { batchSize: 2 }), officialProvider().recommendedSettings]);
});

test('名称或模型似DeepSeek不能产生预设；无效或错绑预设也不显示', async () => {
  const h = harness({}, {}, [provider(1, { name: 'DeepSeek 官方', model: 'deepseek-flash' }), provider(2, {
    recommendedSettings: row(1, { batchSize: 32 }), advancedSettings: row(2, { configurationKey: keyB, batchSize: 128 }) })]);
  await h.controller.load(); assert.equal(h.controller.applyProviderPreset('recommendedSettings'), false);
  h.controller.selectedProviderId.value = 2;
  assert.equal(h.controller.selectedProvider.value.recommendedSettings, null); assert.equal(h.controller.selectedProvider.value.advancedSettings, null);
  assert.equal(h.controller.applyProviderPreset('advancedSettings'), false); assert.equal(h.controller.dirty.value, false);
});

test('手动启用继承当前默认草稿，关闭并保存只撤销当前provider覆盖', async () => {
  const h = harness({ providerSettings: [row(2, { batchSize: 8 })] }); await h.controller.load(); h.controller.form.value.externalBatchSize = 16;
  h.controller.setProviderOverride(true); assert.equal(h.controller.selectedProviderSettings.value.batchSize, 16); assert.equal(h.controller.selectedProviderSettings.value.configurationKey, keyA);
  assert.equal(h.controller.selectedProviderSettings.value.disableThinking, false);
  assert.equal(h.controller.selectedSavedSettings.value, undefined); h.controller.setProviderField('batchSize', 32);
  assert.equal(await h.controller.save(), true); assert.equal(h.controller.selectedSavedSettings.value.batchSize, 32);
  h.controller.setProviderOverride(false); assert.equal(h.controller.effectiveProviderSettings.value.batchSize, 16); assert.equal(await h.controller.save(), true);
  assert.deepEqual(h.writes[1].providerSettings, [row(2, { batchSize: 8 })]);
});

test('编辑已有provider按字段合并，保留同一行未编辑预算及其他新增覆盖', async () => {
  const h = harness({ providerSettings: [row(1)] }); await h.controller.load(); h.controller.setProviderField('batchSize', 32);
  h.set({ providerSettings: [row(1, { maxOutputTokens: 8192 }), row(3, { batchSize: 4 })] });
  assert.equal(await h.controller.save(), true);
  assert.deepEqual(h.writes[0].providerSettings, [row(1, { batchSize: 32, maxOutputTokens: 8192 }), row(3, { batchSize: 4 })]);
});

test('达到16条时禁止手动或预设新增第17条，已有覆盖仍可编辑、套预设或删除', async () => {
  const initial = Array.from({ length: 16 }, (_, index) => row(index + 1));
  const directory = [officialProvider(), ...Array.from({ length: 15 }, (_, index) => provider(index + 2)), provider(17, { recommendedSettings: row(17, { batchSize: 32 }) })];
  const h = harness({ providerSettings: initial }, {}, directory); await h.controller.load(); h.controller.selectedProviderId.value = 17;
  assert.equal(h.controller.providerLimitReached.value, true); h.controller.setProviderOverride(true);
  assert.equal(h.controller.selectedProviderSettings.value, undefined); assert.equal(h.controller.applyProviderPreset('recommendedSettings'), false);
  assert.match(h.controller.error.value, /16.*移除不再使用/); assert.equal(h.controller.dirty.value, false); assert.equal(h.writes.length, 0);
  h.controller.selectedProviderId.value = 1; h.controller.setProviderField('batchSize', 8);
  assert.equal(h.controller.selectedProviderSettings.value.batchSize, 8); assert.equal(h.controller.applyProviderPreset('recommendedSettings'), true);
  assert.equal(await h.controller.save(), true); assert.equal(h.writes[0].providerSettings.length, 16);
  h.controller.setProviderOverride(false); assert.equal(h.controller.providerLimitReached.value, false); h.controller.selectedProviderId.value = 17;
  assert.equal(h.controller.applyProviderPreset('recommendedSettings'), true); assert.equal(await h.controller.save(), true);
  assert.equal(h.writes[1].providerSettings.length, 16); assert.equal(h.writes[1].providerSettings.some((value) => value.providerId === 1), false);
  assert.equal(h.writes[1].providerSettings.find((value) => value.providerId === 17).batchSize, 32);
});

test('保存前其他页面新增覆盖导致合并超过16条时不提交或清除对方覆盖', async () => {
  const initial = Array.from({ length: 15 }, (_, index) => row(index + 1));
  const h = harness({ providerSettings: initial }, {}, [provider(16)]); await h.controller.load(); h.controller.setProviderOverride(true);
  h.set({ providerSettings: [...initial, row(17)] });
  assert.equal(await h.controller.save(), false); assert.match(h.controller.error.value, /16.*移除不再使用/);
  assert.equal(h.writes.length, 0); assert.equal(h.controller.uncertain.value, false);
  assert.equal((await h.api.get()).providerSettings.some((value) => value.providerId === 17), true);
});

test('编辑、新增或撤销provider时阻止同字段与整行冲突', async () => {
  const scenarios = [
    { initial: [row(1)], edit: (c) => c.setProviderField('batchSize', 32), latest: [row(1, { batchSize: 8 })] },
    { initial: [row(1)], edit: (c) => c.setProviderField('batchSize', 32), latest: [] },
    { initial: [row(1)], edit: (c) => c.setProviderField('batchSize', 32), latest: [row(1, { configurationKey: keyB })] },
    { initial: [], edit: (c) => c.setProviderOverride(true), latest: [row(1)] },
    { initial: [row(1)], edit: (c) => c.setProviderOverride(false), latest: [row(1, { maxResponseBytes: 131072 })] },
  ];
  for (const scenario of scenarios) {
    const h = harness({ providerSettings: scenario.initial }); await h.controller.load(); scenario.edit(h.controller); h.set({ providerSettings: scenario.latest });
    assert.equal(await h.controller.save(), false); assert.match(h.controller.error.value, /其他页面修改/); assert.equal(h.writes.length, 0);
  }
});

test('配置变化时旧覆盖不视为有效，修改或显式核对只重绑草稿', async () => {
  const h = harness({ providerSettings: [row(1, { batchSize: 32 })] }, {}, [provider(1, { configurationKey: keyB })]); await h.controller.load();
  assert.equal(h.controller.providerConfigurationChanged.value, true); assert.equal(h.controller.providerNeedsRebind.value, true);
  assert.equal(h.controller.effectiveProviderSettings.value.batchSize, 1); assert.equal(h.controller.dirty.value, false);
  h.controller.setProviderField('batchSize', 8); assert.equal(h.controller.selectedProviderSettings.value.configurationKey, keyB);
  assert.equal(h.controller.saved.value.providerSettings[0].configurationKey, keyA); h.controller.reset();
  h.controller.rebindProvider(); assert.equal(h.writes.length, 0); assert.equal(h.controller.providerConfigurationChanged.value, false);
  assert.equal(h.controller.providerNeedsRebind.value, true); assert.equal(await h.controller.save(), true);
  assert.equal(h.writes[0].providerSettings[0].configurationKey, keyB); assert.equal(h.controller.providerNeedsRebind.value, false);
});

test('全局保存不会自动重绑未编辑的过期provider覆盖', async () => {
  const h = harness({ providerSettings: [row(1, { batchSize: 32 })] }, {}, [provider(1, { configurationKey: keyB })]); await h.controller.load();
  h.controller.form.value.localBatchSize = 2; assert.equal(await h.controller.save(), true);
  assert.equal(h.writes[0].providerSettings[0].configurationKey, keyA); assert.equal(h.controller.providerConfigurationChanged.value, true);
});

test('历史覆盖缺非思考字段时默认false，普通编辑和加载官方目录不会自动开启', async () => {
  const legacy = row(1); delete legacy.disableThinking;
  const h = harness({ providerSettings: [legacy] }, {}, [officialProvider()]); await h.controller.load();
  assert.equal(h.controller.selectedProviderSettings.value.disableThinking, false); assert.equal(h.controller.dirty.value, false);
  h.controller.setProviderField('batchSize', 32); assert.equal(await h.controller.save(), true);
  assert.equal(h.writes[0].providerSettings[0].disableThinking, false);
});

test('原配置非思考标记按字段合并保留，不同页面同字段冲突会阻止保存', async () => {
  const h = harness({ providerSettings: [row(1)] }, {}, [officialProvider()]); await h.controller.load(); h.controller.setProviderField('batchSize', 8);
  h.set({ providerSettings: [row(1, { disableThinking: true })] }); assert.equal(await h.controller.save(), true);
  assert.equal(h.writes[0].providerSettings[0].disableThinking, true); assert.equal(h.writes[0].providerSettings[0].batchSize, 8);
  h.controller.setProviderOverride(false); h.controller.setProviderOverride(true);
  h.set({ providerSettings: [row(1, { batchSize: 8, disableThinking: false })] });
  assert.equal(await h.controller.save(), false); assert.match(h.controller.error.value, /其他页面修改/); assert.equal(h.writes.length, 1);
});

test('过期非思考覆盖绑定无支持预设的新配置时显式或字段编辑均清false', async () => {
  for (const edit of [(c) => c.rebindProvider(), (c) => c.setProviderField('batchSize', 2)]) {
    const h = harness({ providerSettings: [row(1, { batchSize: 32, disableThinking: true })] }, {}, [provider(1, { configurationKey: keyB })]); await h.controller.load();
    assert.equal(h.controller.selectedProviderSettings.value.disableThinking, true); edit(h.controller);
    assert.equal(h.controller.selectedProviderSettings.value.configurationKey, keyB); assert.equal(h.controller.selectedProviderSettings.value.disableThinking, false);
    assert.equal(h.controller.saved.value.providerSettings[0].disableThinking, true); assert.equal(await h.controller.save(), true);
    assert.equal(h.writes[0].providerSettings[0].disableThinking, false);
  }
});

test('目录加载失败仍可保存全局，不能编辑或添加未经核对的覆盖', async () => {
  const h = harness({ providerSettings: [row(1)] }, { getProviders: async () => { throw new Error('离线'); } }); await h.controller.load();
  assert.equal(h.controller.ready.value, true); assert.match(h.controller.providerError.value, /目录加载失败/); assert.equal(h.controller.providerOptions.value.length, 1);
  h.controller.setProviderField('batchSize', 32); assert.equal(h.controller.selectedProviderSettings.value.batchSize, 1);
  h.controller.form.value.externalBatchSize = 8; assert.equal(await h.controller.save(), true);
  h.controller.setProviderOverride(false); assert.equal(await h.controller.save(), true); assert.deepEqual(h.writes[1].providerSettings, []);
  assert.equal(h.controller.selectedProviderId.value, null);
});

test('未保存值刷新不会丢弃；重置深复制provider避免污染已保存基线', async () => {
  const h = harness({ providerSettings: [row(1)] }); await h.controller.load(); h.controller.setProviderField('batchSize', 32);
  assert.equal(await h.controller.load(true), false); assert.equal(h.controller.selectedProviderSettings.value.batchSize, 32);
  assert.equal(h.controller.saved.value.providerSettings[0].batchSize, 1);
  h.controller.reset(); h.controller.setProviderField('maxOutputTokens', 8192);
  assert.equal(h.controller.saved.value.providerSettings[0].maxOutputTokens, 2048); assert.equal(h.writes.length, 0);
});

test('重复保存仅提交一次，不确定结果锁定编辑并要求刷新', async () => {
  const pending = deferred(); let updates = 0;
  const h = harness({}, { update: () => { updates += 1; return pending.promise; } }); await h.controller.load(); h.controller.form.value.externalBatchSize = 32;
  const saving = h.controller.save(); assert.equal(await h.controller.save(), false); await Promise.resolve();
  pending.resolve(); assert.equal(await saving, false); assert.equal(updates, 1); assert.equal(h.controller.uncertain.value, true); assert.match(h.controller.error.value, /尚未核实/); assert.equal(await h.controller.save(), false);
  h.controller.reset(); assert.equal(h.controller.form.value.externalBatchSize, 32); h.controller.setProviderOverride(true); assert.deepEqual(h.controller.form.value.providerSettings, []);
  assert.equal(await h.controller.load(true), true); assert.equal(h.controller.uncertain.value, false); assert.equal(h.controller.form.value.externalBatchSize, 1);
});

test('provider回读大小、预算、绑定或删除不一致均进入uncertain', async () => {
  const scenarios = [
    { initial: [], edit: (c) => c.setProviderOverride(true), verified: [] },
    { initial: [row(1)], edit: (c) => c.setProviderField('batchSize', 32), verified: [row(1)] },
    { initial: [row(1)], edit: (c) => c.setProviderField('maxResponseBytes', 131072), verified: [row(1)] },
    { initial: [row(1)], edit: (c) => c.setProviderField('batchSize', 32), verified: [row(1, { batchSize: 32, configurationKey: keyB })] },
    { initial: [row(1)], edit: (c) => c.applyProviderPreset('recommendedSettings'), verified: [row(1, { ...officialProvider().recommendedSettings, disableThinking: false })] },
    { initial: [row(1)], edit: (c) => c.setProviderOverride(false), verified: [row(1)] },
  ];
  for (const scenario of scenarios) {
    const h = harness({ providerSettings: scenario.initial }, {}, [officialProvider()]); await h.controller.load(); scenario.edit(h.controller);
    h.api.update = async () => h.set({ providerSettings: scenario.verified });
    assert.equal(await h.controller.save(), false); assert.equal(h.controller.uncertain.value, true); assert.match(h.controller.error.value, /回读值/);
  }
});

test('保存前本地校验失败不请求提交，加载失败可重试且不能写未知默认值', async () => {
  const h = harness(); await h.controller.load(); h.controller.form.value.maxResponseBytes = 999;
  assert.equal(await h.controller.save(), false); assert.equal(h.writes.length, 0); assert.equal(h.controller.uncertain.value, false);
  let fail = true; const offline = harness({}, { get: async () => { if (fail) throw new Error('离线'); return defaultAiBatchSettings(); } });
  assert.equal(await offline.controller.load(), false); assert.equal(offline.controller.ready.value, false); offline.controller.form.value.externalBatchSize = 32; assert.equal(await offline.controller.save(), false);
  fail = false; assert.equal(await offline.controller.load(), true); assert.equal(offline.controller.form.value.externalBatchSize, 1);
});
