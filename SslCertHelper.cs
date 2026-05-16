using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace StarCitizenJapaneseTextCreater;

public static class SslCertHelper
{
    private const string CertSubject = "CN=SC Japanese Assistant";
    private static readonly Guid AppId = new("F8A7B3C1-2D4E-4F56-A1B2-C3D4E5F6A7B8");
    private const string PfxPassword = "sc-assistant-local";

    private static string PfxPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SCJapaneseAssistant", "server.pfx");

    private static string CerPath => Path.ChangeExtension(PfxPath, ".cer");

    public static string? EnsureCertificateAndBind(int httpsPort)
    {
        try
        {
            var dir = Path.GetDirectoryName(PfxPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string thumbprint;

            if (File.Exists(PfxPath))
            {
                using var existing = new X509Certificate2(PfxPath, PfxPassword);
                if (existing.NotAfter < DateTime.Now.AddDays(30))
                {
                    File.Delete(PfxPath);
                    thumbprint = CreateCertFiles();
                }
                else
                {
                    thumbprint = existing.Thumbprint;
                }
            }
            else
            {
                thumbprint = CreateCertFiles();
            }

            if (!IsSslBound(httpsPort, thumbprint))
                RunSslSetupScript(httpsPort, thumbprint);

            return thumbprint;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SSL] Setup failed: {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    private static string CreateCertFiles()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest(CertSubject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (var ip in GetLocalIps())
            sanBuilder.AddIpAddress(ip);
        req.CertificateExtensions.Add(sanBuilder.Build());

        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
        var thumbprint = cert.Thumbprint;

        File.WriteAllBytes(PfxPath, cert.Export(X509ContentType.Pfx, PfxPassword));
        File.WriteAllBytes(CerPath, cert.Export(X509ContentType.Cert));

        Debug.WriteLine($"[SSL] Certificate created: {thumbprint}");
        Debug.WriteLine($"[SSL] PFX: {PfxPath}");
        Debug.WriteLine($"[SSL] CER: {CerPath}");
        return thumbprint;
    }

    private static bool IsSslBound(int port, string thumbprint)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"http show sslcert ipport=0.0.0.0:{port}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output.Contains(thumbprint, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void RunSslSetupScript(int port, string thumbprint)
    {
        var ps = $@"
$pfx = '{PfxPath.Replace("'", "''")}'
$pass = ConvertTo-SecureString '{PfxPassword}' -AsPlainText -Force
Import-PfxCertificate -FilePath $pfx -CertStoreLocation Cert:\LocalMachine\My -Password $pass | Out-Null
$cer = '{CerPath.Replace("'", "''")}'
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
netsh http delete sslcert ipport=0.0.0.0:{port} 2>$null
netsh http add sslcert ipport=0.0.0.0:{port} certhash={thumbprint} appid=""{{{AppId}}}""
netsh http delete urlacl url=https://+:{port}/ 2>$null
netsh http add urlacl url=https://+:{port}/ user=Everyone
";
        RunElevatedScript(ps, "SSL");
    }

    /// <summary>
    /// HTTP/HTTPS 両ポートのファイアウォールルールと urlacl を一括管理。
    /// 既存ルールのポートと一致していれば何もしない (UAC なし)。
    /// ポートが変更された場合、旧ルール削除→新ルール追加を 1 回の UAC で実行。
    /// </summary>
    public static void EnsureFirewallRules(int httpPort, int httpsPort)
    {
        const string httpRule = "SC Japanese Assistant HTTP";
        const string httpsRule = "SC Japanese Assistant HTTPS";

        var currentHttp = GetFirewallRulePort(httpRule);
        var currentHttps = GetFirewallRulePort(httpsRule);

        var httpOk = currentHttp == httpPort;
        var httpsOk = currentHttps == httpsPort;

        if (httpOk && httpsOk)
        {
            Debug.WriteLine($"[FW] Rules already correct: HTTP={httpPort}, HTTPS={httpsPort}");
            return;
        }

        var sb = new StringBuilder();

        // HTTP ファイアウォール + urlacl
        if (!httpOk)
        {
            Debug.WriteLine($"[FW] HTTP rule change: {currentHttp} -> {httpPort}");
            sb.AppendLine($"netsh advfirewall firewall delete rule name=\"{httpRule}\" 2>$null");
            sb.AppendLine($"netsh advfirewall firewall add rule name=\"{httpRule}\" dir=in action=allow protocol=tcp localport={httpPort}");
            if (currentHttp > 0)
                sb.AppendLine($"netsh http delete urlacl url=http://+:{currentHttp}/ 2>$null");
            sb.AppendLine($"netsh http delete urlacl url=http://+:{httpPort}/ 2>$null");
            sb.AppendLine($"netsh http add urlacl url=http://+:{httpPort}/ user=Everyone");
        }

        // HTTPS ファイアウォール + urlacl
        if (!httpsOk)
        {
            Debug.WriteLine($"[FW] HTTPS rule change: {currentHttps} -> {httpsPort}");
            sb.AppendLine($"netsh advfirewall firewall delete rule name=\"{httpsRule}\" 2>$null");
            sb.AppendLine($"netsh advfirewall firewall add rule name=\"{httpsRule}\" dir=in action=allow protocol=tcp localport={httpsPort}");
            if (currentHttps > 0)
                sb.AppendLine($"netsh http delete urlacl url=https://+:{currentHttps}/ 2>$null");
            sb.AppendLine($"netsh http delete urlacl url=https://+:{httpsPort}/ 2>$null");
            sb.AppendLine($"netsh http add urlacl url=https://+:{httpsPort}/ user=Everyone");
        }

        RunElevatedScript(sb.ToString(), "FW");
    }

    /// <summary>
    /// ファイアウォールルールから現在登録されているポート番号を取得。
    /// ルールが存在しない場合は 0 を返す。
    /// </summary>
    private static int GetFirewallRulePort(string ruleName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall show rule name=\"{ruleName}\" verbose",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            // "LocalPort:                            8099" のような行からポートを抽出
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("LocalPort", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.Split(':');
                    if (parts.Length >= 2 && int.TryParse(parts[^1].Trim(), out var port))
                        return port;
                }
            }
        }
        catch { }
        return 0;
    }

    private static void RunElevatedScript(string psCommands, string tag)
    {
        var script = Path.Combine(Path.GetTempPath(), $"sc_{tag.ToLower()}_{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, psCommands);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(30000);
            Debug.WriteLine($"[{tag}] Script exit={proc?.ExitCode}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{tag}] Script failed: {ex.Message}");
        }
        finally
        {
            try { File.Delete(script); } catch { }
        }
    }

    public static byte[]? ExportPublicCertBytes()
    {
        if (File.Exists(CerPath))
            return File.ReadAllBytes(CerPath);
        return null;
    }

    private static IPAddress[] GetLocalIps()
    {
        var ips = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        ips.Add(addr.Address);
                }
            }
        }
        catch { }
        return ips.ToArray();
    }
}
