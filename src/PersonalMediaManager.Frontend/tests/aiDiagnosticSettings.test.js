import assert from 'node:assert/strict';
import test from 'node:test';
import { createAiDiagnosticSettings } from '../src/composables/aiDiagnosticSettings.js';
const settings = (level = 'Standard') => ({ level, maxArtifactUtf8Bytes: 2097152, maxArtifactTotalBytes: 67108864, maxArtifacts: 256, retentionDays: 7 });
test('default Standard cannot be saved before authenticated settings load', async () => {
  const writes = []; const controller = createAiDiagnosticSettings({ get: async () => settings(), update: async (v) => writes.push(v) });
  controller.state.level = 'Full'; assert.equal(await controller.save(), false); assert.equal(writes.length, 0);
  assert.equal(await controller.load(), true); assert.equal(controller.state.level, 'Standard');
});
test('explicit Full choice is only level transmitted and confirmed', async () => {
  const writes = []; const controller = createAiDiagnosticSettings({ get: async () => settings(), update: async (v) => { writes.push(v); return settings(v); } });
  await controller.load(); controller.state.level = 'Full'; assert.equal(await controller.save(), true);
  assert.deepEqual(writes, ['Full']); assert.equal(controller.state.settings.level, 'Full');
});
test('double save is rejected; uncertain writes require a read before another write', async () => {
  let resolve; let calls = 0;
  const controller = createAiDiagnosticSettings({ get: async () => settings(), update: () => { calls++; return new Promise((done) => { resolve = done; }); } });
  await controller.load(); controller.state.level = 'Full'; const pending = controller.save();
  assert.equal(await controller.save(), false); assert.equal(calls, 1); resolve({});
  assert.equal(await pending, false); assert.equal(controller.state.uncertain, true); assert.equal(await controller.save(), false);
  await controller.load(); assert.equal(controller.state.uncertain, false); assert.equal(controller.state.level, 'Standard');
});
test('failed and malformed load never enables save with guessed settings', async () => {
  const controller = createAiDiagnosticSettings({ get: async () => ({ status: 403 }), update: async () => { throw new Error('unexpected write'); } });
  assert.equal(await controller.load(), false); assert.equal(controller.state.settings, null);
  controller.state.level = 'Full'; assert.equal(await controller.save(), false);
});
