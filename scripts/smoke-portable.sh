#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
# 仅临时空库，无媒体目录、账号、外部提供商或 webhook。
python3 - <<'PY'
import json, os, pathlib, re, signal, socket, subprocess, tempfile, time, urllib.request, xml.etree.ElementTree as ET
root = pathlib.Path.cwd()
expected_version = ET.parse(root / 'Directory.Build.props').find('.//PmmProductVersion').text.strip()
dll = root / 'src/PersonalMediaManager.Server/bin/Release/net10.0/PersonalMediaManager.Server.dll'
with tempfile.TemporaryDirectory(prefix='pmm-smoke-') as temp:
    data = pathlib.Path(temp) / 'data'
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    executable = os.environ.get('PMM_SERVER_EXECUTABLE')
    base = [executable, '--headless'] if executable else ['dotnet', str(dll), '--headless']
    command = base + ['--data-dir', str(data), '--port', str(port)]
    for attempt in range(2):
        with open(pathlib.Path(temp) / f'server-{attempt}.log', 'w+') as log:
            process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)
            try:
                for _ in range(120):
                    if process.poll() is not None:
                        log.seek(0); raise RuntimeError(log.read())
                    try:
                        with urllib.request.urlopen(f'http://127.0.0.1:{port}/api/health', timeout=1) as response:
                            assert json.load(response)['code'] == 0
                        break
                    except (OSError, ValueError): time.sleep(.5)
                else:
                    log.seek(0); raise RuntimeError('启动超时\n' + log.read())
                with urllib.request.urlopen(f'http://127.0.0.1:{port}/api/system/version', timeout=3) as response:
                    version = json.load(response)
                    assert version['code'] == 0
                    assert {version['data'][field] for field in ('product', 'backend', 'frontend')} == {expected_version}
                    database = version['data']['database']
                    assert database['status'] == 'notChecked'
                    assert not database.get('appliedMigrationId') and not database.get('target')
                    assert not database['pendingMigrationIds'] and not database['unknownMigrationIds']
                with urllib.request.urlopen(f'http://127.0.0.1:{port}/', timeout=3) as response:
                    html = response.read().decode()
                    assert '<html' in html.lower()
                    scripts = re.findall(r'<script[^>]+src="([^"]+)"', html)
                    assert scripts, '内嵌前端必须包含实际 JS 资源，不能只是占位页'
                    with urllib.request.urlopen(f'http://127.0.0.1:{port}' + scripts[0], timeout=3) as asset:
                        assert 'javascript' in asset.headers.get('Content-Type', '')
                        assert len(asset.read()) > 100
                assert (data / 'pmm.db').is_file()
                duplicate = subprocess.run(command, capture_output=True, text=True, timeout=15)
                assert duplicate.returncode != 0, '重复实例不得启动'
                conflict = subprocess.run(base + ['--data-dir', str(pathlib.Path(temp) / 'conflict'), '--port', str(port)], capture_output=True, text=True, timeout=30)
                assert conflict.returncode != 0, '显式端口冲突不能静默更换端口'
                assert 'bind' in (conflict.stdout + conflict.stderr).lower(), '必须因真实端口绑定冲突失败'
                if os.name != 'nt':
                    assert data.stat().st_mode & 0o077 == 0
                    assert (data / 'jwt-signing-key.txt').stat().st_mode & 0o077 == 0
                    assert (data / 'pmm.db').stat().st_mode & 0o077 == 0
            finally:
                if process.poll() is None: process.send_signal(signal.SIGTERM)
                try: code = process.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    process.kill(); process.wait(); raise
            assert code == 0, f'退出码 {code}'
            with socket.socket() as sock:
                assert sock.connect_ex(('127.0.0.1', port)) != 0, '关闭后端口仍被占用'
    print('通过：空库启动、health、内嵌 WebUI、独占实例、Unix 权限、SIGTERM、重启及端口释放')
PY
