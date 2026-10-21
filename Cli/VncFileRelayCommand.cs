using System.Diagnostics;
using System.Security.Cryptography;
using UvncFtpBridge.Client;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Cli;

/// <summary>Experimental transport only: File-Tunnel owns TCP; OpenSSH owns SSH.</summary>
internal static class VncFileRelayCommand
{
    public static int Run(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("用法：--vnc-file-relay <機台> <機台專用資料夾> <本機專用資料夾>；密碼用 UVNC_PASSWORD。");
            return 2;
        }
        string? password = Environment.GetEnvironmentVariable("UVNC_PASSWORD");
        if (string.IsNullOrEmpty(password)) { Console.Error.WriteLine("請設定 UVNC_PASSWORD。"); return 2; }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            using var session = new RemoteSession(args[0], 5900, password, Socks5Proxy.FromEnvironment());
            string remote = RemotePath.Normalize(args[1]);
            string local = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(local);
            var ft = session.Ft;
            // Require a pre-created, dedicated folder. Never clear a caller's folder.
            ft.ListDirectory(remote);
            var sent = new Dictionary<string, string>();
            var received = new Dictionary<string, string>();
            byte[]? localMetadata = null, remoteMetadata = null;
            DateTime? remoteMetadataTime = null;
            byte[] Download(string name)
            {
                using var data = new MemoryStream();
                ft.Download(RemotePath.Combine(remote, name), data);
                return data.ToArray();
            }
            void Upload(string name, byte[] data)
            {
                string destination = RemotePath.Combine(remote, name);
                string staging = destination + ".relay-tmp";
                using var source = new MemoryStream(data, false);
                ft.Upload(staging, source, data.Length, DateTime.UtcNow);
                // Publish only complete files. ft may otherwise read a partial upload.
                ft.Rename(staging, destination);
            }
            static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
            var timer = Stopwatch.StartNew();
            long up = 0, down = 0;
            Console.Error.WriteLine("VNC 檔案搬運已啟動；兩端 ft 必須都用 --upload-download。Ctrl+C 結束。");
            while (!stop.IsCancellationRequested)
            {
                var listing = ft.ListDirectory(remote).Where(e => !e.IsDirectory).ToList();
                var entries = listing.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                string metadataPath = Path.Combine(local, "to-remote.dat");
                if (File.Exists(metadataPath))
                {
                    byte[] data = File.ReadAllBytes(metadataPath);
                    if (data.Length == 8 && (localMetadata == null || !data.SequenceEqual(localMetadata)))
                    {
                        if (entries.Contains("to-remote.dat")) ft.Delete(RemotePath.Combine(remote, "to-remote.dat"));
                        Upload("to-remote.dat", data); localMetadata = data;
                    }
                }
                var remoteMetadataEntry = listing.FirstOrDefault(e => e.Name.Equals("to-local.dat", StringComparison.OrdinalIgnoreCase));
                // Downloading it every pass doubled the VNC round trips; ft only rewrites it when it restarts.
                if (remoteMetadataEntry != null && remoteMetadataEntry.ModifiedUtc != remoteMetadataTime)
                {
                    byte[] data = Download("to-local.dat");
                    // A partial write is retried on the next pass.
                    if (data.Length == 8) remoteMetadataTime = remoteMetadataEntry.ModifiedUtc;
                    if (data.Length == 8 && (remoteMetadata == null || !data.SequenceEqual(remoteMetadata)))
                    {
                        File.WriteAllBytes(Path.Combine(local, "to-local.dat"), data); remoteMetadata = data;
                    }
                }
                for (int slot = 1; slot <= 5; slot++)
                {
                    string outgoing = $"to-remote.ft{slot}.dat";
                    string outgoingPath = Path.Combine(local, outgoing);
                    if (sent.TryGetValue(outgoing, out string? fingerprint))
                    {
                        if (File.Exists(outgoingPath) && Hash(File.ReadAllBytes(outgoingPath)) != fingerprint)
                        {
                            // ft can reuse a slot after its own timeout. Republish the new generation.
                            byte[] data = File.ReadAllBytes(outgoingPath);
                            if (data.Length >= 16)
                            {
                                if (entries.Contains(outgoing)) ft.Delete(RemotePath.Combine(remote, outgoing));
                                Upload(outgoing, data); sent[outgoing] = Hash(data); up += data.Length;
                            }
                        }
                        else if (!entries.Contains(outgoing))
                        {
                            // An old acknowledgement must never delete a reused slot.
                            if (File.Exists(outgoingPath) && Hash(File.ReadAllBytes(outgoingPath)) == fingerprint) File.Delete(outgoingPath);
                            sent.Remove(outgoing);
                        }
                    }
                    else if (File.Exists(outgoingPath) && !entries.Contains(outgoing))
                    {
                        byte[] data = File.ReadAllBytes(outgoingPath);
                        if (data.Length >= 16) { Upload(outgoing, data); sent[outgoing] = Hash(data); up += data.Length; }
                    }
                    string incoming = $"to-local.ft{slot}.dat";
                    string incomingPath = Path.Combine(local, incoming);
                    if (received.TryGetValue(incoming, out fingerprint))
                    {
                        if (!File.Exists(incomingPath))
                        {
                            if (entries.Contains(incoming) && Hash(Download(incoming)) == fingerprint) ft.Delete(RemotePath.Combine(remote, incoming));
                            received.Remove(incoming);
                        }
                    }
                    else if (entries.Contains(incoming) && !File.Exists(incomingPath))
                    {
                        byte[] data = Download(incoming);
                        if (data.Length >= 16)
                        {
                            string staging = incomingPath + ".relay-tmp";
                            File.WriteAllBytes(staging, data); File.Move(staging, incomingPath);
                            received[incoming] = Hash(data); down += data.Length;
                        }
                    }
                }
                if (timer.ElapsedMilliseconds >= 10000)
                {
                    // Lease is consumed by the remote bootstrap, which stops only its own ft child.
                    using var lease = new MemoryStream(new byte[] { 1 });
                    ft.Upload(RemotePath.Combine(remote, "lease"), lease, 1, DateTime.UtcNow);
                    // stderr reaches the interactive SSH window; keep the counter for -v only.
                    Log.Debug($"搬運累計：上傳 {up} B，下載 {down} B"); timer.Restart();
                }
                stop.Token.WaitHandle.WaitOne(50);
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("檔案搬運停止：" + ex.Message); return 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
