using System.Diagnostics;
using System.IO;
using System.Net;
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

            var needSsl = !IsSslBound(httpsPort, thumbprint);
            var needFw = !IsFirewallRuleExists("SC Japanese Assistant HTTPS", httpsPort);

            if (needSsl || needFw)
                RunSetupScript(httpsPort, thumbprint);

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

    private static void RunSetupScript(int port, string thumbprint)
    {
        var script = Path.Combine(Path.GetTempPath(), $"sc_ssl_setup_{Guid.NewGuid():N}.ps1");
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
netsh advfirewall firewall delete rule name=""SC Japanese Assistant HTTPS"" 2>$null
netsh advfirewall firewall add rule name=""SC Japanese Assistant HTTPS"" dir=in action=allow protocol=tcp localport={port}
";
        File.WriteAllText(script, ps);

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
            Debug.WriteLine($"[SSL] Setup script exit={proc?.ExitCode}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SSL] Setup script failed: {ex.Message}");
        }
        finally
        {
            try { File.Delete(script); } catch { }
        }
    }

    private static bool IsFirewallRuleExists(string ruleName, int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall show rule name=\"{ruleName}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Contains(port.ToString());
        }
        catch { return false; }
    }

    public static void EnsureFirewallRule(int httpPort)
    {
        if (IsFirewallRuleExists("SC Japanese Assistant HTTP", httpPort)) return;

        var script = Path.Combine(Path.GetTempPath(), $"sc_fw_setup_{Guid.NewGuid():N}.ps1");
        var ps = $@"
netsh advfirewall firewall delete rule name=""SC Japanese Assistant HTTP"" 2>$null
netsh advfirewall firewall add rule name=""SC Japanese Assistant HTTP"" dir=in action=allow protocol=tcp localport={httpPort}
netsh http delete urlacl url=http://+:{httpPort}/ 2>$null
netsh http add urlacl url=http://+:{httpPort}/ user=Everyone
";
        File.WriteAllText(script, ps);
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
            Debug.WriteLine($"[FW] HTTP firewall rule exit={proc?.ExitCode}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FW] HTTP firewall setup failed: {ex.Message}");
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
