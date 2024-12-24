using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;

class ProxyServer
{
    private static HashSet<string> blockedSites = new HashSet<string>();

    public static void Main(string[] args)
    {
        ConfigureSystemProxy();
        LoadBlockedSites("blocked_sites.json");

        Console.WriteLine("Proxy server is running on port 8888...");

        TcpListener listener = new TcpListener(IPAddress.Any, 8888);
        listener.Start();

        while (true)
        {
            try
            {
                TcpClient client = listener.AcceptTcpClient();
                Thread thread = new Thread(() => HandleClient(client));
                thread.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }

    private static void HandleConnect(NetworkStream clientStream, string target)
    {
        try
        {
            string[] targetParts = target.Split(':');
            string host = targetParts[0];
            int port = targetParts.Length > 1 ? int.Parse(targetParts[1]) : 443;

            using (TcpClient serverClient = new TcpClient(host, port))
            using (NetworkStream serverStream = serverClient.GetStream())
            {
                // Отправляем клиенту ответ об успешном создании туннеля
                string response = "HTTP/1.1 200 Connection Established\r\n\r\n";
                byte[] responseBytes = Encoding.ASCII.GetBytes(response);
                clientStream.Write(responseBytes, 0, responseBytes.Length);

                // Перенаправляем данные между клиентом и сервером
                ForwardData(clientStream, serverStream);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling CONNECT: {ex.Message}");
        }
    }
    private static void ForwardData(NetworkStream clientStream, NetworkStream serverStream)
    {
        try
        {
            using (var clientToServer = new ManualResetEvent(false))
            using (var serverToClient = new ManualResetEvent(false))
            {
                // Потоки для перенаправления данных
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        clientStream.CopyTo(serverStream);
                    }
                    catch { }
                    finally { clientToServer.Set(); }
                });

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        serverStream.CopyTo(clientStream);
                    }
                    catch { }
                    finally { serverToClient.Set(); }
                });

                // Ожидаем завершения потоков
                WaitHandle.WaitAll(new[] { clientToServer, serverToClient });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error forwarding data: {ex.Message}");
        }
    }
    private static void HandleClient(TcpClient client)
    {
        try
        {
            using (NetworkStream clientStream = client.GetStream())
            {
                byte[] buffer = new byte[8192];
                int bytesRead = clientStream.Read(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    string request = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                    string[] requestLines = request.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

                    // Определяем метод запроса (CONNECT или GET/POST)
                    string[] requestLineParts = requestLines[0].Split(' ');
                    if (requestLineParts.Length < 2)
                    {
                        Console.WriteLine("Invalid request.");
                        return;
                    }

                    string method = requestLineParts[0];
                    string target = requestLineParts[1];

                    if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                    {
                        HandleConnect(clientStream, target);
                    }
                    else
                    {
                        string host = GetHostFromRequest(request);

                        if (IsBlocked(host))
                        {
                            Console.WriteLine($"Blocked request to: {host}");
                            SendBlockedResponse(clientStream);
                            return;
                        }

                        Console.WriteLine($"Forwarding request to: {host}");
                        ForwardRequest(clientStream, buffer, bytesRead);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Client handling error: {ex.Message}");
        }
        finally
        {
            client.Close();
        }
    }

    private static string GetHostFromRequest(string request)
    {
        string[] lines = request.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            if (line.StartsWith("Host: "))
            {
                return line.Substring(6).Trim().Split(':')[0];
            }
        }
        return string.Empty;
    }

    private static bool IsBlocked(string host)
    {
        return blockedSites.Contains(host);
    }

    private static void SendBlockedResponse(NetworkStream stream)
    {
        string response = "HTTP/1.1 403 Forbidden\r\n" +
                          "Content-Type: text/html\r\n" +
                          "Content-Length: 23\r\n\r\n" +
                          "<h1>403 Forbidden</h1>";
        byte[] responseBytes = Encoding.ASCII.GetBytes(response);
        stream.Write(responseBytes, 0, responseBytes.Length);
    }

    private static void ConfigureSystemProxy()
    {
        try
        {
            Console.WriteLine("Configuring system proxy...");
        
            // Запуск команды PowerShell для установки прокси 
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "Set-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings' -Name ProxyEnable -Value 1; " +
                            "Set-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings' -Name ProxyServer -Value '127.0.0.1:8888';",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process process = Process.Start(psi))
            {
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    Console.WriteLine("System proxy configured successfully.");
                }
                else
                {
                    Console.WriteLine("Failed to configure system proxy.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error configuring system proxy: {ex.Message}");
        }
    }
    private static void ForwardRequest(NetworkStream clientStream, byte[] clientRequest, int bytesRead)
    {
        try
        {
            string[] requestLines = Encoding.ASCII.GetString(clientRequest, 0, bytesRead).Split("\r\n");
            string hostLine = Array.Find(requestLines, line => line.StartsWith("Host: "));
        
            if (hostLine == null)
            {
                Console.WriteLine("Host header not found in request.");
                return;
            }
        
            string host = hostLine.Substring(6).Trim(); 
            int port = 443; 
            if (host.Contains(":"))
            {
                string[] hostParts = host.Split(':');
                host = hostParts[0];
                port = int.Parse(hostParts[1]);
            }

            using (TcpClient serverClient = new TcpClient(host, port))
            using (NetworkStream serverStream = serverClient.GetStream())
            {
                serverStream.Write(clientRequest, 0, bytesRead);

                byte[] serverBuffer = new byte[8192];
                int serverBytesRead;

                while ((serverBytesRead = serverStream.Read(serverBuffer, 0, serverBuffer.Length)) > 0)
                {
                    clientStream.Write(serverBuffer, 0, serverBytesRead);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error forwarding request: {ex.Message}");
        }
    }

    private static void LoadBlockedSites(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine("Blocked sites file not found. Creating a default one.");
            File.WriteAllText(filePath, JsonSerializer.Serialize(new List<string> { "example.com", "blockedsite.com" }));
        }

        string json = File.ReadAllText(filePath);
        blockedSites = JsonSerializer.Deserialize<HashSet<string>>(json);
    }
}
