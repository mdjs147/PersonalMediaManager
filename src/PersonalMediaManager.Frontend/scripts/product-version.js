import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontendDirectory = fileURLToPath(new URL('../', import.meta.url));
const productPropsPath = resolve(frontendDirectory, '..', '..', 'Directory.Build.props');
const numericIdentifier = '(?:0|[1-9]\\d*)';
// 与 .NET 程序集及发布标签保持同一格式，提交与构建信息单独记录。
const productVersionPattern = new RegExp(
  `^${numericIdentifier}\\.${numericIdentifier}\\.${numericIdentifier}$`,
);

/** 注释外的原文分段独立读取，绝不拼接成原文件中不存在的标签或值。 */
function xmlSegmentsOutsideComments(xml) {
  const segments = [];
  let cursor = 0;
  while (cursor < xml.length) {
    const start = xml.indexOf('<!--', cursor);
    if (start === -1) {
      segments.push(xml.slice(cursor));
      break;
    }
    segments.push(xml.slice(cursor, start));
    const end = xml.indexOf('-->', start + 4);
    if (end === -1) {
      throw new Error('Directory.Build.props 包含未闭合的 XML 注释。');
    }
    const comment = xml.slice(start + 4, end);
    // XML 注释不能嵌套、包含双连字符，或以连字符结束。
    if (comment.includes('--') || comment.endsWith('-')) {
      throw new Error('Directory.Build.props 包含格式无效的 XML 注释。');
    }
    cursor = end + 3;
  }
  return segments;
}

/** 读取唯一人工维护的完整主版本号，配置异常时阻止构建。 */
export function readProductVersion(propsPath = productPropsPath) {
  const segments = xmlSegmentsOutsideComments(readFileSync(propsPath, 'utf8'));
  const values = segments.flatMap((segment) =>
    [...segment.matchAll(/<PmmProductVersion\b[^>]*>([^<]*)<\/PmmProductVersion\s*>/g)],
  );
  if (values.length !== 1) {
    throw new Error('Directory.Build.props 必须且只能定义一个 PmmProductVersion。');
  }

  const version = values[0][1].trim();
  if (!productVersionPattern.test(version)) {
    throw new Error(`PmmProductVersion 必须是无前导零的完整 X.Y.Z 版本号，当前值：${version || '空'}。`);
  }
  return version;
}

/** npm 清单中的版本是主版本的生成副本，不单独维护。 */
export function syncPackageVersions({
  propsPath = productPropsPath,
  frontendDir = frontendDirectory,
  check = false,
} = {}) {
  const version = readProductVersion(propsPath);
  const packagePath = resolve(frontendDir, 'package.json');
  const lockPath = resolve(frontendDir, 'package-lock.json');
  const pkg = JSON.parse(readFileSync(packagePath, 'utf8'));
  const lock = JSON.parse(readFileSync(lockPath, 'utf8'));
  if (!pkg || typeof pkg !== 'object' || Array.isArray(pkg)) {
    throw new Error('package.json 必须是有效的 npm 包对象。');
  }
  const rootPackage = lock?.packages?.[''];
  if (!rootPackage || typeof rootPackage !== 'object' || Array.isArray(rootPackage)) {
    throw new Error('package-lock.json 缺少根包 packages[""]，请重新生成有效的 npm 锁文件。');
  }

  const packageChanged = pkg.version !== version;
  const lockChanged = lock.version !== version || lock.packages[''].version !== version;
  if (check && (packageChanged || lockChanged)) {
    throw new Error(`npm 清单版本与 PmmProductVersion=${version} 不一致，请运行 npm run version:sync。`);
  }

  // 先验证两份清单，再只更新版本字段；依赖与锁定信息保持原值。
  if (!check && packageChanged) {
    pkg.version = version;
    writeFileSync(packagePath, `${JSON.stringify(pkg, null, 2)}\n`);
  }
  if (!check && lockChanged) {
    lock.version = version;
    lock.packages[''].version = version;
    writeFileSync(lockPath, `${JSON.stringify(lock, null, 2)}\n`);
  }
  return { version, changed: packageChanged || lockChanged };
}
