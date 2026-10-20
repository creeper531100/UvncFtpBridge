"""Exercise the distributed launcher and its INI, with no FTP proxy command-line overrides."""
import argparse
import hashlib
import os
import pathlib
import shutil
import socket
import subprocess
import tempfile

from integration import Machine, ROOT, SocksProxy


def launch(package, script, env, file_tunnel=False):
    command = [str(package / "UvncFtpBridge.exe"), "winscp"]
    if file_tunnel:
        command.append("--file-tunnel")
    command += ["--script", str(script)]
    result = subprocess.run(command,
                            capture_output=True, env=env, timeout=60)
    if result.returncode:
        password = env.get("UVNC_PASSWORD", "")
        output = (result.stdout + result.stderr).decode(errors="replace")
        if password:
            output = output.replace(password, "***")
        raise AssertionError(output)


def read_ini(path):
    import configparser
    ini = configparser.ConfigParser(interpolation=None)
    ini.read(path, encoding="utf-8-sig")
    return ini


def check_listener_closed(port):
    with socket.socket() as sock:
        sock.settimeout(1)
        assert sock.connect_ex(("127.0.0.1", port)) != 0, "launcher leaked its FTP listener"


def run(args):
    with tempfile.TemporaryDirectory(prefix="uvnc-portable-中文 空白-") as temp:
        temp = pathlib.Path(temp)
        package = temp / "可攜套件"
        shutil.copytree(args.package, package)
        script = temp / "commands.txt"
        download = temp / "download.bin"
        ini_path = package / "WinSCP/WinSCP.ini"
        machine = Machine("portable")
        try:
            env = os.environ.copy()
            env["UVNC_PASSWORD"] = "testpass"
            script.write_text("\n".join([
                "option batch abort", "option confirm off",
                f'open ftp://ultravnc@127.0.0.1:{machine.port}/ -password="%UVNC_PASSWORD%"',
                "ls /", f'get -transfer=binary /C:/hello.txt "{download}"', "exit",
            ]), encoding="utf-8-sig")
            launch(package, script, env)
            assert download.read_bytes() == machine.files["C:\\hello.txt"]
            ini = read_ini(ini_path)
            defaults = ini[r"Sessions\Default%20Settings"]
            assert defaults["FSProtocol"] == "5" and defaults["PortNumber"] == "5900"
            assert defaults["FtpProxyLogonType"] == "5"
            check_listener_closed(int(defaults["ProxyPort"]))
            # Simulate a user-saved site whose proxy endpoint belongs to a previous launch.
            with ini_path.open("a", encoding="utf-8") as output:
                output.write(f"\n[Sessions\\Saved-machine]\nHostName=127.0.0.1\nPortNumber={machine.port}\nUserName=ultravnc\nFSProtocol=5\nFtpProxyLogonType=5\nProxyHost=127.0.0.1\nProxyPort=1\n")
            script.write_text("\n".join([
                "option batch abort", 'open Saved-machine -password="%UVNC_PASSWORD%"',
                f'get -transfer=binary /C:/hello.txt "{download}"', "exit",
            ]), encoding="utf-8-sig")
            launch(package, script, env)
            ini = read_ini(ini_path)
            assert ini[r"Sessions\Saved-machine"]["HostName"] == "127.0.0.1"
            check_listener_closed(int(ini[r"Sessions\Saved-machine"]["ProxyPort"]))
            assert not machine.errors, machine.errors
            print("PASS portable launcher: INI proxy defaults, Unicode/space paths, saved sites across launches, bridge shutdown")
        finally:
            machine.close()

        machine = Machine("portable-socks")
        proxy = SocksProxy({("203.0.113.11", machine.port): machine.port})
        try:
            assert (package / "Start-WinSCP-FileTunnel.vbs").exists()
            assert (package / "Start-WinSCP-FileTunnel.cmd").exists()
            assert (package / "Start-FileTunnel-Remote.cmd").exists()
            assert (package / "Start-FileTunnel-Remote.ps1").exists()
            assert (package / "ft.exe").exists()
            env = os.environ.copy()
            env["UVNC_PASSWORD"] = "testpass"
            env["UVNC_SOCKS5_PROXY"] = f"127.0.0.1:{proxy.port}"
            script.write_text("\n".join([
                "option batch abort", "option confirm off",
                f'open ftp://ultravnc@203.0.113.11:{machine.port}/ -password="%UVNC_PASSWORD%"',
                "ls /", f'get -transfer=binary /C:/hello.txt "{download}"', "exit",
            ]), encoding="utf-8-sig")
            launch(package, script, env)
            assert download.read_bytes() == machine.files["C:\\hello.txt"]
            assert ("203.0.113.11", machine.port) in proxy.requests
            assert not proxy.errors and not machine.errors
            print("PASS portable WinSCP uses real machine IP through SOCKS5")
        finally:
            proxy.close()
            machine.close()

        machine = Machine("portable-file-tunnel")
        shared = pathlib.Path(tempfile.gettempdir()) / "UvncFtpBridge" / "FileTunnel"
        shared.mkdir(parents=True, exist_ok=True)
        marker = shared / "active.marker"
        assert not marker.exists(), "another local File-Tunnel session is active"
        remote = subprocess.Popen([
            "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            str(package / "Start-FileTunnel-Remote.ps1"), str(shared),
        ], stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
        try:
            import time
            time.sleep(1)
            assert remote.poll() is None, "remote launcher should wait for the local tunnel"
            marker.write_text("test", encoding="ascii")
            time.sleep(1)
            script.write_text("\n".join([
                "option batch abort", "option confirm off",
                f'open ftp://ultravnc@127.0.0.1:{machine.port}/ -password="%UVNC_PASSWORD%"',
                "ls /", f'get -transfer=binary /C:/hello.txt "{download}"', "exit",
            ]), encoding="utf-8-sig")
            env = os.environ.copy()
            env["UVNC_PASSWORD"] = "testpass"
            launch(package, script, env, file_tunnel=True)
            assert download.read_bytes() == machine.files["C:\\hello.txt"]
            check_listener_closed(1081)
            assert remote.wait(timeout=10) == 0
            assert not machine.errors, machine.errors
            print("PASS portable File-Tunnel SOCKS5 to UltraVNC, remote shutdown and TEMP cleanup")
        finally:
            if remote.poll() is None:
                remote.terminate()
                remote.wait(timeout=5)
            machine.close()

        if args.live_host:
            assert os.environ.get("UVNC_PASSWORD"), "Set UVNC_PASSWORD for the optional live test"
            assert args.source and args.expected
            remote = "/" + args.source.replace("\\", "/").lstrip("/")
            script.write_text("\n".join([
                "option batch abort", "option confirm off",
                f'open ftp://ultravnc@{args.live_host}:{args.port}/ -password="%UVNC_PASSWORD%"',
                "ls /", f'get -transfer=binary -resumesupport=off "{remote}" "{download}"', "exit",
            ]), encoding="utf-8-sig")
            launch(package, script, os.environ.copy())
            assert hashlib.sha256(download.read_bytes()).digest() == hashlib.sha256(args.expected.read_bytes()).digest()
            ini = read_ini(ini_path)
            check_listener_closed(int(ini[r"Sessions\Default%20Settings"]["ProxyPort"]))
            print("PASS portable WinSCP with real UltraVNC: inherited proxy configuration, real target IP, drive list, SHA-256 download match")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=pathlib.Path, default=ROOT / "dist/UltraVNC-WinSCP-Portable")
    parser.add_argument("--live-host")
    parser.add_argument("--port", type=int, default=5900)
    parser.add_argument("--source")
    parser.add_argument("--expected", type=pathlib.Path)
    run(parser.parse_args())
