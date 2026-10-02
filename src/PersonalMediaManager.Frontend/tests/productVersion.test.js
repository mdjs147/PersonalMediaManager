import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { readProductVersion, syncPackageVersions } from '../scripts/product-version.js';

function fixture(t, version = '0.4.0') {
  const frontendDir = mkdtempSync(join(tmpdir(), 'pmm-product-version-'));
  t.after(() => rmSync(frontendDir, { recursive: true, force: true }));
  const propsPath = join(frontendDir, 'Directory.Build.props');
  const packagePath = join(frontendDir, 'package.json');
  const lockPath = join(frontendDir, 'package-lock.json');
  const pkg = {
    name: 'pmm-frontend',
    private: true,
    version: '0.9.0',
    scripts: { build: 'vite build' },
    dependencies: { vue: '^3.5.13' },
  };
  const lock = {
    name: pkg.name,
    version: '0.9.0',
    lockfileVersion: 3,
    packages: {
      '': { name: pkg.name, version: '0.9.0', dependencies: { ...pkg.dependencies } },
      'node_modules/vue': { version: '3.5.13', integrity: 'sha512-unchanged' },
    },
  };
  writeFileSync(propsPath, `<Project><PropertyGroup><PmmProductVersion>${version}</PmmProductVersion></PropertyGroup></Project>`);
  writeFileSync(packagePath, `${JSON.stringify(pkg, null, 2)}\n`);
  writeFileSync(lockPath, `${JSON.stringify(lock, null, 2)}\n`);
  return { propsPath, frontendDir, packagePath, lockPath, pkg, lock };
}

test('读取完整主版本，忽略注释示例和旧组件版本', (t) => {
  const f = fixture(t);
  writeFileSync(f.propsPath, `
    <Project>
      <!-- <PmmProductVersion>8.8.8</PmmProductVersion> -->
      <PropertyGroup>
        <PmmProductVersion> 0.4.0 </PmmProductVersion>
        <VersionPrefix>0.12.0</VersionPrefix>
        <PmmDbVersion>0.10.0</PmmDbVersion>
      </PropertyGroup>
    </Project>`);
  assert.equal(readProductVersion(f.propsPath), '0.4.0');
});

test('预发布标识和构建元数据不能进入主版本源', (t) => {
  const f = fixture(t);
  for (const version of ['1.2.3-beta.2', '1.2.3+build.007', '1.2.3-beta.2+build.007']) {
    writeFileSync(f.propsPath, `<Project><PmmProductVersion>${version}</PmmProductVersion></Project>`);
    assert.throws(() => readProductVersion(f.propsPath), /完整 X.Y.Z 版本号/, version);
  }
});

test('主版本缺失、重复或文件丢失时拒绝回退到其他版本', (t) => {
  const f = fixture(t);
  writeFileSync(f.propsPath, '<Project><VersionPrefix>0.12.0</VersionPrefix></Project>');
  assert.throws(() => readProductVersion(f.propsPath), /必须且只能定义一个/);
  writeFileSync(f.propsPath, '<Project><PmmProductVersion>0.4.0</PmmProductVersion><PmmProductVersion>0.5.0</PmmProductVersion></Project>');
  assert.throws(() => readProductVersion(f.propsPath), /必须且只能定义一个/);
  assert.throws(() => readProductVersion(join(f.frontendDir, 'missing.props')), { code: 'ENOENT' });
});

test('不接受空值、只有主位、前导零或待求值的 MSBuild 表达式', (t) => {
  const f = fixture(t);
  for (const version of ['', '0', '0.4', 'v0.4.0', '00.4.0', '0.04.0', '0.4.00', '$(VersionPrefix)']) {
    writeFileSync(f.propsPath, `<Project><PmmProductVersion>${version}</PmmProductVersion></Project>`);
    assert.throws(() => readProductVersion(f.propsPath), /完整 X.Y.Z 版本号/, version);
  }
});

test('同步三处 npm 版本并完整保留脚本、依赖和锁定信息', (t) => {
  const f = fixture(t);
  assert.deepEqual(syncPackageVersions(f), { version: '0.4.0', changed: true });
  const pkg = JSON.parse(readFileSync(f.packagePath, 'utf8'));
  const lock = JSON.parse(readFileSync(f.lockPath, 'utf8'));
  assert.deepEqual(pkg, { ...f.pkg, version: '0.4.0' });
  assert.deepEqual(lock, {
    ...f.lock,
    version: '0.4.0',
    packages: { ...f.lock.packages, '': { ...f.lock.packages[''], version: '0.4.0' } },
  });
  assert.deepEqual(syncPackageVersions({ ...f, check: true }), { version: '0.4.0', changed: false });
});

test('每处清单漂移都令只读检查失败，检查不隐式修复文件', (t) => {
  const f = fixture(t);
  const changes = [
    () => {
      const pkg = JSON.parse(readFileSync(f.packagePath, 'utf8'));
      pkg.version = '9.9.9';
      writeFileSync(f.packagePath, JSON.stringify(pkg));
    },
    () => {
      const lock = JSON.parse(readFileSync(f.lockPath, 'utf8'));
      lock.version = '9.9.9';
      writeFileSync(f.lockPath, JSON.stringify(lock));
    },
    () => {
      const lock = JSON.parse(readFileSync(f.lockPath, 'utf8'));
      lock.packages[''].version = '9.9.9';
      writeFileSync(f.lockPath, JSON.stringify(lock));
    },
  ];
  for (const drift of changes) {
    syncPackageVersions(f);
    drift();
    const beforePackage = readFileSync(f.packagePath, 'utf8');
    const beforeLock = readFileSync(f.lockPath, 'utf8');
    assert.throws(() => syncPackageVersions({ ...f, check: true }), /npm run version:sync/);
    assert.equal(readFileSync(f.packagePath, 'utf8'), beforePackage);
    assert.equal(readFileSync(f.lockPath, 'utf8'), beforeLock);
  }
});

test('只修改主版本源即可重新生成清单，重复同步不改写已一致文件', (t) => {
  const f = fixture(t);
  syncPackageVersions(f);
  writeFileSync(f.propsPath, '<Project><PmmProductVersion>0.5.0</PmmProductVersion></Project>');
  assert.throws(() => syncPackageVersions({ ...f, check: true }), /PmmProductVersion=0.5.0/);
  assert.deepEqual(syncPackageVersions(f), { version: '0.5.0', changed: true });
  const beforePackage = statSync(f.packagePath, { bigint: true }).mtimeNs;
  const beforeLock = statSync(f.lockPath, { bigint: true }).mtimeNs;
  assert.deepEqual(syncPackageVersions(f), { version: '0.5.0', changed: false });
  assert.equal(statSync(f.packagePath, { bigint: true }).mtimeNs, beforePackage);
  assert.equal(statSync(f.lockPath, { bigint: true }).mtimeNs, beforeLock);
});

test('锁文件无效时不提前改写 package.json', (t) => {
  const f = fixture(t);
  const beforePackage = readFileSync(f.packagePath, 'utf8');
  for (const invalidLock of ['{', 'null', '{"version":"0.9.0","packages":{}}', '{"packages":{"":[]}}']) {
    writeFileSync(f.lockPath, invalidLock);
    assert.throws(() => syncPackageVersions(f));
    assert.equal(readFileSync(f.packagePath, 'utf8'), beforePackage);
    assert.equal(readFileSync(f.lockPath, 'utf8'), invalidLock);
  }
});

test('包清单根结构无效时保留锁文件', (t) => {
  const f = fixture(t);
  const beforeLock = readFileSync(f.lockPath, 'utf8');
  for (const invalidPackage of ['null', '[]', '"0.9.0"']) {
    writeFileSync(f.packagePath, invalidPackage);
    assert.throws(() => syncPackageVersions(f), /有效的 npm 包对象/);
    assert.equal(readFileSync(f.packagePath, 'utf8'), invalidPackage);
    assert.equal(readFileSync(f.lockPath, 'utf8'), beforeLock);
  }
});
