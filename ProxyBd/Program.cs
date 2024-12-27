using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Http;
using Newtonsoft.Json;
using Microsoft.Win32;

class Program
{
    private static readonly ProxyServer proxyServer = new ProxyServer();
    private static ExplicitProxyEndPoint explicitEndPoint;
    private static HashSet<string> blockedSites = new HashSet<string>();

    static async Task Main(string[] args)
    {
        Console.WriteLine("Starting proxy server...");

        LoadBlockedSites("blocked_sites.json");

        proxyServer.BeforeRequest += OnRequest;
        proxyServer.BeforeResponse += OnResponse;

        proxyServer.CertificateManager.CreateRootCertificate(true);
        proxyServer.CertificateManager.TrustRootCertificate(true);

        explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 4444, true);

        proxyServer.AddEndPoint(explicitEndPoint);
        proxyServer.Start();

        SetSystemProxy("127.0.0.1", 4444);

        Console.WriteLine("Proxy server is running. Press Enter to stop.");
        Console.ReadLine();

        StopProxyServer();
    }

    private static void LoadBlockedSites(string filePath)
    {
        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                blockedSites = JsonConvert.DeserializeObject<HashSet<string>>(json);
                Console.WriteLine("Blocked sites loaded.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading blocked sites: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"Blocked sites file not found: {filePath}");
        }
    }

    private static async Task OnRequest(object sender, SessionEventArgs e)
    {
        var host = e.HttpClient.Request.RequestUri.Host;

        if (blockedSites.Contains(host))
        {
            Console.WriteLine($"Blocked request to: {host}");

            e.Ok("<html><body><h1>Access Denied</h1><p>This site is blocked.</p></body></html>");
            return;
        }

        await Task.CompletedTask;
    }

    private static async Task OnResponse(object sender, SessionEventArgs e)
    {
        await Task.CompletedTask;
    }

    private static void SetSystemProxy(string address, int port)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings"))
            {
                key.SetValue("ProxyEnable", 1);
                key.SetValue("ProxyServer", $"{address}:{port}");
            }

            Console.WriteLine("System proxy set.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting system proxy: {ex.Message}");
        }
    }

    private static void RemoveSystemProxy()
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings"))
            {
                key.SetValue("ProxyEnable", 0);
                key.DeleteValue("ProxyServer", false);
            }

            Console.WriteLine("System proxy removed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error removing system proxy: {ex.Message}");
        }
    }

    private static void StopProxyServer()
    {
        Console.WriteLine("Stopping proxy server...");

        RemoveSystemProxy();

        proxyServer.Stop();

        proxyServer.BeforeRequest -= OnRequest;
        proxyServer.BeforeResponse -= OnResponse;

        Console.WriteLine("Proxy server stopped.");
    }
}
