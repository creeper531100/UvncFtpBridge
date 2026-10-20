"""Local integration checks; never connects to a real machine or writes its files.

python tests/integration.py [--bridge dist/UvncFtpBridge.exe] [--winscp <WinSCP.com>]
The mock speaks RFB 3.8 + UltraVNC FT v4, with an in-memory filesystem.
"""
import argparse
import concurrent.futures
import ftplib
import io
import pathlib
import socket
import struct
import subprocess
import tempfile
import threading
import time


ROOT = pathlib.Path(__file__).resolve().parent.parent


def exact(sock, count):
    result = b""
    while len(result) < count:
        part = sock.recv(count - len(result))
        if not part:
            raise EOFError()
        result += part
    return result


def ft(sock, kind, param=0, size=0, data=b"", tail=b""):
    sock.sendall(bytes([7, kind]) + struct.pack("<H", param) +
                 struct.pack(">II", size, len(data)) + data + tail)


class Machine:
    def __init__(self, name, reject_login=False):
        self.name = name
        self.reject_login = reject_login
        self.files = {"C:\\hello.txt": ("hello from " + name + "\n").encode(),
                      "C:\\中文.txt": "測試檔案\n".encode()}
        self.dirs = {"C:\\"}
        self.connections = 0
        self.errors = []
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.port = self.listener.getsockname()[1]
        self.listener.listen()
        self.listener.settimeout(0.1)
        self.closed = False
        self.thread = threading.Thread(target=self.accept, daemon=True)
        self.thread.start()

    def accept(self):
        while not self.closed:
            try:
                conn, _ = self.listener.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            self.connections += 1
            threading.Thread(target=self.serve, args=(conn,), daemon=True).start()

    def serve(self, conn):
        try:
            with conn:
                conn.settimeout(10)
                conn.sendall(b"RFB 003.008\n")
                assert exact(conn, 12) == b"RFB 003.008\n"
                # Use password authentication; record the challenge response and exercise rejection.
                conn.sendall(b"\x01\x02")
                assert exact(conn, 1) == b"\x02"
                conn.sendall(bytes(range(16)))
                assert len(exact(conn, 16)) == 16
                if self.reject_login:
                    reason = b"mock authentication denied"
                    conn.sendall(struct.pack(">II", 1, len(reason)) + reason)
                    return
                conn.sendall(struct.pack(">I", 0))
                assert exact(conn, 1) == b"\x01"  # shared connection
                name = self.name.encode()
                conn.sendall(struct.pack(">HH", 800, 600) + bytes(16) + struct.pack(">I", len(name)) + name)
                upload_path = None
                upload = bytearray()
                download = None
                while True:
                    msg = exact(conn, 1)[0]
                    if msg == 0:
                        exact(conn, 19)
                        continue
                    if msg == 2:
                        header = exact(conn, 3)
                        exact(conn, struct.unpack(">H", header[1:])[0] * 4)
                        ft(conn, 17, 4, 32768)
                        continue
                    if msg == 13:
                        continue
                    assert msg == 7, msg
                    header = exact(conn, 11)
                    kind = header[0]
                    param = struct.unpack("<H", header[1:3])[0] & 0x7FFF
                    size, length = struct.unpack(">II", header[3:])
                    data = exact(conn, length)
                    path = data.decode("utf-8").rstrip("\0") if kind in (1, 3, 8, 10) else ""
                    if kind == 8:
                        exact(conn, 4)  # upload sizeH
                    if kind in (15, 16):
                        continue
                    if kind == 7:
                        ft(conn, 14, size=1)
                    elif kind == 1 and param == 2:
                        ft(conn, 2, 3, data=b"C:l\0")
                    elif kind == 1:
                        assert path in self.dirs, path
                        ft(conn, 2, 0x8001, data=path.encode())
                        for entry in sorted(set(self.files) | self.dirs):
                            if entry == path or not entry.startswith(path):
                                continue
                            name = entry[len(path):].rstrip("\\")
                            if "\\" in name:
                                continue
                            is_dir = entry in self.dirs
                            content = self.files.get(entry, b"")
                            find = bytearray(44)
                            struct.pack_into("<I", find, 0, 0x10 if is_dir else 0x20)
                            struct.pack_into("<Q", find, 20, 133444736000000000)
                            struct.pack_into("<I", find, 32, len(content))
                            ft(conn, 2, 0x8001 if is_dir else 0x8002,
                               data=bytes(find) + (name + "\0").encode("utf-16le"))
                        ft(conn, 2)
                    elif kind == 3:
                        download = self.files.get(path)
                        ft(conn, 4, size=len(download) if download is not None else 0xFFFFFFFF,
                           data=data, tail=struct.pack(">I", 0 if download is not None else 0xFFFFFFFF))
                    elif kind == 4:
                        assert download is not None
                        ft(conn, 5, data=download)
                        ft(conn, 6)
                    elif kind == 8:
                        upload_path = path.rsplit(",", 1)[0]
                        upload = bytearray()
                        ft(conn, 9)
                    elif kind == 5:
                        upload.extend(data)
                    elif kind == 6:
                        assert upload_path
                        self.files[upload_path] = bytes(upload)
                        upload_path = None
                    elif kind == 10:
                        if param == 1:
                            self.dirs.add(path.rstrip("\\") + "\\")
                        elif param == 4:
                            self.files.pop(path, None)
                            self.dirs.discard(path.rstrip("\\") + "\\")
                        elif param == 5:
                            old, new = path.split("*", 1)
                            self.files[new] = self.files.pop(old)
                        else:
                            raise AssertionError(param)
                        ft(conn, 11, param)
                    else:
                        raise AssertionError((kind, param, size))
        except (EOFError, ConnectionResetError, ConnectionAbortedError):
            pass
        except Exception as exc:
            self.errors.append(repr(exc))

    def close(self):
        self.closed = True
        self.listener.close()
        self.thread.join(timeout=2)


class Bridge:
    def __init__(self, command, machine):
        self.process = subprocess.Popen(command + ["--ftp", "--port", "0", "--target", "127.0.0.1",
                                       "--vnc-port", str(machine.port), "--ready-stdout"],
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
        self.logs = []
        threading.Thread(target=lambda: self.logs.extend(self.process.stderr), daemon=True).start()
        with concurrent.futures.ThreadPoolExecutor() as pool:
            ready = pool.submit(self.process.stdout.readline).result(timeout=30).strip()
        assert ready.startswith("UVNCFTP/1 "), repr(ready)
        self.port = int(ready.split()[1])
        assert 0 < self.port < 65536

    def ftp(self, user="ultravnc"):
        ftp = ftplib.FTP()
        ftp.connect("127.0.0.1", self.port, timeout=10)
        ftp.login(user, "testpass")
        return ftp

    def close(self):
        self.process.terminate()
        self.process.communicate(timeout=10)


def run(command, winscp):
    for arguments in (["--ftp", "--port", "no"], ["--ftp", "--port", "65536"],
                      ["--ftp", "--vnc-port", "0"], ["--ftp", "--target", "bad host"],
                      ["--ftp", "--target"],
                      ["--ftp", "--bind", "0.0.0.0"]):
        result = subprocess.run(command + arguments, capture_output=True, timeout=10)
        assert result.returncode == 2, (arguments, result.returncode)
    assert subprocess.run(command + ["gui"], capture_output=True).returncode == 2
    help_result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
    assert help_result.returncode == 0 and "【圖形介面】" not in help_result.stdout
    print("PASS CLI-only entry points and invalid argument handling")

    machines = [Machine("machine-A"), Machine("machine-B"), Machine("denied", reject_login=True)]
    bridges = []
    try:
        bridges = [Bridge(command, machine) for machine in machines]
        assert len({bridge.port for bridge in bridges}) == 3
        a, b, denied = bridges
        # USER deliberately contains an unrelated IP: fixed target must win.
        with a.ftp("203.0.113.99:5900") as ftp, b.ftp() as other:
            assert ftp.pwd() == "/"
            assert ftp.nlst() == ["C:"]
            assert dict(ftp.mlsd("/"))["C:"]["type"] == "dir"
            ftp.cwd("/C:")
            assert {name for name, _ in ftp.mlsd()} == {"hello.txt", "中文.txt"}
            result = bytearray()
            ftp.retrbinary("RETR hello.txt", result.extend)
            assert result == machines[0].files["C:\\hello.txt"]
            second = bytearray()
            other.retrbinary("RETR /C:/hello.txt", second.extend)
            assert second == machines[1].files["C:\\hello.txt"] and result != second
            payload = bytes(range(256)) * 300
            ftp.storbinary("STOR upload.bin", io.BytesIO(payload))
            result.clear()
            ftp.retrbinary("RETR upload.bin", result.extend)
            assert bytes(result) == payload
            ftp.rename("upload.bin", "renamed.bin")
            assert ftp.size("renamed.bin") == len(payload)
            ftp.delete("renamed.bin")
            ftp.mkd("testdir")
            ftp.cwd("testdir")
            ftp.cwd("..")
            ftp.rmd("testdir")
            try:
                ftp.retrbinary("RETR missing.txt", lambda _: None)
                raise AssertionError("missing file should fail")
            except ftplib.error_perm:
                pass
        print("PASS fixed target, simultaneous machine isolation, MLSD, Unicode names, binary get/put, rename, delete, mkdir/rmdir")
        for _ in range(2):
            try:
                with denied.ftp():
                    raise AssertionError("authentication should fail")
            except ftplib.error_perm as exc:
                assert str(exc).startswith("530"), exc
        with a.ftp() as ftp:
            assert ftp.pwd() == "/"
        print("PASS rejected VNC authentication and reconnect")

        if winscp:
            with tempfile.TemporaryDirectory(prefix="uvnc-integration-") as directory:
                directory = pathlib.Path(directory)
                (directory / "payload.bin").write_bytes(bytes(range(256)) * 300)
                script = directory / "commands.txt"
                script.write_text("\n".join([
                    "option batch abort", "option confirm off",
                    f"open ftp://ultravnc:testpass@127.0.0.1:{a.port}/ -passive=on",
                    "ls /", "cd /C:", "ls", f'get hello.txt "{directory / "hello.txt"}"',
                    f'get 中文.txt "{directory / "中文.txt"}"',
                    f'put -nopreservetime -resumesupport=off "{directory / "payload.bin"}" uploaded.bin',
                    "mv uploaded.bin renamed.bin", f'get renamed.bin "{directory / "roundtrip.bin"}"',
                    "rm renamed.bin", "mkdir via-winscp", "rmdir via-winscp", "exit",
                ]), encoding="utf-8-sig")
                result = subprocess.run([winscp, "/ini=nul", f"/script={script}", f"/log={directory / 'winscp.log'}"],
                                        capture_output=True, timeout=60)
                assert result.returncode == 0, result.stdout.decode(errors="replace") + (directory / "winscp.log").read_text(encoding="utf-8-sig")[-7000:]
                assert (directory / "hello.txt").read_bytes() == machines[0].files["C:\\hello.txt"]
                assert (directory / "中文.txt").read_bytes() == machines[0].files["C:\\中文.txt"]
                assert (directory / "roundtrip.bin").read_bytes() == (directory / "payload.bin").read_bytes()
            print("PASS stock WinSCP FTP engine against CLI adapter: browse/get/put/rename/delete/mkdir/rmdir")
        assert all(not machine.errors for machine in machines), [machine.errors for machine in machines]
    finally:
        for bridge in bridges:
            bridge.close()
        for machine in machines:
            machine.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--bridge", type=pathlib.Path)
    parser.add_argument("--winscp")
    args = parser.parse_args()
    command = [str(args.bridge.resolve())] if args.bridge else ["dotnet", str(ROOT / "bin/Release/net9.0/UvncFtpBridge.dll")]
    run(command, args.winscp)
