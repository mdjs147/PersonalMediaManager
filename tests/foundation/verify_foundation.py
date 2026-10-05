#!/usr/bin/env python3
"""Run the new inert host on Linux using only loopback and fresh temporary data.

This is a real process/socket check, not proof of all possible network histories.
The xUnit suite separately checks the complete endpoint and service registrations.
"""
from __future__ import annotations

import argparse
import http.client
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time


ROOT = Path(__file__).resolve().parents[2]
HEALTH = {"status": "ok", "mode": "foundation", "businessOperationsEnabled": False}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def process_sockets(pid: int) -> list[dict[str, object]]:
    """Filter Linux namespace socket tables to the actual child process's FDs."""
    descriptors = Path(f"/proc/{pid}/fd")
    inodes = set()
    for descriptor in descriptors.iterdir():
        try:
            target = os.readlink(descriptor)
        except FileNotFoundError:  # A just-closed descriptor is a normal race.
            continue
        match = re.fullmatch(r"socket:\[(\d+)\]", target)
        if match:
            inodes.add(match[1])
    observed = []
    for table, family in (("tcp", socket.AF_INET), ("tcp6", socket.AF_INET6),
                          ("udp", socket.AF_INET), ("udp6", socket.AF_INET6)):
        for line in Path(f"/proc/{pid}/net/{table}").read_text().splitlines()[1:]:
            fields = line.split()
            if fields[9] not in inodes:
                continue
            addresses = []
            for value in fields[1:3]:
                host_hex, port_hex = value.split(":")
                raw = bytes.fromhex(host_hex)
                raw = raw[::-1] if family == socket.AF_INET else b"".join(
                    raw[index:index + 4][::-1] for index in range(0, 16, 4))
                addresses.append((socket.inet_ntop(family, raw), int(port_hex, 16)))
            observed.append({"table": table, "state": fields[3],
                             "local": addresses[0], "remote": addresses[1]})
    return observed


def verify_sockets(pid: int, expected_port: int | None = None) -> int | None:
    observed = process_sockets(pid)
    listeners = [item for item in observed if item["state"] == "0A"]
    require(len(listeners) <= 1, f"More than one actual listening socket: {listeners}")
    for item in observed:
        local_host, _ = item["local"]
        remote_host, remote_port = item["remote"]
        require(item["table"] == "tcp", f"Unexpected IPv6 or UDP socket: {item}")
        require(local_host == "127.0.0.1", f"Non-loopback local socket: {item}")
        if remote_port:
            require(ipaddress.ip_address(remote_host).is_loopback,
                    f"Observed non-loopback connection: {item}")
    if not listeners:
        return None
    port = listeners[0]["local"][1]
    require(port > 0, "Kestrel did not acquire an actual dynamic port")
    if expected_port is not None:
        require(port == expected_port, "Listening port changed during verification")
    return port


def request(port: int, method: str, path: str) -> tuple[int, dict[str, str], bytes]:
    # http.client talks directly to loopback and never consults proxy settings.
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
    try:
        connection.request(method, path, headers={"Connection": "close"})
        response = connection.getresponse()
        body = response.read(1024 * 1024 + 1)
        require(len(body) <= 1024 * 1024, "Unexpectedly large inert response")
        return response.status, dict(response.getheaders()), body
    finally:
        connection.close()


def file_snapshot(root: Path) -> dict[str, bytes | None]:
    result = {}
    for path in root.rglob("*"):
        require(not path.is_symlink(), "Host created a symlink in the isolated test area")
        if path.is_dir():
            result[str(path.relative_to(root))] = None
        elif path.is_file():
            result[str(path.relative_to(root))] = path.read_bytes()
        else:
            raise RuntimeError("Unexpected runtime filesystem entry in the isolated test area")
    return result


def verify(configuration: str) -> None:
    require(sys.platform == "linux", "Only Linux /proc host checks are implemented; other platforms NOT-RUN")
    executable = shutil.which("dotnet")
    require(executable is not None, "Reviewed .NET SDK/runtime is absent from PATH")
    dll = ROOT / "src/PersonalMediaManager.Web/bin" / configuration / "net10.0/PersonalMediaManager.Web.dll"
    require(dll.is_file(), "Build the new foundation solution before running this check")
    with tempfile.TemporaryDirectory(prefix="pmm-dev001-host-test-") as fresh:
        sandbox = Path(fresh)
        for name in ("home", "data", "cache", "tmp", "run", "cli"):
            (sandbox / name).mkdir()
        (sandbox / "run/appsettings.json").write_text(
            '{"Urls":"http://0.0.0.0:17436","Kestrel":{"Endpoints":'
            '{"Injected":{"Url":"http://0.0.0.0:17437"}}}}', encoding="utf-8")
        before = file_snapshot(sandbox)
        environment = {
            "PATH": str(Path(executable).parent) + os.pathsep + "/usr/bin:/bin",
            "HOME": str(sandbox / "home"),
            "DOTNET_CLI_HOME": str(sandbox / "cli"),
            "XDG_DATA_HOME": str(sandbox / "data"),
            "XDG_CACHE_HOME": str(sandbox / "cache"),
            "TMPDIR": str(sandbox / "tmp"),
            "TMP": str(sandbox / "tmp"),
            "TEMP": str(sandbox / "tmp"),
            "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_NOLOGO": "1",
            "DOTNET_EnableDiagnostics": "0",
            "ASPNETCORE_ENVIRONMENT": "Development",
            "DOTNET_ENVIRONMENT": "Development",
            "ASPNETCORE_URLS": "http://0.0.0.0:17432",
            "ASPNETCORE_HTTP_PORTS": "17433",
            "ASPNETCORE_HTTPS_PORTS": "17434",
            "Kestrel__Endpoints__Injected__Url": "http://0.0.0.0:17435",
        }
        if "DOTNET_ROOT" in os.environ:
            environment["DOTNET_ROOT"] = os.environ["DOTNET_ROOT"]
        logs: list[str] = []
        process = subprocess.Popen([executable, str(dll), "--port", "0"],
                                   cwd=sandbox / "run", env=environment,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, start_new_session=True)

        def drain() -> None:
            assert process.stdout is not None
            for line in process.stdout:
                if sum(map(len, logs)) < 65536:
                    logs.append(line)

        reader = threading.Thread(target=drain, daemon=True)
        reader.start()
        passed = False
        try:
            deadline = time.monotonic() + 20
            port = None
            while time.monotonic() < deadline:
                require(process.poll() is None, f"Host exited before readiness ({process.returncode})")
                port = verify_sockets(process.pid)
                if port is not None:
                    try:
                        status, _, body = request(port, "GET", "/health")
                        if status == 200:
                            require(json.loads(body) == HEALTH, "Unexpected health contract")
                            break
                    except (ConnectionError, TimeoutError):
                        pass
                time.sleep(0.05)
            else:
                raise RuntimeError("Host did not become healthy within 20 seconds")
            require(port is not None, "No loopback listener observed")
            for _ in range(3):
                require(verify_sockets(process.pid, port) == port, "Listener disappeared")
                status, headers, body = request(port, "GET", "/")
                require(status == 200, "Inert Razor shell failed")
                require("text/html" in headers.get("Content-Type", ""), "Shell is not HTML")
                html = body.decode("utf-8")
                require("<html" in html.lower() and "</html>" in html.lower(), "Incomplete Razor shell")
                require(not re.search(r"(?:src|href)\s*=\s*['\"](?:https?:)?//", html, re.I),
                        "Inert shell requests an external asset")
                require("Set-Cookie" not in headers, "Inert host created a session/antiforgery cookie")
                for path in ("/", "/health"):
                    status, headers, body = request(port, "HEAD", path)
                    require(status == 200 and body == b"", f"HEAD contract failed: {path}")
                    require("Set-Cookie" not in headers, "HEAD created a cookie")
                    require(request(port, "POST", path)[0] == 405, f"Write method accepted: {path}")
                for path in ("/mcp", "/api/media", "/api/jobs", "/account/login", "/not-a-route"):
                    for method in ("GET", "POST"):
                        require(request(port, method, path)[0] == 404, f"Unexpected endpoint: {method} {path}")
                status, headers, body = request(port, "GET", "/health")
                require(status == 200 and json.loads(body) == HEALTH, "Health changed during requests")
                require("application/json" in headers.get("Content-Type", ""), "Health is not JSON")
                require(file_snapshot(sandbox) == before,
                        "Host created/changed isolated runtime files (keys, database or other state)")
            require(verify_sockets(process.pid, port) == port, "Final listener check failed")
            passed = True
        except Exception:
            print("".join(logs), file=sys.stderr)
            raise
        finally:
            if process.poll() is None:
                process.send_signal(signal.SIGTERM)
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=5)
                    if passed:
                        raise RuntimeError("Host did not shut down gracefully")
            reader.join(timeout=2)
            if process.stdout is not None:
                process.stdout.close()
        require(process.returncode == 0, f"Host shutdown was unsuccessful: {process.returncode}")
        require(file_snapshot(sandbox) == before, "Shutdown created or changed persistent runtime state")
    print("FOUNDATION_INERT_AND_ISOLATED_VERIFIED")
    print("Linux actual Kestrel process: one 127.0.0.1 listener, inert Razor/health, no sampled external sockets;")
    print("fresh temporary HOME/XDG/run data unchanged; no old databases, media or product tests accessed.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", choices=("Debug", "Release"), default="Debug")
    arguments = parser.parse_args()
    try:
        verify(arguments.configuration)
    except (OSError, RuntimeError, ValueError, http.client.HTTPException) as error:
        print(f"FOUNDATION_CHECK_FAILED: {error}", file=sys.stderr)
        sys.exit(1)
