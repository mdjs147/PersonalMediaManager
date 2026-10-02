import assert from 'node:assert/strict';
import test from 'node:test';
import {
  createLocalAiSettingsController, defaultLocalAiSettings, downloadPercent,
  localAiLimits, modelVerificationLabel, validateLocalAiSettings,
} from '../src/composables/useLocalAiSettings.js';

const modelId = 'qwen2.5-0.5b-instruct-q8_0';
const huihuiId = 'huihui-qwen2.5-0.5b-v3-q8_0';

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
}

function harness(overrides = {}) {
  const calls = [];
  const timers = new Map();
  let nextTimer = 0;
  const snapshot = {
    state: 'Stopped', modelId, runtimeConfigured: true, platformSupported: true,
    message: '已停止', downloadState: 'Idle', downloadedBytes: 0,
  };
  const api = {
    get: async () => defaultLocalAiSettings(),
    models: async () => [{ id: modelId, installed: true, canDownload: true, canVerify: true }],
    status: async () => ({ ...snapshot }),
    ...Object.fromEntries(['update', 'download', 'cancelDownload', 'start', 'stop'].map((name) => [name,
      async (...args) => { calls.push({ name, args }); },
    ])),
    ...overrides,
  };
  const controller = createLocalAiSettingsController(api, {
    schedule(callback) { const id = ++nextTimer; timers.set(id, callback); return id; },
    unschedule(id) { timers.delete(id); },
  });
  async function tick() {
    const [id, callback] = timers.entries().next().value;
    timers.delete(id);
    await callback();
  }
  return { controller, api, calls, timers, snapshot, tick };
}

test('默认关闭且资源默认值符合后端边界', () => {
  const settings = defaultLocalAiSettings();
  assert.equal(settings.mode, 'Disabled');
  assert.equal(settings.runtimeExecutablePath, '');
  assert.equal(settings.modelId, modelId);
  assert.equal(validateLocalAiSettings(settings), '');
  for (const field of localAiLimits) {
    for (const value of [field.min - 1, field.max + 1, field.min + 0.5, NaN, undefined]) {
      assert.notEqual(validateLocalAiSettings({ ...settings, [field.key]: value }), '', `${field.key}: ${value}`);
    }
  }
  assert.notEqual(validateLocalAiSettings({ ...settings, contextTokens: 512, maxOutputTokens: 512 }), '');
  assert.notEqual(validateLocalAiSettings({ ...settings, mode: 0 }), '');
  assert.notEqual(validateLocalAiSettings({ ...settings, modelId: '自定义远端模型' }), '');
  assert.equal(validateLocalAiSettings({ ...settings, modelId: huihuiId }), '');
  assert.notEqual(validateLocalAiSettings({ ...settings, modelId: 'huihui-qwen3-0.6b-q8_0' }), '');
});

test('运行时只接受本机可执行文件绝对路径', () => {
  for (const runtimeExecutablePath of ['/opt/llama.cpp/llama-server', 'C:\\llama.cpp\\llama-server.exe']) {
    assert.equal(validateLocalAiSettings({ ...defaultLocalAiSettings(), runtimeExecutablePath }), '');
  }
  for (const runtimeExecutablePath of ['llama-server', '/opt/llama-server --host 0.0.0.0', '//server/llama-server', '\\\\server\\llama-server.exe', 'https://host/llama-server', '/bin/sh', '/opt/llama-server\n']) {
    assert.notEqual(validateLocalAiSettings({ ...defaultLocalAiSettings(), runtimeExecutablePath }), '');
  }
});

test('保存只提交明确选择的设置，不隐式下载或启动', async () => {
  const { controller, calls } = harness();
  await controller.load();
  controller.form.value.mode = 'AfterRules';
  controller.form.value.runtimeExecutablePath = '/opt/llama-server';
  controller.form.value.unexpected = '不应提交';
  assert.equal(await controller.save(), true);
  assert.deepEqual(calls.map((call) => call.name), ['update']);
  assert.equal(calls[0].args[0].mode, 'AfterRules');
  assert.equal('unexpected' in calls[0].args[0], false);
  assert.equal(controller.dirty.value, false);
  assert.equal(controller.saved.value.mode, 'AfterRules');
  controller.dispose();
});

test('启动与停止不会把关闭模式改成启用', async () => {
  const { controller, calls } = harness();
  await controller.load();
  await controller.start();
  await controller.stop();
  assert.deepEqual(calls.map((call) => call.name), ['start', 'stop']);
  assert.equal(controller.form.value.mode, 'Disabled');
  assert.equal(controller.saved.value.mode, 'Disabled');
  controller.dispose();
});

test('选择本地实验模型保持关闭，手动刷新发现文件后单独启动', async () => {
  let present = false;
  const state = harness({ models: async () => [{ id: huihuiId, canDownload: false, canVerify: true, installed: present }] });
  await state.controller.load();
  state.controller.form.value.modelId = huihuiId;
  assert.equal(await state.controller.save(), true);
  assert.equal(state.controller.saved.value.modelId, huihuiId);
  assert.equal(state.controller.saved.value.mode, 'Disabled');
  assert.equal(state.controller.models.value[0].installed, false);
  assert.deepEqual(state.calls.map((call) => call.name), ['update']);
  present = true;
  await state.controller.refresh();
  assert.equal(state.controller.models.value[0].installed, true);
  assert.equal(state.controller.models.value[0].canDownload, false);
  assert.equal(await state.controller.start(), true);
  assert.deepEqual(state.calls.map((call) => call.name), ['update', 'start']);
  assert.equal(state.controller.saved.value.mode, 'Disabled');
  state.controller.dispose();
});

test('本地文件大小匹配不能显示已通过哈希，运行状态失联后也不能宣称校验成功', () => {
  const model = { id: huihuiId, canVerify: true, canDownload: false, installed: false };
  assert.match(modelVerificationLabel(model), /手动放入.*固定路径.*SHA256.*保留原文件/);
  model.installed = true;
  assert.match(modelVerificationLabel(model), /大小匹配.*启动前.*SHA256/);
  const running = { modelId: huihuiId, state: 'Running' };
  assert.match(modelVerificationLabel(model, running), /本次启动已通过/);
  assert.doesNotMatch(modelVerificationLabel(model, running, true), /已通过/);
  assert.doesNotMatch(modelVerificationLabel(model, { modelId: huihuiId, state: 'Faulted' }), /已通过/);
});

test('重复点击和跨操作点击只发出一个变更请求', async () => {
  const waiting = deferred();
  let downloads = 0;
  let stops = 0;
  const { controller, timers } = harness({
    download: async () => { downloads += 1; await waiting.promise; },
    stop: async () => { stops += 1; },
  });
  await controller.load();
  const first = controller.download(modelId);
  assert.equal(controller.busy.value, true);
  assert.equal(timers.size, 0);
  assert.equal(await controller.download(modelId), false);
  assert.equal(await controller.stop(), false);
  waiting.resolve();
  assert.equal(await first, true);
  assert.equal(downloads, 1);
  assert.equal(stops, 0);
  assert.equal(timers.size, 1);
  controller.dispose();
});

test('轮询保留未保存编辑，并在下载终态刷新模型列表', async () => {
  const state = harness();
  await state.controller.load();
  state.controller.form.value.threads = 4;
  state.snapshot.downloadState = 'Completed';
  state.api.models = async () => [{ id: modelId, installed: true, canDownload: true }];
  await state.tick();
  assert.equal(state.controller.form.value.threads, 4);
  assert.equal(state.controller.dirty.value, true);
  assert.equal(state.controller.status.value.downloadState, 'Completed');
  assert.equal(state.controller.models.value[0].installed, true);
  assert.equal(state.timers.size, 1);
  state.controller.dispose();
});

test('取消下载只请求取消，保留模型选择和介入模式', async () => {
  const state = harness();
  state.snapshot.downloadState = 'Downloading';
  state.snapshot.downloadModelId = modelId;
  await state.controller.load();
  await state.controller.cancelDownload();
  assert.deepEqual(state.calls.map((call) => call.name), ['cancelDownload']);
  assert.equal(state.controller.form.value.modelId, modelId);
  assert.equal(state.controller.form.value.mode, 'Disabled');
  state.controller.dispose();
});

test('失联后暂停轮询并标记旧状态，手动刷新成功才恢复', async () => {
  const state = harness();
  state.snapshot.state = 'Running';
  await state.controller.load();
  state.api.status = async () => { throw new Error('连接中断'); };
  await state.tick();
  assert.equal(state.timers.size, 0);
  assert.equal(state.controller.stale.value, true);
  assert.equal(state.controller.status.value.state, 'Running');
  assert.match(state.controller.error.value, /自动刷新已暂停.*连接中断/);
  state.api.status = async () => ({ ...state.snapshot, state: 'Stopped' });
  await state.controller.refresh();
  assert.equal(state.controller.stale.value, false);
  assert.equal(state.controller.error.value, '');
  assert.equal(state.controller.status.value.state, 'Stopped');
  assert.equal(state.timers.size, 1);
  state.controller.dispose();
});

test('切页移除轮询、中止请求并忽略迟到的响应', async () => {
  const state = harness();
  await state.controller.load();
  const pending = deferred();
  let signal;
  state.api.status = async (options) => { signal = options.signal; return pending.promise; };
  const refreshing = state.controller.refresh();
  state.controller.dispose();
  assert.equal(signal.aborted, true);
  assert.equal(state.timers.size, 0);
  pending.resolve({ ...state.snapshot, state: 'Running' });
  await refreshing;
  assert.equal(state.controller.status.value.state, 'Stopped');
  assert.equal(state.timers.size, 0);
  assert.equal(await state.controller.start(), false);
});

test('初次加载失败后可重试，加载成功前不能提交动作', async () => {
  const state = harness({ get: async () => { throw new Error('暂时无法读取设置'); } });
  assert.equal(await state.controller.load(), false);
  assert.equal(state.controller.ready.value, false);
  assert.equal(await state.controller.start(), false);
  assert.equal(state.timers.size, 0);
  state.api.get = async () => defaultLocalAiSettings();
  assert.equal(await state.controller.refresh(), true);
  assert.equal(state.controller.ready.value, true);
  state.controller.dispose();
});

test('提交成功但刷新失败不会宣称服务健康或自动重发变更', async () => {
  const state = harness();
  await state.controller.load();
  state.api.status = async () => { throw new Error('状态读取超时'); };
  assert.equal(await state.controller.start(), false);
  assert.equal(state.controller.stale.value, true);
  assert.match(state.controller.error.value, /操作已提交，但状态刷新失败/);
  assert.deepEqual(state.calls.map((call) => call.name), ['start']);
  assert.equal(state.timers.size, 0);
  state.controller.dispose();
});

test('不确定的保存结果保留草稿，不自动重试或改为已保存', async () => {
  const state = harness({ update: async () => { throw new Error('请求超时'); } });
  await state.controller.load();
  state.controller.form.value.mode = 'BeforeRules';
  assert.equal(await state.controller.save(), false);
  assert.equal(state.controller.form.value.mode, 'BeforeRules');
  assert.equal(state.controller.saved.value.mode, 'Disabled');
  assert.equal(state.controller.dirty.value, true);
  assert.equal(state.controller.stale.value, true);
  state.controller.dispose();
});

test('下载进度在未知长度和边界输入下保持有效范围', () => {
  assert.equal(downloadPercent(null), 0);
  assert.equal(downloadPercent({ downloadedBytes: 10, downloadTotalBytes: null }), 0);
  assert.equal(downloadPercent({ downloadedBytes: 50, downloadTotalBytes: 100 }), 50);
  assert.equal(downloadPercent({ downloadedBytes: 200, downloadTotalBytes: 100 }), 100);
  assert.equal(downloadPercent({ downloadedBytes: -1, downloadTotalBytes: 100 }), 0);
});
