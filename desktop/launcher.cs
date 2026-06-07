using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

class PdfSignerLauncher
{
    static readonly byte[] Page = ReadResource("app.html");

    static byte[] ReadResource(string name)
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        string[] names = asm.GetManifestResourceNames();
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].EndsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                using (Stream stream = asm.GetManifestResourceStream(names[i]))
                using (MemoryStream ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }
        throw new InvalidOperationException("Embedded app.html was not found.");
    }

    static void Main()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string url = "http://127.0.0.1:" + port + "/";

        Console.WriteLine("============================================");
        Console.WriteLine("  PDF signer is running");
        Console.WriteLine("============================================");
        Console.WriteLine("  URL: " + url);
        Console.WriteLine("  Close this window to stop.");
        Console.WriteLine("============================================");

        Thread browser = new Thread(delegate()
        {
            Thread.Sleep(400);
            try
            {
                Process.Start(url);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to open browser: " + ex.Message);
            }
        });
        browser.IsBackground = true;
        browser.Start();

        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            Thread worker = new Thread(delegate() { HandleClient(client); });
            worker.IsBackground = true;
            worker.Start();
        }
    }

    static void HandleClient(TcpClient client)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        using (StreamReader reader = new StreamReader(stream, Encoding.ASCII))
        {
            string request = reader.ReadLine() ?? "";
            string[] parts = request.Split(' ');
            string path = parts.Length > 1 ? parts[1] : "/";

            string line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine())) { }

            if (path == "/ping")
            {
                byte[] body = Encoding.UTF8.GetBytes("ok");
                WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", body);
            }
            else if (path == "/" || path.StartsWith("/?", StringComparison.Ordinal))
            {
                WriteResponse(stream, "200 OK", "text/html; charset=utf-8", Page);
            }
            else
            {
                byte[] body = Encoding.UTF8.GetBytes("Not found");
                WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", body);
            }
        }
    }

    static void WriteResponse(Stream stream, string status, string contentType, byte[] body)
    {
        string header =
            "HTTP/1.1 " + status + "\r\n" +
            "Content-Type: " + contentType + "\r\n" +
            "Content-Length: " + body.Length + "\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(body, 0, body.Length);
    }
}
