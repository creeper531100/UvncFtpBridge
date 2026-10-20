"""Read-only live test. Password is read from UVNC_PASSWORD, never stored in a script.

python tests/live-readonly.py --host 127.0.0.1 --source 'D:\\path\\file' --expected local-file
"""
import argparse
import hashlib
import os
import pathlib
import subprocess
import tempfile

from integration import ROOT


def run(args):
    password = os.environ.get("UVNC_PASSWORD")
    if not password:
        raise SystemExit("Set UVNC_PASSWORD in the process environment first.")
    # Bridge only needs the VNC port; the target address is passed explicitly here.
    command = [str(args.bridge.resolve())] if args.bridge else ["dotnet", str(ROOT / "bin/Release/net9.0/UvncFtpBridge.dll")]
    target_options = [] if args.ftp_proxy else ["--target", args.host]
    process = subprocess.Popen(command + ["--ftp", "--port", "0"] + target_options +
                              ["--vnc-port", str(args.port), "--ready-stdout"],
                               stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding="utf-8")
    try:
        ready = process.stdout.readline().strip()
        assert ready.startswith("UVNCFTP/1 "), ready
        local_port = int(ready.split()[1])
        login = f'open ftp://ultravnc@127.0.0.1:{local_port}/ -password="%UVNC_PASSWORD%" -passive=on'
        if args.ftp_proxy:
            login = (f'open ftp://ultravnc@{args.host}:{args.port}/ -password="%UVNC_PASSWORD%" -passive=on '
                     f'-rawsettings FtpProxyLogonType=5 ProxyHost=127.0.0.1 ProxyPort={local_port}')
        remote = "/" + args.source.replace("\\", "/").lstrip("/")
        with tempfile.TemporaryDirectory(prefix="uvnc-live-readonly-") as temp:
            temp = pathlib.Path(temp)
            download = temp / "download.bin"
            script = temp / "commands.txt"
            script.write_text("\n".join([
                "option batch abort", "option confirm off",
                login,
                "ls /", f'ls "{remote.rsplit("/", 1)[0]}"',
                f'get -transfer=binary -resumesupport=off "{remote}" "{download}"', "exit",
            ]), encoding="utf-8-sig")
            result = subprocess.run([args.winscp, "/ini=nul", f"/script={script}"],
                                    capture_output=True, timeout=120)
            if result.returncode:
                output = (result.stdout + result.stderr).decode(errors="replace").replace(password, "***")
                raise RuntimeError(output)
            expected = hashlib.sha256(args.expected.read_bytes()).hexdigest()
            actual = hashlib.sha256(download.read_bytes()).hexdigest()
            assert actual == expected, (actual, expected)
            mode = "native WinSCP FTP proxy with real target in HostName" if args.ftp_proxy else "fixed-target adapter"
            print(f"PASS real UltraVNC {args.host}:{args.port}: {mode}; login, list and download; SHA256={actual}")
    finally:
        process.terminate()
        process.communicate(timeout=10)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", required=True)
    parser.add_argument("--port", type=int, default=5900)
    parser.add_argument("--source", required=True)
    parser.add_argument("--expected", type=pathlib.Path, required=True)
    parser.add_argument("--bridge", type=pathlib.Path)
    parser.add_argument("--ftp-proxy", action="store_true", help="Use stock WinSCP with the machine address in its HostName field")
    parser.add_argument("--winscp", default=r"C:\Program Files (x86)\WinSCP\WinSCP.com")
    run(parser.parse_args())
