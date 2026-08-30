#!/usr/bin/env node
/**
 * 把 vite build 产物 dist/* 同步到 src/PersonalMediaManager.Host/wwwroot/
 * 用法：npm run build:host （已在 package.json 串入）
 */
import { mkdirSync, readdirSync, rmSync, statSync, copyFileSync } from 'fs';
import { join, dirname, resolve } from 'path';
import { fileURLToPath } from 'url';

const here = dirname(fileURLToPath(import.meta.url));
const distDir = resolve(here, '..', 'dist');
const wwwroot = resolve(here, '..', '..', 'PersonalMediaManager.Host', 'wwwroot');

if (!statSync(distDir, { throwIfNoEntry: false })) {
  console.error(`[copy-to-host-wwwroot] dist/ 不存在；请先 npm run build`);
  process.exit(1);
}

if (statSync(wwwroot, { throwIfNoEntry: false })) {
  rmSync(wwwroot, { recursive: true, force: true });
}
mkdirSync(wwwroot, { recursive: true });

function copyRecursive(src, dst) {
  const stat = statSync(src);
  if (stat.isDirectory()) {
    mkdirSync(dst, { recursive: true });
    for (const entry of readdirSync(src)) {
      copyRecursive(join(src, entry), join(dst, entry));
    }
  } else {
    copyFileSync(src, dst);
  }
}

copyRecursive(distDir, wwwroot);
console.log(`[copy-to-host-wwwroot] 已同步 dist/ -> ${wwwroot}`);
