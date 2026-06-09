using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class PdfSignerLauncher
{
    static readonly byte[] Page = ReadResource("app.html");
    static readonly object ConvLock = new object();   // PowerPoint 변환은 한 번에 하나만

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

        // PDFSIGNER_NO_BROWSER=1 이면 자동 브라우저 실행을 끈다(테스트/자동화용).
        if (Environment.GetEnvironmentVariable("PDFSIGNER_NO_BROWSER") != "1")
        {
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
        }

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
        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                // ---- 요청 헤더를 \r\n\r\n 까지 바이트 단위로 읽는다(바디는 바이너리라 StreamReader 금지) ----
                List<byte> head = new List<byte>(1024);
                int b;
                while ((b = stream.ReadByte()) != -1)
                {
                    head.Add((byte)b);
                    int n = head.Count;
                    if (n >= 4 && head[n - 4] == 13 && head[n - 3] == 10 && head[n - 2] == 13 && head[n - 1] == 10) break;
                    if (n > 65536) break;   // 헤더 과대 방지
                }
                string headerText = Encoding.ASCII.GetString(head.ToArray());
                string[] lines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                string[] rp = (lines.Length > 0 ? lines[0] : "").Split(' ');
                string method = rp.Length > 0 ? rp[0] : "";
                string path = rp.Length > 1 ? rp[1] : "/";

                int contentLength = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    int c = lines[i].IndexOf(':');
                    if (c <= 0) continue;
                    string key = lines[i].Substring(0, c).Trim();
                    string val = lines[i].Substring(c + 1).Trim();
                    if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(val, out contentLength);
                }

                // ---- 바디(있으면) 읽기 ----
                byte[] body = new byte[0];
                if (contentLength > 0 && contentLength <= 300 * 1024 * 1024)
                {
                    body = new byte[contentLength];
                    int read = 0;
                    while (read < contentLength)
                    {
                        int got = stream.Read(body, read, contentLength - read);
                        if (got <= 0) break;
                        read += got;
                    }
                    if (read != contentLength) body = new byte[0];
                }

                // ---- 라우팅 ----
                if (method == "POST" && path == "/convert-pptx")
                {
                    if (body.Length == 0)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("empty-body"));
                        return;
                    }
                    try
                    {
                        byte[] pdf;
                        lock (ConvLock) { pdf = ConvertPptxToPdf(body); }
                        WriteResponse(stream, "200 OK", "application/pdf", pdf);
                    }
                    catch (PowerPointMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-powerpoint"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("convert-failed: " + ex.Message));
                    }
                }
                else if (path == "/ping")
                {
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (path == "/can-convert")
                {
                    // 변환 백엔드(PowerPoint) 사용 가능 여부를 빠르게 알려준다(앱이 미리 분기 가능)
                    bool ok = Type.GetTypeFromProgID("PowerPoint.Application") != null;
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ok ? "yes" : "no"));
                }
                else if (method == "GET" && (path == "/" || path.StartsWith("/?", StringComparison.Ordinal)))
                {
                    WriteResponse(stream, "200 OK", "text/html; charset=utf-8", Page);
                }
                else
                {
                    WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"));
                }
            }
        }
        catch { /* 연결 오류는 무시 */ }
    }

    static void WriteResponse(Stream stream, string status, string contentType, byte[] body)
    {
        string header =
            "HTTP/1.1 " + status + "\r\n" +
            "Content-Type: " + contentType + "\r\n" +
            "Content-Length: " + body.Length + "\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(body, 0, body.Length);
    }

    // ===== PPTX → PDF 변환 (설치된 PowerPoint 를 late-bound COM 으로 구동) =====
    // STA 스레드에서 실행하고, 윈도우 없이(WithWindow=false) 열어 PDF 로 저장한다.
    static byte[] ConvertPptxToPdf(byte[] pptx)
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "moida_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        string inPath = Path.Combine(tmpDir, "in.pptx");
        string outPath = Path.Combine(tmpDir, "out.pdf");
        File.WriteAllBytes(inPath, pptx);

        byte[] result = null;
        Exception err = null;
        Thread t = new Thread(delegate()
        {
            try { result = RunPowerPointExport(inPath, outPath); }
            catch (Exception ex) { err = ex; }
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        bool finished = t.Join(180000);   // 최대 3분(대형 덱 + PowerPoint 기동 여유)

        try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }

        if (!finished) throw new Exception("timeout");
        if (err != null) throw err;
        if (result == null || result.Length == 0) throw new Exception("empty-pdf");
        return result;
    }

    static byte[] RunPowerPointExport(string inPath, string outPath)
    {
        Type pptType = Type.GetTypeFromProgID("PowerPoint.Application");
        if (pptType == null) throw new PowerPointMissingException();

        object app = null, presentations = null, pres = null;
        try
        {
            app = Activator.CreateInstance(pptType);
            TrySet(app, "DisplayAlerts", 1);        // ppAlertsNone — 대화상자 억제
            TrySet(app, "AutomationSecurity", 3);   // msoAutomationSecurityForceDisable — 매크로 차단
            presentations = Get(app, "Presentations");
            // Open(FileName, ReadOnly=msoTrue(-1), Untitled=msoFalse(0), WithWindow=msoFalse(0))
            pres = Invoke(presentations, "Open", new object[] { inPath, -1, 0, 0 });
            // SaveAs(FileName, ppSaveAsPDF=32, EmbedTrueTypeFonts=msoFalse(0))
            Invoke(pres, "SaveAs", new object[] { outPath, 32, 0 });
            Invoke(pres, "Close", null);
        }
        finally
        {
            if (pres != null) { try { Marshal.ReleaseComObject(pres); } catch { } }
            if (presentations != null) { try { Marshal.ReleaseComObject(presentations); } catch { } }
            if (app != null)
            {
                try { Invoke(app, "Quit", null); } catch { }
                try { Marshal.ReleaseComObject(app); } catch { }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        if (!File.Exists(outPath)) throw new Exception("no-output");
        return File.ReadAllBytes(outPath);
    }

    static object Get(object o, string name)
    {
        return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);
    }
    static object Invoke(object o, string name, object[] args)
    {
        return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
    }
    static void TrySet(object o, string name, object val)
    {
        try { o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new object[] { val }); }
        catch { }
    }
}

class PowerPointMissingException : Exception { }
