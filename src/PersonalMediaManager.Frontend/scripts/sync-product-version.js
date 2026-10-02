#!/usr/bin/env node
import { syncPackageVersions } from './product-version.js';

try {
  const args = process.argv.slice(2);
  if (args.length > 1 || (args.length === 1 && args[0] !== '--check')) {
    throw new Error('用法：node scripts/sync-product-version.js [--check]');
  }
  const check = args.includes('--check');
  const { version, changed } = syncPackageVersions({ check });
  console.log(`[版本] 主版本 ${version}：npm 清单${check ? '校验通过' : changed ? '已同步' : '已一致'}。`);
} catch (error) {
  console.error(`[版本] ${error.message}`);
  process.exitCode = 1;
}
