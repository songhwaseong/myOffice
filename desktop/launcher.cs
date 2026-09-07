using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

class ClassDockLauncher
{
    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int dwProcessId);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")]
    static extern uint GetOEMCP();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHBrowseForFolderW")]
    static extern IntPtr SHBrowseForFolder(ref BROWSEINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetPathFromIDListW")]
    static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder path);
    [DllImport("ole32.dll")]
    static extern void CoTaskMemFree(IntPtr ptr);

    // ── 프로세스 트리(자기 자신 + 파이썬 커널·드라이버 등 자식) 메모리 측정용 ──
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
    const uint TH32CS_SNAPPROCESS = 0x00000002;
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    static readonly string LocalAuthToken = CreateLocalAuthToken();
    static readonly byte[] Page = InjectLocalAuthToken(ReadResource("app.html"));
    static readonly byte[] PythonKernelRunner = ReadResource("python_kernel.py");
    static readonly byte[] DbWorkerRunner = ReadResource("db_worker.py");
    static readonly byte[] NpmPackageRunner = ReadResource("npm_package_runner.js");
    static readonly object ConvLock = new object();   // PowerPoint 변환은 한 번에 하나만
    static readonly object MediaConvLock = new object();   // ffmpeg 영상 변환도 한 번에 하나만
    // 경로 방식 변환 작업표. 파일을 통째로 주고받는 /convert-media 와 달리 한 요청 안에서
    // 끝내지 않고(몇 십 분짜리 변환도 있다) 진행률을 폴링으로 보여 준다.
    static readonly object MediaJobLock = new object();
    static readonly Dictionary<string, MediaConvertJob> MediaJobs = new Dictionary<string, MediaConvertJob>(StringComparer.Ordinal);
    const int MediaJobMax = 64;
    // <video src> 는 fetch 가 아니라 X-ClassDock-Token 헤더를 붙일 수 없다. 그래서 앱이 먼저
    // 토큰으로 표(ticket)를 받아 그 표를 주소에 담아 재생한다. 표 하나는 파일 하나에만 쓴다.
    static readonly object MediaTicketLock = new object();
    static readonly Dictionary<string, MediaTicket> MediaTickets = new Dictionary<string, MediaTicket>(StringComparer.Ordinal);
    const int MediaTicketMax = 512;
    static readonly TimeSpan MediaTicketLifetime = TimeSpan.FromHours(12);
    static readonly object FfmpegProbeLock = new object();
    static string _ffmpegCmd = null;                   // 찾은 ffmpeg 경로 캐시(없으면 매번 재탐색 — 나중에 놓아도 인식)
    static readonly object FfmpegInstallLock = new object();
    static volatile string _ffInstallState = "idle";   // idle | downloading | extracting | done | error
    static long _ffInstallReceived = 0;                // 내려받은 바이트(진행률 표시용 — 근사값이면 충분)
    static long _ffInstallTotal = 0;
    static volatile string _ffInstallError = "";
    static readonly object WorkspaceLock = new object();
    static readonly string WorkspacePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassDock", "workspace.bin");
    // 브라우저 origin(포트) 변경과 무관하게 유지할 설정 저장소(localStorage 스냅샷 JSON).
    // 런처가 다른 포트로 떠도 테마·자동복원·탭 순서 등이 초기화되지 않도록 서버측 원본으로 삼는다.
    static readonly string AppStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassDock", "app-state.json");
    // 브라우저 화면이 멈추거나 비정상 종료된 뒤에도 다음 실행에서 원인을 볼 수 있는 공통 진단 로그.
    // 문서 본문은 브라우저 로거에서 제외하고, 런처는 크기 제한·순환 보관만 맡는다.
    static readonly string DiagnosticsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassDock", "logs");
    static readonly string DiagnosticsLogPath = Path.Combine(DiagnosticsDir, "events.jsonl");
    static readonly string DiagnosticsSessionPath = Path.Combine(DiagnosticsDir, "session.json");
    static readonly object DiagnosticsLock = new object();
    const int DiagnosticsEventMaxBytes = 32 * 1024;
    const int DiagnosticsSessionMaxBytes = 16 * 1024;
    const long DiagnosticsLogMaxBytes = 4L * 1024 * 1024;
    const int DiagnosticsArchiveCount = 2;
    static readonly string NpmPackageCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassDock", "js-npm-packages");
    static readonly string NpmPackageRunnerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassDock", "npm-package-runner.js");
    static readonly object AppStateLock = new object();
    const int AppStateMaxBytes = 8 * 1024 * 1024;
    const int MaxHttpHeaderBytes = 64 * 1024;
    const int MaxHttpRequestBodyBytes = 1034 * 1024 * 1024;
    // 일반적인 수업용 데이터 분석은 허용하면서, 실수로 큰 배열을 반복 생성해 PC 전체가 멈추는 일을 줄인다.
    const long PythonProcessMemoryLimitBytes = 4096L * 1024 * 1024;
    // 지속형 노트북 커널은 프로세스가 살아 있어 일반 실행의 WaitForExit 제한을 타지 않는다.
    // 셀 하나가 무한 실행되는 상황을 막되, 데이터 분석 셀은 일반 스크립트보다 길 수 있어 10분을 허용한다.
    const int PythonKernelExecutionTimeoutMs = 10 * 60 * 1000;
    // 직전 인스턴스가 실제로 바인딩한 포트. 다음 실행이 후보 포트 전체를 HTTP 로 뒤지지 않고 이 한 곳만 확인해
    // 단일 인스턴스 여부를 빠르게 판단하도록 기록한다(기동 지연 방지).
    static readonly string InstancePortPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassDock", "instance-port.txt");
    // 포트 파일 확인 전에 두 프로세스가 동시에 기동하는 경쟁을 막는다. 뮤텍스는 프로세스가
    // 강제 종료되어도 OS가 자동으로 해제하므로 별도의 종료 정리가 필요 없다.
    const string SingleInstanceMutexName = @"Local\ClassDock_SingleInstance";
    static Mutex SingleInstanceMutex;
    // 앱 모드(탭·주소창 없는 --app 창)로 열지 여부. 브라우저는 앱 화면이 뜨기 전에 실행되므로
    // 이 설정만은 localStorage 가 아니라 런처가 기동 중 읽을 수 있는 파일에 둔다. 값은 "1" 또는 "0".
    static readonly string AppModeConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassDock", "app-mode.txt");
    // 실행 중인 서버의 주소. 설정의 '지금 앱 모드로 열기'가 같은 origin 으로 새 창을 띄울 때 쓴다.
    static string ServerUrl = "";
    // 편집한 코드를 브라우저 권한 팝업 없이 바로 저장하는 폴더. 사용자가 바꾸지 않으면 내 문서\ClassDock 저장.
    static readonly string DefaultSaveRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "ClassDock 저장");
    static readonly string SaveRootConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassDock", "save-root.txt");
    static readonly object SaveRootLock = new object();
    static string SaveRoot = LoadSaveRoot();
    static readonly object ImageMemoLock = new object();
    static readonly object SaveRootPickerLock = new object();
    static string SaveRootPickerState = "idle";
    static string SaveRootPickerResult = "";
    // EXE의 '폴더 열기'는 브라우저 File System Access API 대신 Windows 폴더 선택창을 사용한다.
    // 브라우저 API가 숨기는 드라이브 포함 절대경로를 터미널 작업폴더로 전달하면서도,
    // 선택한 루트 밖의 파일에는 접근할 수 없도록 실행 중 발급한 ID로만 후속 요청을 받는다.
    static readonly object SourceFolderLock = new object();
    static readonly Dictionary<string, string> SourceFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static readonly string SourceFolderConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassDock", "source-folders.txt");
    static readonly object SourceFolderPickerLock = new object();
    static string SourceFolderPickerState = "idle";
    static string SourceFolderPickerResult = "";
    static string SourceFolderPickerId = "";
    // 오프라인 실행용으로 번들된 Pyodide 코어 폴더(exe 옆 vendor/pyodide/). tools/download-pyodide.js 로 채운다.
    static readonly string PyodideDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vendor", "pyodide");
    // 브라우저의 WORKSPACE_CAP(state.js)과 반드시 같은 값이어야 한다. 저장은 병합(replace=0)이라
    // 한 번의 요청이 작아도 누적 파일은 커질 수 있는데, 복원 쪽이 이 상한을 넘는 기록은 통째로
    // 지워 버리기 때문이다. 여기서 막으면 저장 시점에 "너무 큼" 안내가 뜬다.
    // 이 크기를 감당할 수 있는 것은 병합·삭제가 RewriteWorkspace 로 흘려 쓰기 때문이다.
    const int WorkspaceMaxBytes = 512 * 1024 * 1024;

    // 학생 코드의 무한 print가 서버 메모리와 폴링 응답을 계속 키우지 않도록 앞 4MB까지만 보관한다.
    // 진단·채점·단계 실행은 stdout 끝의 전용 마커 뒤 JSON을 사용하므로 그 구간만 별도 6MB까지 보존한다.
    class LimitedTextBuffer
    {
        const int HeadLimit = 4 * 1024 * 1024;
        const int ProtocolLimit = 6 * 1024 * 1024;
        const string Notice = "\n\n[출력이 4MB를 넘어 이후 내용은 생략했습니다. 실행은 계속됩니다.]\n";
        static readonly string[] Markers = { "__CLASSDOCK_DIAG__", "__CLASSDOCK_GRADE__", "__CLASSDOCK_TRACE__" };
        readonly object Sync = new object();
        readonly StringBuilder Head = new StringBuilder();
        readonly StringBuilder Protocol = new StringBuilder();
        string ScanTail = "";
        bool CapturingProtocol;
        bool Truncated;

        public void Append(char[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) return;
            Append(new string(buffer, offset, count));
        }

        public void Append(string value)
        {
            string text = value ?? "";
            if (text.Length == 0) return;
            lock (Sync)
            {
                string combined = ScanTail + text;
                int markerAt = -1;
                for (int i = 0; i < Markers.Length; i++)
                    markerAt = Math.Max(markerAt, combined.LastIndexOf(Markers[i], StringComparison.Ordinal));
                if (markerAt >= 0)
                {
                    CapturingProtocol = true;
                    Protocol.Length = 0;
                    AppendProtocol(combined.Substring(markerAt));
                }
                else if (CapturingProtocol) AppendProtocol(text);

                int scanSize = 0;
                for (int i = 0; i < Markers.Length; i++) scanSize = Math.Max(scanSize, Markers[i].Length - 1);
                ScanTail = combined.Length > scanSize ? combined.Substring(combined.Length - scanSize) : combined;

                int remaining = HeadLimit - Head.Length;
                if (remaining > 0) Head.Append(text, 0, Math.Min(remaining, text.Length));
                if (text.Length > Math.Max(0, remaining)) Truncated = true;
            }
        }

        void AppendProtocol(string text)
        {
            int remaining = ProtocolLimit - Protocol.Length;
            if (remaining > 0) Protocol.Append(text, 0, Math.Min(remaining, text.Length));
        }

        public void AppendLine(string value)
        {
            Append((value ?? "") + Environment.NewLine);
        }

        public string GetText()
        {
            lock (Sync)
            {
                if (!Truncated) return Head.ToString();
                return Head.ToString() + Notice + (Protocol.Length > 0 ? Protocol.ToString() : "");
            }
        }

        // GetText() 결과의 길이만 문자열 생성 없이 계산 — 폴링의 "변경 없음" 판정용.
        // 버퍼는 덧붙이기 전용이라 길이가 같으면 내용도 같다.
        public int TextLength
        {
            get
            {
                lock (Sync)
                {
                    if (!Truncated) return Head.Length;
                    return Head.Length + Notice.Length + Protocol.Length;
                }
            }
        }

        // GetText() 논리 문자열(Head + [Notice + Protocol])에서 from 이후의 새 내용만 만든다.
        // 폴링마다 누적 출력 전체(최대 1MB+)를 복사·전송하지 않기 위한 증분 응답용.
        public string GetTextFrom(int from)
        {
            lock (Sync)
            {
                if (from <= 0) return GetText();
                StringBuilder sb = new StringBuilder();
                int headLen = Head.Length;
                if (from < headLen) sb.Append(Head.ToString(from, headLen - from));
                if (Truncated)
                {
                    int f2 = Math.Max(from - headLen, 0);
                    if (f2 < Notice.Length) sb.Append(Notice.Substring(f2));
                    int f3 = Math.Max(f2 - Notice.Length, 0);
                    if (f3 < Protocol.Length) sb.Append(Protocol.ToString(f3, Protocol.Length - f3));
                }
                return sb.ToString();
            }
        }
    }

    class PythonSession
    {
        public string Id;
        public Process Process;
        public readonly object Sync = new object();
        public readonly LimitedTextBuffer Stdout = new LimitedTextBuffer();
        public readonly LimitedTextBuffer Stderr = new LimitedTextBuffer();
        public bool Complete;
        public int ExitCode = -1;
        public string ImagesJson = "[]";
        public string VariablesJson = "[]";
        public string RunnerPath;
        public string PlotDir;
        public string TempRoot;
        public Dictionary<string, long> InitSize = new Dictionary<string, long>();   // 실행 전 입력 파일 크기
        public Dictionary<string, long> InitMtime = new Dictionary<string, long>();  // 실행 전 입력 파일 수정시각(Ticks)
        public string OutputsJson = "[]";                                            // 실행이 만든/바꾼 파일 목록
        public DateTime DoneAt = DateTime.MaxValue;                                   // 완료 시각(보존 정리용)
        public readonly List<int[]> Echoes = new List<int[]>();                       // stdout 속 입력 에코 구간 [시작,길이] — 프런트가 입력값만 다른 색으로 표시
    }

    static readonly object PySessionsLock = new object();
    static readonly Dictionary<string, PythonSession> PySessions = new Dictionary<string, PythonSession>();

    /* 자바 실행 1건. 흐름(출력 버퍼·입력 에코·완료 감시)은 파이썬 세션과 같지만 파이썬 전용 필드
       (그림·변수·출력 파일)는 자바에 해당이 없어 따로 둔다 — 억지로 공유하면 죽은 필드만 남는다. */
    class JavaSession
    {
        public string Id;
        public Process Process;
        public readonly object Sync = new object();
        public readonly LimitedTextBuffer Stdout = new LimitedTextBuffer();
        public readonly LimitedTextBuffer Stderr = new LimitedTextBuffer();
        public bool Complete;
        public int ExitCode = -1;
        public string TempRoot;
        public string MainClass = "";                             // 실제로 저장·실행한 클래스 이름(오류 줄 매칭에 프런트가 쓴다)
        public DateTime DoneAt = DateTime.MaxValue;
        public readonly List<int[]> Echoes = new List<int[]>();    // stdout 속 입력 에코 구간 [시작,길이]
    }

    static readonly object JavaSessionsLock = new object();
    static readonly Dictionary<string, JavaSession> JavaSessions = new Dictionary<string, JavaSession>();

    // pip 설치 1건. 로그를 프로세스가 끝날 때까지 붙들고 있으면 화면이 멈춘 것처럼 보이므로,
    // 파이썬 세션과 같은 방식으로 버퍼에 흘려 담고 프런트가 /pip-install-poll 로 증분만 받아간다.
    class PipJob
    {
        public string Id;
        public Process Process;
        public readonly object Sync = new object();
        public readonly LimitedTextBuffer Log = new LimitedTextBuffer();
        public bool Complete;
        public int ExitCode = -1;
        public bool CancelRequested;
        public DateTime DoneAt = DateTime.MaxValue;
    }

    static readonly object PipJobsLock = new object();
    static readonly Dictionary<string, PipJob> PipJobs = new Dictionary<string, PipJob>();

    // npm 설치·브라우저 번들 작업. 사용자 패키지 설치 스크립트는 helper가 --ignore-scripts로 차단한다.
    class NpmJob
    {
        public string Id;
        public Process Process;
        public readonly object Sync = new object();
        public readonly LimitedTextBuffer Log = new LimitedTextBuffer();
        public bool Complete;
        public int ExitCode = -1;
        public bool CancelRequested;
        public DateTime DoneAt = DateTime.MaxValue;
    }

    static readonly object NpmJobsLock = new object();
    static readonly Dictionary<string, NpmJob> NpmJobs = new Dictionary<string, NpmJob>();

    class TerminalSession
    {
        public string Id;
        public Process Process;
        public readonly object Sync = new object();
        public LimitedTextBuffer Stdout = new LimitedTextBuffer();
        public LimitedTextBuffer Stderr = new LimitedTextBuffer();
        public StreamWriter Input;
        public bool CommandRunning;
        public bool CommandComplete = true;
        public bool ShellExited;
        public int ExitCode = -1;
        public int Sequence;
        public string Cwd = "";
        public string Marker = "";
        public string ScriptPath = "";
        public bool CwdFallback;
        public IntPtr JobHandle = IntPtr.Zero;
        public bool StopRequested;
        public DateTime CommandStartedAt = DateTime.MaxValue;
        public DateTime LastUsed = DateTime.UtcNow;
        public DateTime DoneAt = DateTime.MaxValue;
    }

    static readonly object TerminalSessionsLock = new object();
    static readonly Dictionary<string, TerminalSession> TerminalSessions = new Dictionary<string, TerminalSession>();

    // 노트북 셀을 같은 전역 변수 공간에서 차례로 실행하는 지속형 로컬 Python 커널.
    // Selenium driver 같은 객체도 다음 셀까지 살아 있어 Jupyter와 같은 흐름으로 사용할 수 있다.
    class PythonKernel
    {
        public string Id;
        public Process Process;
        public readonly object ExecLock = new object();
        public readonly LimitedTextBuffer Stderr = new LimitedTextBuffer();
        public string RunnerPath;
        public string TempRoot;
        public DateTime LastUsed = DateTime.UtcNow;
    }

    static readonly object PyKernelsLock = new object();
    static readonly Dictionary<string, PythonKernel> PyKernels = new Dictionary<string, PythonKernel>();

    // 원격 MySQL 접속 하나. 워커 프로세스가 커넥션을 물고 있어 트랜잭션·임시 테이블·세션 변수가
    // 요청 사이에 유지된다. SQLite 미리보기처럼 매번 새로 실행하면 이 셋이 전부 끊긴다.
    class DbSession
    {
        public string Id;
        public Process Process;
        public readonly object ExecLock = new object();    // 요청 한 건과 그 응답 한 줄을 직렬화한다
        public readonly object StdinLock = new object();   // 취소는 쿼리 실행 중에도 stdin 에 써야 한다
        public readonly LimitedTextBuffer Stderr = new LimitedTextBuffer();
        public string RunnerPath;
        public string Label = "";
        public bool ReadOnly = true;
        public string ActiveJobId = "";                    // 취소가 같은 세션의 다른 작업을 죽이지 않게 한다
        public DateTime LastUsed = DateTime.UtcNow;
    }

    // 실행 중인 쿼리 한 건. 브라우저가 60초짜리 fetch 에 매달리지 않도록 시작만 하고
    // 결과는 pip 설치와 같은 방식으로 폴링해 가져간다.
    class DbQueryJob
    {
        public string Id;
        public string SessionId;
        public readonly object Sync = new object();
        public bool Complete;
        public bool CancelRequested;
        public bool Started;                                // ExecLock 을 얻고 워커에 요청까지 보냈는지
        public string ResultJson = "";
        public string Error = "";
        public string Progress = "";                   // 워커가 보낸 마지막 진행 JSON(덤프처럼 오래 걸리는 작업)
        public DateTime DoneAt = DateTime.MaxValue;
    }

    static readonly object DbSessionsLock = new object();
    static readonly Dictionary<string, DbSession> DbSessions = new Dictionary<string, DbSession>();
    static readonly object DbJobsLock = new object();
    static readonly Dictionary<string, DbQueryJob> DbJobs = new Dictionary<string, DbQueryJob>();
    const int MaxDbSessions = 4;                       // 원격 터미널의 동시 세션 상한과 같은 기조
    const int DbMetadataTimeoutMs = 60 * 1000;         // 접속·스키마·테이블 조회
    const int DbQueryDefaultSeconds = 60;
    const int DbQueryMaxSeconds = 600;
    // 덤프는 몇십 분이 걸릴 수 있다. 총 시간이 아니라 "진행 보고가 끊긴 시간"으로 잰다.
    const int DbDumpIdleMs = 120 * 1000;
    const int MaxDbDumpObjects = 500;                  // 워커의 MAX_DUMP_OBJECTS 와 같은 값
    // CSV·엑셀 적재. 워커의 MAX_IMPORT_ROWS·MAX_IMPORT_CELLS 와 같은 값이고, 본문 크기는
    // 여기서 한 번 더 막는다(행·셀 수를 세기 전에 거대한 본문을 읽어 들이지 않기 위해서다).
    const int MaxDbImportRows = 10000;
    const int MaxDbImportCells = 100000;
    const int MaxDbImportBytes = 8 * 1024 * 1024;
    const int DbIdleMinutes = 30;                      // 유휴 접속은 스스로 정리한다
    static readonly object HeartbeatLock = new object();
    static readonly Dictionary<string, DateTime> HeartbeatClients = new Dictionary<string, DateTime>();
    static bool HeartbeatRequired;
    static bool HeartbeatSeen;
    static DateTime HeartbeatStartedAt;
    static DateTime NoHeartbeatClientsSince = DateTime.MaxValue;
    // 기존 창에서 새 앱 창으로 넘어가는 동안 새 페이지의 스크립트가 로드될 시간을 보장한다.
    // 이 시간이 없으면 기존 창을 닫은 뒤 5초 안에 새 heartbeat가 오지 않을 때 서버가 먼저 종료될 수 있다.
    static DateTime BrowserHandoffUntil = DateTime.MinValue;

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

    static string CreateLocalAuthToken()
    {
        byte[] bytes = new byte[32];
        using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
        {
            rng.GetBytes(bytes);
        }
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static byte[] InjectLocalAuthToken(byte[] page)
    {
        string html = Encoding.UTF8.GetString(page);
        string tokenScript = "<script>window.__CLASSDOCK_LOCAL_TOKEN__=" + JsonString(LocalAuthToken) + ";</script>\n";
        int at = html.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
        if (at < 0) at = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (at >= 0) html = html.Insert(at, tokenScript);
        else html = tokenScript + html;
        return Encoding.UTF8.GetBytes(html);
    }

    static string NormalizeSaveRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return null;
        try { return Path.GetFullPath(path.Trim()); }
        catch { return null; }
    }

    static string LoadSaveRoot()
    {
        try
        {
            if (File.Exists(SaveRootConfigPath))
            {
                string configured = NormalizeSaveRoot(File.ReadAllText(SaveRootConfigPath, Encoding.UTF8));
                if (!string.IsNullOrEmpty(configured)) return configured;
            }
        }
        catch { }
        return DefaultSaveRoot;
    }

    static string CurrentSaveRoot()
    {
        lock (SaveRootLock) { return SaveRoot; }
    }

    static void SetSaveRoot(string path)
    {
        string normalized = NormalizeSaveRoot(path);
        if (string.IsNullOrEmpty(normalized)) throw new InvalidDataException("invalid-save-root");
        Directory.CreateDirectory(normalized);
        string configDir = Path.GetDirectoryName(SaveRootConfigPath);
        if (!string.IsNullOrEmpty(configDir)) Directory.CreateDirectory(configDir);
        File.WriteAllText(SaveRootConfigPath, normalized, new UTF8Encoding(false));
        lock (SaveRootLock) { SaveRoot = normalized; }
    }

    static void OpenSaveRootFolder()
    {
        string root = CurrentSaveRoot();
        Directory.CreateDirectory(root);
        Process.Start(new ProcessStartInfo { FileName = root, UseShellExecute = true });
    }

    // 직전에 저장한 파일이 있는 폴더를 연다(가능하면 그 파일을 하이라이트).
    // rel 은 저장 루트 기준 상대경로. SafeRelPath 로 검증해 저장 루트 밖으로 벗어나지 못하게 한다.
    // 파일이 없으면 그 상위 폴더를, 그마저 없으면 저장 루트를 연다.
    static string OpenFileFolder(string rel)
    {
        string root = CurrentSaveRoot();
        string safe = SafeRelPath(rel ?? "");
        if (safe != null)
        {
            string full = Path.Combine(root, safe);
            if (File.Exists(full))
            {
                Process.Start("explorer.exe", "/select,\"" + full + "\"");   // 폴더를 열고 파일 선택
                return Path.GetDirectoryName(full);
            }
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                return dir;
            }
        }
        OpenSaveRootFolder();
        return root;
    }

    static void SetSaveRootPickerStatus(string state, string result)
    {
        lock (SaveRootPickerLock)
        {
            SaveRootPickerState = state;
            SaveRootPickerResult = result ?? "";
        }
    }

    static string RunSaveRootPickerProcess(string current)
    {
        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";
        string script =
            "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)\n" +
            "$shell = New-Object -ComObject Shell.Application\n" +
            "try {\n" +
            "  $folder = $shell.BrowseForFolder(0, 'ClassDock에서 파일을 자동 저장할 폴더를 선택하세요.', 81, 0)\n" +
            "  if ($null -ne $folder) { [Console]::Write($folder.Self.Path) }\n" +
            "} finally {\n" +
            "  [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)\n" +
            "}\n";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = powershell;
        psi.Arguments = "-NoLogo -NoProfile -STA -WindowStyle Hidden -EncodedCommand " + encoded;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.EnvironmentVariables["MN_SAVE_ROOT"] = current;
        Process process = Process.Start(psi);
        if (process == null) throw new InvalidOperationException("folder-picker-process-failed");
        try
        {
            AllowSetForegroundWindow(process.Id);
            for (int i = 0; i < 40 && !process.HasExited; i++)
            {
                process.Refresh();
                IntPtr hwnd = process.MainWindowHandle;
                if (hwnd != IntPtr.Zero) { SetForegroundWindow(hwnd); break; }
                Thread.Sleep(50);
            }
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("folder-picker-process-error: " + error.Trim());
            return output.Trim();
        }
        finally { process.Dispose(); }
    }

    static bool StartSaveRootPicker()
    {
        lock (SaveRootPickerLock)
        {
            if (SaveRootPickerState == "opening") return false;
            SaveRootPickerState = "opening";
            SaveRootPickerResult = "";
        }
        Thread dialogThread = new Thread(delegate()
        {
            try
            {
                string current = CurrentSaveRoot();
                Directory.CreateDirectory(current);
                string selected = RunSaveRootPickerProcess(current);
                if (!string.IsNullOrEmpty(selected))
                {
                    SetSaveRoot(selected);
                    SetSaveRootPickerStatus("saved", selected);
                }
                else SetSaveRootPickerStatus("cancelled", "");
            }
            catch (Exception ex) { SetSaveRootPickerStatus("error", FlattenMessage(ex)); }
        });
        dialogThread.SetApartmentState(ApartmentState.STA);
        dialogThread.IsBackground = true;
        dialogThread.Start();
        return true;
    }

    static string SaveRootPickerStatusJson()
    {
        lock (SaveRootPickerLock)
        {
            return "{\"state\":" + JsonString(SaveRootPickerState)
                + ",\"result\":" + JsonString(SaveRootPickerResult) + "}";
        }
    }

    static string RunSourceFolderPickerDialog()
    {
        IntPtr displayName = Marshal.AllocHGlobal(520);
        IntPtr pidl = IntPtr.Zero;
        try
        {
            BROWSEINFO info = new BROWSEINFO();
            // 사용자가 버튼을 누른 브라우저 창을 소유자로 지정해 선택창이 뒤에 숨지 않게 한다.
            info.hwndOwner = GetForegroundWindow();
            info.pszDisplayName = displayName;
            info.lpszTitle = "ClassDock에서 열 폴더를 선택하세요.";
            // BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX | BIF_NONEWFOLDERBUTTON
            info.ulFlags = 1 | 64 | 16 | 512;
            pidl = SHBrowseForFolder(ref info);
            if (pidl == IntPtr.Zero) return "";
            StringBuilder selected = new StringBuilder(32768);
            if (!SHGetPathFromIDList(pidl, selected)) throw new InvalidOperationException("folder-path-unavailable");
            return selected.ToString().Trim();
        }
        finally
        {
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
            Marshal.FreeHGlobal(displayName);
        }
    }

    static void RememberSourceFolder(string path)
    {
        try
        {
            string normalized = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(SourceFolderConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            List<string> rows = new List<string>();
            if (File.Exists(SourceFolderConfigPath))
                foreach (string row in File.ReadAllLines(SourceFolderConfigPath, Encoding.UTF8))
                    try
                    {
                        string value = Path.GetFullPath(row.Trim());
                        if (Directory.Exists(value) && !rows.Exists(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
                            rows.Add(value);
                    }
                    catch { }
            rows.RemoveAll(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
            rows.Insert(0, normalized);
            if (rows.Count > 64) rows.RemoveRange(64, rows.Count - 64);
            File.WriteAllLines(SourceFolderConfigPath, rows.ToArray(), new UTF8Encoding(false));
        }
        catch { }
    }

    static bool IsRememberedSourceFolder(string path)
    {
        try
        {
            string normalized = Path.GetFullPath(path);
            if (!File.Exists(SourceFolderConfigPath)) return false;
            foreach (string row in File.ReadAllLines(SourceFolderConfigPath, Encoding.UTF8))
                try
                {
                    if (string.Equals(Path.GetFullPath(row.Trim()), normalized, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
        }
        catch { }
        return false;
    }

    static string RegisterSourceFolder(string path, bool remember)
    {
        string normalized = NormalizeSaveRoot(path);
        if (string.IsNullOrEmpty(normalized) || !Directory.Exists(normalized))
            throw new DirectoryNotFoundException("source-folder-not-found");
        lock (SourceFolderLock)
        {
            foreach (KeyValuePair<string, string> item in SourceFolders)
                if (string.Equals(item.Value, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    if (remember) RememberSourceFolder(normalized);
                    return item.Key;
                }
            string id = Guid.NewGuid().ToString("N");
            SourceFolders[id] = normalized;
            if (remember) RememberSourceFolder(normalized);
            return id;
        }
    }

    static bool StartSourceFolderPicker()
    {
        lock (SourceFolderPickerLock)
        {
            if (SourceFolderPickerState == "opening") return false;
            SourceFolderPickerState = "opening";
            SourceFolderPickerResult = "";
            SourceFolderPickerId = "";
        }
        Thread dialogThread = new Thread(delegate()
        {
            try
            {
                string selected = RunSourceFolderPickerDialog();
                lock (SourceFolderPickerLock)
                {
                    if (string.IsNullOrEmpty(selected))
                    {
                        SourceFolderPickerState = "cancelled";
                    }
                    else
                    {
                        SourceFolderPickerResult = Path.GetFullPath(selected);
                        SourceFolderPickerId = RegisterSourceFolder(SourceFolderPickerResult, true);
                        SourceFolderPickerState = "saved";
                    }
                }
            }
            catch (Exception ex)
            {
                lock (SourceFolderPickerLock)
                {
                    SourceFolderPickerState = "error";
                    SourceFolderPickerResult = FlattenMessage(ex);
                    SourceFolderPickerId = "";
                }
            }
        });
        dialogThread.SetApartmentState(ApartmentState.STA);
        dialogThread.IsBackground = true;
        dialogThread.Start();
        return true;
    }

    static string SourceFolderPickerStatusJson()
    {
        lock (SourceFolderPickerLock)
        {
            return "{\"state\":" + JsonString(SourceFolderPickerState)
                + ",\"result\":" + JsonString(SourceFolderPickerResult)
                + ",\"id\":" + JsonString(SourceFolderPickerId) + "}";
        }
    }

    static string RestoreSourceFolderJson(byte[] body)
    {
        string path = Encoding.UTF8.GetString(body ?? new byte[0]).Trim();
        if (!IsRememberedSourceFolder(path)) throw new UnauthorizedAccessException("source-folder-not-remembered");
        string id = RegisterSourceFolder(path, false);
        return "{\"id\":" + JsonString(id) + ",\"path\":" + JsonString(Path.GetFullPath(path)) + "}";
    }

    static bool HasLocalActionHeader(Dictionary<string, string> headers)
    {
        string value;
        return headers != null && headers.TryGetValue("X-ClassDock-Action", out value) && value == "1";
    }

    static bool HasImageMemoHeader(Dictionary<string, string> headers)
    {
        string value;
        return headers != null && headers.TryGetValue("X-ClassDock-Image-Memo", out value) && value == "1";
    }

    static bool TokenEquals(string value)
    {
        if (value == null) return false;
        int diff = value.Length ^ LocalAuthToken.Length;
        for (int i = 0; i < LocalAuthToken.Length; i++)
        {
            char c = i < value.Length ? value[i] : '\0';
            diff |= c ^ LocalAuthToken[i];
        }
        return diff == 0;
    }

    static bool HasLocalAuthToken(Dictionary<string, string> headers)
    {
        string value;
        if (headers != null && headers.TryGetValue("X-ClassDock-Token", out value) && TokenEquals(value)) return true;
        return false;
    }

    // loopback에만 바인딩하더라도 DNS rebinding 등으로 다른 Host가 들어오는 요청은 받지 않는다.
    // 이 서버는 IPv4 loopback 전용이므로 Host도 localhost 또는 127.0.0.1만 허용한다.
    static bool HasAllowedLocalHost(Dictionary<string, string> headers)
    {
        string value;
        if (headers == null || !headers.TryGetValue("Host", out value) || string.IsNullOrWhiteSpace(value)) return false;
        string host = value.Trim().ToLowerInvariant();
        int colon = host.LastIndexOf(':');
        if (colon > 0) host = host.Substring(0, colon);
        return host == "127.0.0.1" || host == "localhost";
    }

    // 브라우저가 Origin을 보냈다면 현재 loopback origin과 일치해야 한다.
    // 일부 로컬 도구는 Origin을 생략하므로, 그 경우에는 실행별 토큰 검증을 계속 경계로 삼는다.
    static bool HasAllowedLocalOrigin(Dictionary<string, string> headers)
    {
        string origin, host;
        if (headers == null || !headers.TryGetValue("Origin", out origin) || string.IsNullOrWhiteSpace(origin)) return true;
        if (!headers.TryGetValue("Host", out host) || string.IsNullOrWhiteSpace(host)) return false;
        return string.Equals(origin.Trim(), "http://" + host.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    static bool RequiresLocalAuthToken(string method, string path)
    {
        if (method == "POST")
        {
            if (path.StartsWith("/workspace-save", StringComparison.Ordinal)) return true;
            if (path == "/workspace-clear" || path == "/workspace-remove") return true;
            if (path == "/convert-pptx" || path == "/convert-media" || path == "/install-ffmpeg") return true;
            // 경로 방식 변환은 디스크의 파일을 읽고 쓴다 → 토큰 대상. 재생 표 발급도 같다
            // (표를 확인해 파일을 흘려보내는 GET /media-stream 만 예외 — <video> 는 헤더를 못 붙인다).
            if (path.StartsWith("/convert-media-path", StringComparison.Ordinal)
                || path.StartsWith("/convert-media-cancel", StringComparison.Ordinal)
                || path.StartsWith("/media-ticket", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/app-state", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/diagnostics/", StringComparison.Ordinal)) return true;
            if (path == "/sqlite-preview" || path == "/sqlite-disk-preview" || path == "/sqlite-exec"
                || path == "/save-file" || path == "/save-file-exists") return true;
            if (path == "/open-save-folder" || path == "/open-file-folder" || path == "/choose-save-folder") return true;
            // 앱 모드: 설정 저장은 다음 실행 동작을 바꾸고, 재열기는 브라우저 프로세스를 띄운다 → 둘 다 토큰 필요.
            if (path.StartsWith("/launcher-config", StringComparison.Ordinal) || path == "/reopen-app-mode") return true;
            if (path == "/choose-source-folder" || path == "/source-folder-restore") return true;
            if (path.StartsWith("/source-folder-file", StringComparison.Ordinal)
                || path.StartsWith("/source-folder-directory", StringComparison.Ordinal)
                || path.StartsWith("/source-folder-remove", StringComparison.Ordinal)) return true;
            if (path == "/image-memo-delete") return true;
            if (path == "/complete" || path == "/definition") return true;
            if (path == "/python-project-sync") return true;
            if (path == "/exam-receive-start" || path == "/exam-receive-stop") return true;
            if (path.StartsWith("/pip-install", StringComparison.Ordinal)) return true;   // /pip-install, -start, -cancel
            if (path.StartsWith("/js-npm-", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/heartbeat", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/python-kernel-", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/db-", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/python-session-", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/terminal-session-", StringComparison.Ordinal)) return true;
            if (path == "/terminal-complete") return true;
            if (path.StartsWith("/ssh-", StringComparison.Ordinal)) return true;
            if (path == "/run-python" || path == "/run-python-bundle") return true;
            if (path == "/python-rescan") return true;
            // 자바 실행·설치 계열. 탐지 캐시를 비우는 /java-rescan 은 다음 실행 동작을 바꾸므로 토큰이 필요하다
            // (읽기만 하는 /can-run-java·/java-diagnostics 는 파이썬 쪽과 같이 Origin 검사만 거친다).
            if (path.StartsWith("/java-", StringComparison.Ordinal)) return true;
            if (path == "/tile-cache-clear") return true;
            if (path.StartsWith("/map-search-key", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/map-search-provider", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/exchange-rate-key", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/subway-key", StringComparison.Ordinal)) return true;
        }
        if (method == "GET")
        {
            if (path.StartsWith("/ssh-file-job?", StringComparison.Ordinal)
                || path.StartsWith("/ssh-file-content?", StringComparison.Ordinal)) return true;
            if (path == "/workspace-load") return true;
            if (path.StartsWith("/diagnostics/", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/exam-receive-status", StringComparison.Ordinal)) return true;
            if (path == "/save-root" || path == "/choose-save-folder-status") return true;
            if (path == "/launcher-config") return true;
            if (path == "/source-folder-capability" || path == "/choose-source-folder-status") return true;
            if (path.StartsWith("/source-folder-entry", StringComparison.Ordinal)
                || path.StartsWith("/source-folder-list", StringComparison.Ordinal)
                || path.StartsWith("/source-folder-file", StringComparison.Ordinal)) return true;
            if (path == "/image-memo-list" || path.StartsWith("/image-memo-file?", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/convert-media-job", StringComparison.Ordinal)) return true;
            if (path == "/can-complete") return true;
            if (path == "/python-import-index") return true;
            if (path.StartsWith("/local-file?", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/python-kernel-file?", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/pip-install-poll", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/db-", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/js-npm-", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/python-session-poll", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/python-session-file", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/java-session-poll", StringComparison.Ordinal)) return true;
            // 라이브러리 목록·카탈로그·설치 로그도 토큰 대상이다(설치·삭제 자체는 위 POST 규칙이 잡는다).
            if (path.StartsWith("/java-lib-", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/terminal-session-poll", StringComparison.Ordinal)) return true;
            if (path == "/ssh-capability" || path == "/ssh-key-pick-status" || path == "/ssh-upload-pick-status"
                || path.StartsWith("/ssh-session-poll", StringComparison.Ordinal)
                || path.StartsWith("/ssh-upload-poll", StringComparison.Ordinal)) return true;
            if (path == "/tile-cache-status" || path == "/can-proxy-tiles") return true;
            if (path == "/map-search-key-status") return true;
            if (path.StartsWith("/geocode?", StringComparison.Ordinal)) return true;
            if (path == "/can-proxy-rates" || path == "/exchange-rate-key-status") return true;
            if (path.StartsWith("/exchange-rate?", StringComparison.Ordinal)) return true;
            if (path == "/can-proxy-subway" || path == "/subway-key-status") return true;
            if (path.StartsWith("/subway-position?", StringComparison.Ordinal)) return true;
        }
        if (method == "DELETE" && (path == "/map-search-key" || path == "/exchange-rate-key")) return true;
        if (method == "DELETE" && path == "/subway-key") return true;
        return false;
    }

    static bool IsImageMemoExtension(string ext)
    {
        ext = (ext ?? "").ToLowerInvariant();
        return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp"
            || ext == ".gif" || ext == ".bmp" || ext == ".svg" || ext == ".avif";
    }

    static string ImageMemoContentType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
        if (ext == ".webp") return "image/webp";
        if (ext == ".gif") return "image/gif";
        if (ext == ".bmp") return "image/bmp";
        if (ext == ".svg") return "image/svg+xml";
        if (ext == ".avif") return "image/avif";
        return "image/png";
    }

    static bool TryResolveImageMemoPath(string relativePath, out string fullPath)
    {
        fullPath = "";
        string safe = SafeRelPath(relativePath);
        if (safe == null || !IsImageMemoExtension(Path.GetExtension(safe))) return false;
        try
        {
            string root = Path.GetFullPath(CurrentSaveRoot());
            string memoRoot = Path.GetFullPath(Path.Combine(root, "이미지메모"));
            string candidate;
            if (!TryResolveSaveRootPath(safe, out candidate) || !IsPathInsideRoot(memoRoot, candidate, false)) return false;
            fullPath = candidate;
            return true;
        }
        catch { return false; }
    }

    static string ImageMemoListJson()
    {
        lock (ImageMemoLock)
        {
            try
            {
                string root = Path.GetFullPath(CurrentSaveRoot());
                string memoRoot = Path.Combine(root, "이미지메모");
                if (!Directory.Exists(memoRoot)) return "{\"items\":[],\"total\":0}";
                List<FileInfo> files = new List<FileInfo>();
                foreach (string path in Directory.GetFiles(memoRoot, "*", SearchOption.AllDirectories))
                {
                    if (!IsImageMemoExtension(Path.GetExtension(path))) continue;
                    try
                    {
                        FileInfo info = new FileInfo(path);
                        if (info.Length <= 0) continue;
                        files.Add(info);
                    }
                    catch { }
                }
                files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
                int total = files.Count;
                int limit = Math.Min(total, 50);
                string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                StringBuilder json = new StringBuilder();
                json.Append("{\"items\":[");
                for (int i = 0; i < limit; i++)
                {
                    if (i > 0) json.Append(',');
                    FileInfo info = files[i];
                    string rel = info.FullName.Substring(rootPrefix.Length).Replace('\\', '/');
                    long modified = (long)(info.LastWriteTimeUtc - epoch).TotalMilliseconds;
                    json.Append("{\"path\":").Append(JsonString(rel))
                        .Append(",\"name\":").Append(JsonString(info.Name))
                        .Append(",\"size\":").Append(info.Length)
                        .Append(",\"modified\":").Append(modified)
                        .Append('}');
                }
                json.Append("],\"total\":").Append(total).Append('}');
                return json.ToString();
            }
            catch { return "{\"items\":[],\"total\":0}"; }
        }
    }

    static bool TryReadImageMemo(string relativePath, out byte[] data, out string contentType)
    {
        data = null;
        contentType = "application/octet-stream";
        string full;
        if (!TryResolveImageMemoPath(relativePath, out full)) return false;
        lock (ImageMemoLock)
        {
            if (!File.Exists(full)) return false;
            FileInfo info = new FileInfo(full);
            if (info.Length <= 0 || info.Length > 200L * 1024 * 1024) return false;
            data = File.ReadAllBytes(full);
            contentType = ImageMemoContentType(full);
            return true;
        }
    }

    static bool DeleteImageMemo(string relativePath)
    {
        string full;
        if (!TryResolveImageMemoPath(relativePath, out full)) return false;
        lock (ImageMemoLock)
        {
            if (!File.Exists(full)) return false;
            File.Delete(full);
            string memoRoot = Path.GetFullPath(Path.Combine(CurrentSaveRoot(), "이미지메모"));
            string dir = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(dir) && !string.Equals(dir, memoRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.GetFileSystemEntries(dir).Length != 0) break;
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
            return true;
        }
    }

    public static void Run()
    {
        // 안정적인 origin(포트) 확보용 고정 포트 후보 목록.
        // 브라우저 localStorage(테마·자동복원 설정·탭 순서 등)는 origin(127.0.0.1:포트)별로 갈리므로,
        // 매 실행 같은 포트로 떠야 설정이 유지된다. 첫 후보가 외부 앱/좀비 소켓에 막혀도 "랜덤"이 아니라
        // 다음 고정 후보로 결정적으로 떨어져, 같은 PC 는 재실행마다 같은 포트를 재사용한다.
        int[] candidatePorts = new int[] { 17645, 18645, 19645, 27645, 37645, 47645 };
        TcpListener listener = null;
        int port = 0;

        // 1) 직전 인스턴스가 기록해 둔 포트가 아직 우리 서버면 → 새로 띄우지 않고 그 origin 으로 브라우저만 열고 종료(단일 인스턴스).
        //    후보 포트 전체를 HTTP 로 확인하지 않고 "기록된 한 곳"만 확인하므로 기동이 빠르다.
        //    (예전엔 후보마다 /ping 을 시도해, 외부 앱/좀비 소켓이 점유한 포트에서 타임아웃이 쌓여 기동이 느렸다.)
        int remembered = ReadInstancePort();
        if (remembered > 0 && IsOurServerAt(remembered))
        {
            if (Environment.GetEnvironmentVariable("CLASSDOCK_NO_BROWSER") != "1")
            {
                try { OpenAppUrl("http://127.0.0.1:" + remembered + "/", LoadAppMode()); } catch {}
            }
            return;
        }

        // 포트 기록이 생기기 전 거의 동시에 실행된 경우에도 한 프로세스만 서버와 TEMP 청소를 맡는다.
        // 뒤에 온 프로세스는 먼저 온 프로세스가 포트를 기록할 때까지 잠시 기다린 뒤 브라우저만 연다.
        if (!TryAcquireSingleInstanceMutex())
        {
            for (int i = 0; i < 20; i++)
            {
                remembered = ReadInstancePort();
                if (remembered > 0 && IsOurServerAt(remembered))
                {
                    if (Environment.GetEnvironmentVariable("CLASSDOCK_NO_BROWSER") != "1")
                    {
                        try { OpenAppUrl("http://127.0.0.1:" + remembered + "/", LoadAppMode()); } catch { }
                    }
                    return;
                }
                Thread.Sleep(100);
            }
            return;
        }

        // 2) 바인딩 가능한 첫 후보 포트를 사용한다(HTTP 확인 없이 TCP 바인드만 시도 → 점유 포트도 즉시 실패라 빠름).
        //    결정적 순서라 같은 PC 는 재실행마다 같은 포트를 재사용 → origin 이 유지된다.
        foreach (int cand in candidatePorts)
        {
            try
            {
                TcpListener l = new TcpListener(IPAddress.Loopback, cand);
                l.Start();
                listener = l;
                port = cand;
                break;
            }
            catch { /* 이 포트는 점유됨 → 다음 후보 시도 */ }
        }

        // 3) 모든 후보가 막힌 드문 경우에만 최후로 임의 포트를 쓴다(이때만 origin 이 달라질 수 있음).
        if (listener == null)
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        WriteInstancePort(port);   // 다음 실행이 이 포트로 바로 붙을 수 있게 기록(단일 인스턴스 확인용)
        string url = "http://127.0.0.1:" + port + "/";
        ServerUrl = url;
        HeartbeatRequired = Environment.GetEnvironmentVariable("CLASSDOCK_NO_BROWSER") != "1";
        HeartbeatStartedAt = DateTime.UtcNow;

        // 지난 실행이 %TEMP% 에 남긴 고아 작업폴더 청소. 지울 양이 수백 MB 일 수 있어
        // 별도 스레드에서 처리한다 — 기동과 첫 화면을 붙잡지 않는다.
        Thread tempSweeper = new Thread(delegate() { try { SweepOrphanTempEntries(); } catch { } });
        tempSweeper.IsBackground = true;
        tempSweeper.Start();

        Console.WriteLine("============================================");
        Console.WriteLine("  ClassDock is running");
        Console.WriteLine("============================================");
        Console.WriteLine("  URL: " + url);
        Console.WriteLine("  Close this window to stop.");
        Console.WriteLine("============================================");

        // CLASSDOCK_NO_BROWSER=1 이면 자동 브라우저 실행을 끈다(테스트/자동화용).
        if (HeartbeatRequired)
        {
            Thread browser = new Thread(delegate()
            {
                Thread.Sleep(400);
                try
                {
                    OpenAppUrl(url, LoadAppMode());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to open browser: " + ex.Message);
                }
            });
            browser.IsBackground = true;
            browser.Start();

            Thread heartbeatWatcher = new Thread(delegate()
            {
                while (true)
                {
                    Thread.Sleep(2000);
                    bool shouldExit = false;
                    lock (HeartbeatLock)
                    {
                        DateTime now = DateTime.UtcNow;
                        List<string> stale = new List<string>();
                        foreach (KeyValuePair<string, DateTime> client in HeartbeatClients)
                            if ((now - client.Value).TotalSeconds > 90) stale.Add(client.Key);
                        foreach (string id in stale) HeartbeatClients.Remove(id);

                        if (now < BrowserHandoffUntil) NoHeartbeatClientsSince = DateTime.MaxValue;
                        else if (HeartbeatClients.Count > 0) NoHeartbeatClientsSince = DateTime.MaxValue;
                        else if (HeartbeatSeen)
                        {
                            if (NoHeartbeatClientsSince == DateTime.MaxValue) NoHeartbeatClientsSince = now;
                            else if ((now - NoHeartbeatClientsSince).TotalSeconds >= 5) shouldExit = true;
                        }
                        else if ((now - HeartbeatStartedAt).TotalSeconds >= 45) shouldExit = true;
                    }
                    if (shouldExit)
                    {
                        CleanupOwnTempEntries();   // 이 실행이 만든 임시 작업폴더를 남기지 않고 종료
                        Environment.Exit(0);
                    }
                }
            });
            heartbeatWatcher.IsBackground = true;
            heartbeatWatcher.Start();
        }

        // 요청 핸들러 중에는 스레드를 오래 붙잡는 것이 많다 — SSH 롱폴(최대 500ms 대기),
        // 타일·지오코딩·환율의 동기 외부 HTTP, 파이썬 등 외부 프로세스 WaitForExit.
        // 스레드풀 기본 최소치는 코어 수뿐이고 그 위로는 초당 1~2개씩만 늘어나므로,
        // 지도를 열거나 파이썬을 돌리는 동안 SSH 키 입력 요청이 큐에서 수백ms~수초 대기하게 된다.
        // 최소치를 넉넉히 올려 둔다(최소치일 뿐 필요할 때만 실제로 생성되므로 평소 비용은 없다).
        try
        {
            int minWorker, minIo;
            ThreadPool.GetMinThreads(out minWorker, out minIo);
            int wantedWorker = Math.Max(64, Environment.ProcessorCount * 8);
            if (minWorker < wantedWorker) ThreadPool.SetMinThreads(wantedWorker, minIo);
        }
        catch { /* 최소치 조정 실패는 기능에 영향이 없다 */ }

        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            // 브라우저의 상태 폴링마다 전용 Thread를 만들면 종료된 Thread의 OS 핸들이
            // GC 전까지 누적된다. SSH 폴링처럼 요청이 잦은 기능에서는 수만 개까지
            // 쌓여 askpass 보조 프로세스조차 시작하지 못하므로 재사용되는 스레드풀로 처리한다.
            if (!ThreadPool.QueueUserWorkItem(delegate(object state) { HandleClient((TcpClient)state); }, client))
                client.Close();
        }
    }

    // 한 TCP 연결로 여러 요청을 처리하기 위한 래퍼.
    //  - 읽기: 큰 덩어리로 당겨 와 버퍼에서 꺼내 준다. 헤더를 1바이트씩 recv 하던 비용이 사라지고,
    //          다음 요청의 앞부분을 함께 읽어 와도 버퍼에 남아 있어 잃지 않는다.
    //  - KeepAlive: 이번 응답 뒤에도 연결을 유지할지. WriteResponse 가 이 값으로 Connection 헤더를 정한다.
    //    본문을 끝까지 읽지 않고 빠져나가는 경로는 반드시 false 로 두어야 한다. 남은 본문 바이트를
    //    다음 요청의 헤더로 잘못 읽게 되기 때문이다.
    sealed class HttpConnectionStream : Stream
    {
        readonly Stream inner;
        readonly byte[] buffer = new byte[8192];
        int start, end;
        public bool KeepAlive = true;

        public HttpConnectionStream(Stream stream) { inner = stream; }

        bool Fill()
        {
            if (start < end) return true;
            start = 0;
            end = inner.Read(buffer, 0, buffer.Length);
            if (end <= 0) { end = 0; return false; }
            return true;
        }

        public override int ReadByte()
        {
            if (!Fill()) return -1;
            return buffer[start++];
        }

        public override int Read(byte[] target, int offset, int count)
        {
            if (count <= 0) return 0;
            if (!Fill()) return 0;
            int take = Math.Min(count, end - start);
            Buffer.BlockCopy(buffer, start, target, offset, take);
            start += take;
            return take;
        }

        public override void Write(byte[] source, int offset, int count) { inner.Write(source, offset, count); }
        public override void Flush() { inner.Flush(); }
        // 영상 스트리밍은 상대가 버퍼를 다 채우면 몇 분씩 읽지 않는다. 그동안 Write 가 막히므로
        // 그 응답에서만 보내기 제한 시간을 늘릴 수 있게 열어 둔다(끝나면 원래 값으로 되돌린다).
        public override bool CanTimeout { get { return inner.CanTimeout; } }
        public override int WriteTimeout { get { return inner.WriteTimeout; } set { inner.WriteTimeout = value; } }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }

    // 응답마다 연결을 끊으면(Connection: close) 먼저 끊은 서버 쪽에 TIME_WAIT 이 120초씩 쌓인다.
    // 요청이 잦은 SSH 폴링에서는 수백 개까지 누적되어, 브라우저가 다음 연결에 고른 포트가
    // 그 조합과 겹치면 SYN 이 무시되고 재전송(약 300ms → 600ms → …)으로 넘어간다.
    // 원격 터미널에서 간헐적으로 타자가 멈추던 원인이 이것이라 연결을 재사용한다.
    const int MaxRequestsPerConnection = 1000;

    static void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            using (NetworkStream raw = client.GetStream())
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 15000;
                client.NoDelay = true;   // 작은 응답이 Nagle 과 지연 ACK 에 걸려 늦게 나가지 않도록
                HttpConnectionStream stream = new HttpConnectionStream(raw);
                for (int served = 0; served < MaxRequestsPerConnection; served++)
                {
                    stream.KeepAlive = served + 1 < MaxRequestsPerConnection;
                    HandleRequest(stream);
                    if (!stream.KeepAlive) break;
                }
            }
        }
        catch { /* 연결 오류는 무시 */ }
    }

    static void HandleRequest(HttpConnectionStream stream)
    {
        {
            {
                // ---- 요청 헤더를 \r\n\r\n 까지 바이트 단위로 읽는다(바디는 바이너리라 StreamReader 금지) ----
                List<byte> head = new List<byte>(1024);
                bool headerComplete = false;
                int b;
                while ((b = stream.ReadByte()) != -1)
                {
                    head.Add((byte)b);
                    int n = head.Count;
                    if (n >= 4 && head[n - 4] == 13 && head[n - 3] == 10 && head[n - 2] == 13 && head[n - 1] == 10)
                    {
                        headerComplete = true;
                        break;
                    }
                    if (n > MaxHttpHeaderBytes)
                    {
                        stream.KeepAlive = false;   // 본문을 읽지 않고 끝내므로 연결을 재사용할 수 없다
                        WriteResponse(stream, "431 Request Header Fields Too Large", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("request-header-too-large"));
                        return;
                    }
                }
                // 재사용 중인 연결을 상대가 조용히 닫은 경우다. 오류가 아니므로 응답 없이 끝낸다.
                if (head.Count == 0)
                {
                    stream.KeepAlive = false;
                    return;
                }
                if (!headerComplete)
                {
                    stream.KeepAlive = false;
                    WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("incomplete-request-header"));
                    return;
                }
                string headerText = Encoding.ASCII.GetString(head.ToArray());
                string[] lines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                string[] rp = (lines.Length > 0 ? lines[0] : "").Split(' ');
                string method = rp.Length > 0 ? rp[0] : "";
                string path = rp.Length > 1 ? rp[1] : "/";

                int contentLength = 0;
                bool hasContentLength = false;
                Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int c = lines[i].IndexOf(':');
                    if (c <= 0) continue;
                    string key = lines[i].Substring(0, c).Trim();
                    string val = lines[i].Substring(c + 1).Trim();
                    headers[key] = val;
                    if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        int parsedLength;
                        if (!int.TryParse(val, out parsedLength) || parsedLength < 0
                            || (hasContentLength && parsedLength != contentLength))
                        {
                            stream.KeepAlive = false;
                            WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("invalid-content-length"));
                            return;
                        }
                        contentLength = parsedLength;
                        hasContentLength = true;
                    }
                }

                if (rp.Length < 3 || !rp[2].StartsWith("HTTP/", StringComparison.Ordinal) || !path.StartsWith("/", StringComparison.Ordinal))
                {
                    stream.KeepAlive = false;
                    WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("invalid-request-line"));
                    return;
                }
                // 연결 재사용 협상. HTTP/1.1 은 기본 유지, 1.0 은 명시할 때만 유지하고,
                // 어느 쪽이든 상대가 close 를 요구하면 따른다.
                string connectionHeader;
                if (!headers.TryGetValue("Connection", out connectionHeader)) connectionHeader = "";
                if (connectionHeader.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0) stream.KeepAlive = false;
                else if (!rp[2].StartsWith("HTTP/1.1", StringComparison.Ordinal)
                    && connectionHeader.IndexOf("keep-alive", StringComparison.OrdinalIgnoreCase) < 0) stream.KeepAlive = false;
                // 청크 전송을 빈 본문으로 저장하지 않도록, 지원하지 않는 전송 형식은 라우팅 전에 거부한다.
                if (headers.ContainsKey("Transfer-Encoding"))
                {
                    stream.KeepAlive = false;
                    WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("unsupported-transfer-encoding"));
                    return;
                }
                if (!HasAllowedLocalHost(headers))
                {
                    stream.KeepAlive = false;
                    WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("invalid-local-host"));
                    return;
                }
                // 지도 스냅샷의 sandbox iframe은 Origin: null로 /tile-proxy만 호출한다.
                // 해당 프록시는 별도 목적지 allowlist로 보호하므로 이 경로만 예외로 둔다.
                if (!HasAllowedLocalOrigin(headers) && !path.StartsWith("/tile-proxy", StringComparison.Ordinal))
                {
                    stream.KeepAlive = false;
                    WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("invalid-local-origin"));
                    return;
                }
                if (contentLength > MaxHttpRequestBodyBytes)
                {
                    stream.KeepAlive = false;
                    WriteResponse(stream, "413 Payload Too Large", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("request-body-too-large"));
                    return;
                }
                // 인증 실패 요청은 본문을 읽지 않는다. 큰 무단 요청으로 메모리·I/O를 점유하는 것을 막는다.
                // 본문이 스트림에 남으므로 연결도 재사용하지 않는다.
                if (RequiresLocalAuthToken(method, path) && !HasLocalAuthToken(headers))
                {
                    stream.KeepAlive = false;
                    WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("local-token-required"));
                    return;
                }

                // ---- 바디(있으면) 읽기 ----
                byte[] body = new byte[0];
                if (contentLength > 0)
                {
                    body = new byte[contentLength];
                    int read = 0;
                    while (read < contentLength)
                    {
                        int got = stream.Read(body, read, contentLength - read);
                        if (got <= 0) break;
                        read += got;
                    }
                    // 연결 종료로 덜 받은 본문을 정상적인 빈 파일 저장으로 처리하면 원본이 지워진다.
                    if (read != contentLength)
                    {
                        stream.KeepAlive = false;
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("incomplete-request-body"));
                        return;
                    }
                }

                // ---- 라우팅 ----
                if (method == "OPTIONS" && path.StartsWith("/tile-proxy", StringComparison.Ordinal))
                {
                    // sandbox(origin null) iframe 의 fetch 는 사설 주소(127.0.0.1)라 CORS/PNA 사전 요청을 보낸다.
                    // 이걸 허용해 줘야 지도 스냅샷의 타일 프록시 fetch 가 통과한다.
                    string preflight =
                        "HTTP/1.1 204 No Content\r\n" +
                        "Access-Control-Allow-Origin: *\r\n" +
                        "Access-Control-Allow-Methods: GET, OPTIONS\r\n" +
                        "Access-Control-Allow-Headers: *\r\n" +
                        "Access-Control-Allow-Private-Network: true\r\n" +
                        "Access-Control-Max-Age: 600\r\n" +
                        "Content-Length: 0\r\n" +
                        (stream.KeepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n") +
                        "\r\n";
                    byte[] preflightBytes = Encoding.ASCII.GetBytes(preflight);
                    stream.Write(preflightBytes, 0, preflightBytes.Length);
                }
                else if (method == "POST" && path.StartsWith("/workspace-save", StringComparison.Ordinal))
                {
                    if (!headers.ContainsKey("X-ClassDock-Workspace"))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("workspace-header-required"));
                        return;
                    }
                    try
                    {
                        bool replace = path.IndexOf("replace=1", StringComparison.OrdinalIgnoreCase) >= 0;
                        int count = SaveWorkspace(body, replace);
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(count.ToString()));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("workspace-save-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path == "/workspace-load")
                {
                    byte[] saved = LoadWorkspace();
                    WriteResponse(stream, "200 OK", "application/octet-stream", saved);
                }
                else if (method == "POST" && path == "/workspace-clear")
                {
                    if (!headers.ContainsKey("X-ClassDock-Workspace"))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("workspace-header-required"));
                        return;
                    }
                    ClearWorkspace();
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "POST" && path == "/workspace-remove")
                {
                    if (!headers.ContainsKey("X-ClassDock-Workspace"))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("workspace-header-required"));
                        return;
                    }
                    try
                    {
                        int count = RemoveWorkspaceFiles(body);
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(count.ToString()));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("workspace-remove-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/convert-pptx")
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
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("convert-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/app-state", StringComparison.Ordinal))
                {
                    // origin(포트) 무관 설정 복원용. 저장분이 없으면 빈 객체.
                    byte[] json = Encoding.UTF8.GetBytes(LoadAppState());
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", json);
                }
                else if (method == "POST" && path.StartsWith("/app-state", StringComparison.Ordinal))
                {
                    try
                    {
                        SaveAppState(body);
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("state-save-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/diagnostics/events", StringComparison.Ordinal))
                {
                    int limit;
                    if (!int.TryParse(QueryValue(path, "limit"), out limit)) limit = 500;
                    byte[] json = Encoding.UTF8.GetBytes(LoadDiagnosticEvents(limit));
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", json);
                }
                else if (method == "POST" && path == "/diagnostics/events")
                {
                    try
                    {
                        AppendDiagnosticEvent(body);
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("diagnostic-event-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path == "/diagnostics/session")
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(LoadDiagnosticSession()));
                }
                else if (method == "POST" && path == "/diagnostics/session")
                {
                    try
                    {
                        SaveDiagnosticSession(body);
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("diagnostic-session-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/diagnostics/clear")
                {
                    ClearDiagnosticEvents();
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "POST" && path == "/diagnostics/open-folder")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    try
                    {
                        OpenDiagnosticsFolder();
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(DiagnosticsDir));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("open-diagnostics-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (path == "/ping")
                {
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "POST" && path.StartsWith("/heartbeat?", StringComparison.Ordinal))
                {
                    if (!headers.ContainsKey("X-ClassDock-Heartbeat"))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("heartbeat-header-required"));
                        return;
                    }
                    TouchHeartbeatClient(QueryValue(path, "id"));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "POST" && path.StartsWith("/heartbeat-close?", StringComparison.Ordinal))
                {
                    if (!headers.ContainsKey("X-ClassDock-Heartbeat"))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("heartbeat-header-required"));
                        return;
                    }
                    CloseHeartbeatClient(QueryValue(path, "id"));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (path == "/can-convert")
                {
                    // 변환 백엔드(PowerPoint) 사용 가능 여부를 빠르게 알려준다(앱이 미리 분기 가능)
                    bool ok = Type.GetTypeFromProgID("PowerPoint.Application") != null;
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ok ? "yes" : "no"));
                }
                else if (path == "/can-convert-media")
                {
                    // ffmpeg 사용 가능 여부(브라우저 미지원 코덱 영상 → MP4 변환). exe 옆 또는 PATH 에서 찾는다.
                    bool ok = FindFfmpeg() != null;
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ok ? "yes" : "no"));
                }
                else if (method == "POST" && path == "/convert-media")
                {
                    if (body.Length == 0)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("empty-body"));
                        return;
                    }
                    try
                    {
                        byte[] mp4;
                        lock (MediaConvLock) { mp4 = ConvertMediaToMp4(body, headers.ContainsKey("X-Media-Reencode") && headers["X-Media-Reencode"] == "1"); }
                        WriteResponse(stream, "200 OK", "video/mp4", mp4);
                    }
                    catch (FfmpegMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-ffmpeg"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("convert-media-failed: " + FlattenMessage(ex)));
                    }
                }
                // 큰 영상은 본문으로 주고받지 않는다 — 앱이 경로만 넘기고 ffmpeg 가 디스크에서 직접 처리한다.
                else if (method == "POST" && path.StartsWith("/convert-media-path?", StringComparison.Ordinal))
                {
                    try
                    {
                        string jobId = StartMediaConvertJob(QueryValue(path, "id"), QueryValue(path, "in"), QueryValue(path, "out"), QueryValue(path, "reencode") == "1");
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"job\":" + JsonString(jobId) + "}"));
                    }
                    catch (FfmpegMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-ffmpeg"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("convert-media-path-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/convert-media-job?", StringComparison.Ordinal))
                {
                    string json = MediaConvertJobJson(QueryValue(path, "job"));
                    if (json == null) WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("job-not-found"));
                    else WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                }
                else if (method == "POST" && path.StartsWith("/convert-media-cancel?", StringComparison.Ordinal))
                {
                    bool found = CancelMediaConvertJob(QueryValue(path, "job"));
                    WriteResponse(stream, found ? "200 OK" : "404 Not Found", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes(found ? "ok" : "job-not-found"));
                }
                else if (method == "POST" && path.StartsWith("/media-ticket?", StringComparison.Ordinal))
                {
                    try
                    {
                        string ticket = CreateMediaTicket(QueryValue(path, "id"), QueryValue(path, "path"));
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"ticket\":" + JsonString(ticket) + "}"));
                    }
                    catch (FileNotFoundException ex)
                    {
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(FlattenMessage(ex)));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(FlattenMessage(ex)));
                    }
                }
                // 표를 확인해 파일을 흘려보낸다. <video> 는 헤더를 못 붙이므로 여기만 토큰 대신 표로 연다.
                else if (method == "GET" && path.StartsWith("/media-stream?", StringComparison.Ordinal))
                {
                    string full = ResolveMediaTicketPath(QueryValue(path, "t"));
                    if (full == null)
                    {
                        stream.KeepAlive = false;
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("media-ticket-invalid"));
                    }
                    else
                    {
                        try { WriteFileStreamResponse(stream, full, MediaContentType(full), headers); }
                        catch (Exception ex)
                        {
                            stream.KeepAlive = false;
                            WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(FlattenMessage(ex)));
                        }
                    }
                }
                else if (method == "POST" && path == "/install-ffmpeg")
                {
                    // ffmpeg 원클릭 설치: 공식 배포 zip 을 내려받아 ffmpeg.exe 만 exe 옆에 놓는다(백그라운드).
                    if (FindFfmpeg() != null)
                    {
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                    }
                    else
                    {
                        lock (FfmpegInstallLock)
                        {
                            if (_ffInstallState != "downloading" && _ffInstallState != "extracting")
                            {
                                _ffInstallState = "downloading"; _ffInstallReceived = 0; _ffInstallTotal = 0; _ffInstallError = "";
                                Thread th = new Thread(InstallFfmpegWorker);
                                th.IsBackground = true;
                                th.Start();
                            }
                        }
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("started"));
                    }
                }
                else if (path == "/ffmpeg-install-status")
                {
                    string json = "{\"state\":" + JsonString(_ffInstallState)
                        + ",\"received\":" + Interlocked.Read(ref _ffInstallReceived)
                        + ",\"total\":" + Interlocked.Read(ref _ffInstallTotal)
                        + ",\"error\":" + JsonString(_ffInstallError) + "}";
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                }
                else if (path == "/can-run-java")
                {
                    // 로컬에 JDK 가 설치돼 있는지 알려준다(앱이 실행 화면과 설치 안내를 나눌 때 쓴다)
                    bool ok = FindJava() != null;
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ok ? "yes" : "no"));
                }
                else if (path == "/java-diagnostics")
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JavaDiagnostics()));
                }
                else if (method == "POST" && path == "/java-definition")
                {
                    // 설치된 JDK의 src.zip에서 표준 클래스 원문을 읽어 Java Ctrl+클릭에 제공한다.
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes(JavaDefinitionSource(body)));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8",
                            Encoding.UTF8.GetBytes("java-definition-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/java-rescan")
                {
                    // 사용자가 직접 JDK 를 설치한 뒤 exe 를 껐다 켜지 않아도 되도록 캐시를 비우고 다시 찾는다.
                    ResetJavaProbe();
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JavaDiagnostics()));
                }
                else if (method == "POST" && path == "/java-install")
                {
                    // 이미 있으면 200MB 를 다시 받지 않는다(직접 설치하고 이 버튼을 누른 경우).
                    if (FindJava() != null)
                    {
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("already"));
                    }
                    else
                    {
                        lock (JdkInstallLock)
                        {
                            if (!JdkInstallRunning())
                            {
                                _jdkInstallState = "metadata";
                                _jdkInstallReceived = 0; _jdkInstallTotal = 0;
                                _jdkExtractDone = 0; _jdkExtractTotal = 0;
                                _jdkInstallError = ""; _jdkInstallVersion = "";
                                Thread th = new Thread(InstallJdkWorker);
                                th.IsBackground = true;
                                th.Start();
                            }
                        }
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("started"));
                    }
                }
                else if (path == "/java-install-status")
                {
                    string json = "{\"state\":" + JsonString(_jdkInstallState)
                        + ",\"received\":" + Interlocked.Read(ref _jdkInstallReceived)
                        + ",\"total\":" + Interlocked.Read(ref _jdkInstallTotal)
                        + ",\"extracted\":" + Interlocked.Read(ref _jdkExtractDone)
                        + ",\"entries\":" + Interlocked.Read(ref _jdkExtractTotal)
                        + ",\"version\":" + JsonString(_jdkInstallVersion)
                        + ",\"error\":" + JsonString(_jdkInstallError) + "}";
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                }
                else if (method == "GET" && path == "/java-lib-catalog")
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JavaLibraryCatalogJson()));
                }
                else if (method == "GET" && path == "/java-lib-list")
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JavaLibraryListJson()));
                }
                else if (method == "GET" && path.StartsWith("/java-lib-search", StringComparison.Ordinal))
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(JavaLibrarySearchJson(QueryValue(path, "q"))));
                }
                else if (method == "GET" && path.StartsWith("/java-lib-resolve", StringComparison.Ordinal))
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(JavaLibraryResolveJson(QueryValue(path, "group"), QueryValue(path, "artifact"))));
                }
                else if (method == "GET" && path.StartsWith("/java-lib-members", StringComparison.Ordinal))
                {
                    // 직접 좌표로 받은 jar 의 멤버 표. 처음 한 번만 javap 를 돌리고 그 뒤로는 캐시에서 답한다.
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(JavaLibraryMembersJson(QueryValue(path, "spec"))));
                }
                else if (method == "POST" && path == "/java-lib-install-start")
                {
                    string libConfirmed;
                    if (!headers.TryGetValue("x-classdock-javalib-confirm", out libConfirmed) || libConfirmed != "1")
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("javalib-confirmation-required"));
                        return;
                    }
                    // 인터넷에서 실행될 코드를 받는 동작이라 확인 헤더를 요구한다(pip·npm 설치와 같은 규칙).
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(StartJavaLibraryInstall(body)));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("javalib-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/java-lib-install-poll", StringComparison.Ordinal))
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(PollJavaLibraryInstall(QueryValue(path, "id"), QueryValue(path, "from"))));
                }
                else if (method == "POST" && path.StartsWith("/java-lib-install-cancel", StringComparison.Ordinal))
                {
                    CancelJavaLibraryInstall(QueryValue(path, "id"));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "POST" && path.StartsWith("/java-lib-delete", StringComparison.Ordinal))
                {
                    string deleted = DeleteJavaLibrary(QueryValue(path, "id"));
                    WriteResponse(stream, deleted == "ok" ? "200 OK" : "404 Not Found", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes(deleted));
                }
                else if (method == "POST" && path.StartsWith("/java-session-start", StringComparison.Ordinal))
                {
                    try
                    {
                        // ?piped=1 이면 페이로드의 표준입력을 한 번에 넣고 닫는다(채점용). 없으면 대화형.
                        string id = StartJavaSession(body, QueryValue(path, "piped") == "1", QueryValue(path, "libs"),
                            QueryValue(path, "lint") == "1", QueryValue(path, "main"), QueryValue(path, "junit") == "1");
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"id\":" + JsonString(id) + "}"));
                    }
                    catch (JavaMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-java"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("java-session-start-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/java-check", StringComparison.Ordinal))
                {
                    // 저장 직후의 문법 검사. 실행과 같은 페이로드·라이브러리 목록을 받는다.
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(
                            RunJavaCheck(body, QueryValue(path, "libs"), QueryValue(path, "lint") == "1")));
                    }
                    catch (JavaMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-java"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("java-check-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/java-session-poll", StringComparison.Ordinal))
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(PollJavaSession(QueryValue(path, "id"), QueryValue(path, "so"), QueryValue(path, "se"))));
                }
                else if (method == "POST" && path.StartsWith("/java-session-input", StringComparison.Ordinal))
                {
                    SendJavaSessionInput(QueryValue(path, "id"), Encoding.UTF8.GetString(body));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "POST" && path.StartsWith("/java-session-eof", StringComparison.Ordinal))
                {
                    CloseJavaSessionInput(QueryValue(path, "id"));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "POST" && path.StartsWith("/java-session-stop", StringComparison.Ordinal))
                {
                    StopJavaSession(QueryValue(path, "id"));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (path == "/can-run-python")
                {
                    // 로컬에 파이썬이 설치돼 있는지 알려준다(앱이 로컬 실행/Pyodide 분기)
                    bool ok = FindPython() != null;
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ok ? "yes" : "no"));
                }
                else if (path == "/python-diagnostics")
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(PythonDiagnostics()));
                }
                else if (method == "POST" && path == "/python-rescan")
                {
                    // 파이썬을 새로 설치한 사용자가 exe 를 껐다 켜지 않아도 되도록 캐시를 비우고 다시 찾는다.
                    ResetPythonProbe();
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(PythonDiagnostics()));
                }
                else if (path == "/mem")
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(MemoryStatsJson()));
                }
                else if (method == "POST" && path == "/sqlite-preview")
                {
                    try
                    {
                        if (body.Length > 100 * 1024 * 1024)
                        {
                            WriteResponse(stream, "413 Payload Too Large", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("sqlite-too-large"));
                            return;
                        }
                        string json = SqlitePreview(body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (InvalidDataException)
                    {
                        WriteResponse(stream, "415 Unsupported Media Type", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not-sqlite3"));
                    }
                    catch (PythonMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-python"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("sqlite-preview-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/sqlite-disk-preview")
                {
                    // 저장 루트의 실제 DB를 읽는다. 최초 편집 활성화 때는 브라우저가 연 파일의 SHA-256과
                    // 디스크 파일이 일치해야 하며, 이후 새로고침은 이미 확인된 같은 상대경로를 다시 읽는다.
                    try
                    {
                        string json = SqliteDiskPreview(headers);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (FileNotFoundException)
                    {
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-not-found"));
                    }
                    catch (DbMismatchException)
                    {
                        WriteResponse(stream, "409 Conflict", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-changed"));
                    }
                    catch (InvalidDataException)
                    {
                        WriteResponse(stream, "415 Unsupported Media Type", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not-sqlite3"));
                    }
                    catch (PythonMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-python"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("sqlite-disk-preview-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/sqlite-exec")
                {
                    // 워크스페이스에 저장된 .db 원본에 임의 SQL(SELECT/DDL/DML)을 실행한다.
                    // 경로는 X-Db-Path(퍼센트 인코딩, 저장 루트 기준 상대경로), SQL 은 본문 텍스트.
                    // 실행은 단일 트랜잭션으로 처리하고 수정 계열이면 같은 폴더에 일관된 .bak 백업을 남긴다.
                    try
                    {
                        if (body.Length > 2 * 1024 * 1024)
                        {
                            WriteResponse(stream, "413 Payload Too Large", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("sql-too-large"));
                            return;
                        }
                        string json = SqliteExec(headers, body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (FileNotFoundException)
                    {
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-not-found"));
                    }
                    catch (DbMismatchException)
                    {
                        WriteResponse(stream, "409 Conflict", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-changed"));
                    }
                    catch (InvalidDataException)
                    {
                        WriteResponse(stream, "415 Unsupported Media Type", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not-sqlite3"));
                    }
                    catch (PythonMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-python"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("sqlite-exec-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (path == "/can-save-file")
                {
                    // exe 로컬 서버가 디스크 저장을 지원함을 알린다(앱이 브라우저 권한 팝업 대신 서버 저장 선택)
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("yes"));
                }
                else if (method == "GET" && path == "/launcher-config")
                {
                    // 설정 화면이 '앱 모드' 체크박스의 현재 값과 지원 여부를 읽어간다.
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    string json = "{\"appMode\":" + (LoadAppMode() ? "true" : "false")
                        + ",\"appModeAvailable\":" + (FindChromiumBrowser() != null ? "true" : "false") + "}";
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                }
                else if (method == "POST" && path.StartsWith("/launcher-config", StringComparison.Ordinal))
                {
                    // '앱 모드' 토글 저장. 브라우저는 이미 떠 있으므로 이 값은 다음 실행부터 반영된다.
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    try
                    {
                        SaveAppMode(QueryValue(path, "appMode") == "1");
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("launcher-config-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/reopen-app-mode")
                {
                    // 설정의 '지금 앱 모드로 열기': 지금 실행 중인 서버를 --app 창으로 한 번 더 연다.
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    if (FindChromiumBrowser() == null)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-chromium"));
                        return;
                    }
                    try
                    {
                        lock (HeartbeatLock)
                        {
                            // EXE의 모든 스크립트를 읽은 뒤 heartbeat가 시작되므로 느린 PC에서도 충분히 기다린다.
                            BrowserHandoffUntil = DateTime.UtcNow.AddSeconds(45);
                            NoHeartbeatClientsSince = DateTime.MaxValue;
                        }
                        OpenAppUrl(string.IsNullOrEmpty(ServerUrl) ? "http://127.0.0.1/" : ServerUrl, true);
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("reopen-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path == "/save-root")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(CurrentSaveRoot()));
                }
                else if (method == "POST" && path == "/open-save-folder")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    try
                    {
                        OpenSaveRootFolder();
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(CurrentSaveRoot()));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("open-folder-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/open-file-folder")
                {
                    // 헤더 '저장 폴더' 버튼: 직전에 저장한 파일이 있는 폴더를 연다(X-Save-Path = 저장 루트 기준 상대경로).
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    try
                    {
                        string rel = headers.ContainsKey("X-Save-Path") ? Uri.UnescapeDataString(headers["X-Save-Path"]) : "";
                        string opened = OpenFileFolder(rel);
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(opened ?? CurrentSaveRoot()));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("open-folder-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/choose-save-folder")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    bool started = StartSaveRootPicker();
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(started ? "opened" : "opening"));
                }
                else if (method == "GET" && path == "/choose-save-folder-status")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(SaveRootPickerStatusJson()));
                }
                else if (method == "GET" && path == "/source-folder-capability")
                {
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("yes"));
                }
                else if (method == "POST" && path == "/choose-source-folder")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    bool started = StartSourceFolderPicker();
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(started ? "opened" : "opening"));
                }
                else if (method == "GET" && path == "/choose-source-folder-status")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(SourceFolderPickerStatusJson()));
                }
                else if (method == "POST" && path == "/source-folder-restore")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(RestoreSourceFolderJson(body)));
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(FlattenMessage(ex)));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("source-folder-restore-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/source-folder-entry?", StringComparison.Ordinal))
                {
                    try
                    {
                        string json = SourceFolderEntryJson(QueryValue(path, "id"), QueryValue(path, "path"));
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (FileNotFoundException ex)
                    {
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(FlattenMessage(ex)));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/source-folder-list?", StringComparison.Ordinal))
                {
                    try
                    {
                        string json = SourceFolderListJson(QueryValue(path, "id"), QueryValue(path, "path"));
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (DirectoryNotFoundException ex)
                    {
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(FlattenMessage(ex)));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/source-folder-file?", StringComparison.Ordinal))
                {
                    try
                    {
                        // 큰 파일(영상 등)도 열 수 있도록 디스크에서 그대로 흘려보낸다 — byte[] 로 한 번에
                        // 읽으면 .NET 배열 상한(2GB)에 걸리고, 그 아래에서도 파일 크기만큼 메모리를 쓴다.
                        string full = ResolveSourceFolderFilePath(QueryValue(path, "id"), QueryValue(path, "path"));
                        WriteFileStreamResponse(stream, full, "application/octet-stream", headers);
                    }
                    catch (FileNotFoundException ex)
                    {
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(FlattenMessage(ex)));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/source-folder-file?", StringComparison.Ordinal))
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    try
                    {
                        WriteSourceFolderFile(QueryValue(path, "id"), QueryValue(path, "path"), body);
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("source-file-write-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/source-folder-directory?", StringComparison.Ordinal))
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    try
                    {
                        CreateSourceFolderDirectory(QueryValue(path, "id"), QueryValue(path, "path"));
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("source-directory-create-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/source-folder-remove?", StringComparison.Ordinal))
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    try
                    {
                        RemoveSourceFolderEntry(
                            QueryValue(path, "id"),
                            QueryValue(path, "path"),
                            QueryValue(path, "recursive") == "1");
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                    }
                    catch (FileNotFoundException ex)
                    {
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(FlattenMessage(ex)));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("source-entry-remove-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path == "/image-memo-list")
                {
                    if (!HasImageMemoHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("image-memo-header-required"));
                        return;
                    }
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(ImageMemoListJson()));
                }
                else if (method == "GET" && path.StartsWith("/image-memo-file?", StringComparison.Ordinal))
                {
                    if (!HasImageMemoHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("image-memo-header-required"));
                        return;
                    }
                    byte[] imageData;
                    string imageType;
                    if (TryReadImageMemo(QueryValue(path, "path"), out imageData, out imageType))
                        WriteResponse(stream, "200 OK", imageType, imageData);
                    else
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("image-memo-not-found"));
                }
                else if (method == "POST" && path == "/image-memo-delete")
                {
                    if (!HasImageMemoHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("image-memo-header-required"));
                        return;
                    }
                    try
                    {
                        string rel = headers.ContainsKey("X-Image-Memo-Path") ? Uri.UnescapeDataString(headers["X-Image-Memo-Path"]) : "";
                        if (!DeleteImageMemo(rel))
                        {
                            WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("image-memo-not-found"));
                            return;
                        }
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("deleted"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("image-memo-delete-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/save-file-exists")
                {
                    // 새 문서의 첫 저장 전에 SaveRoot의 기존 파일과 충돌하는지 확인한다.
                    try
                    {
                        string rel = headers.ContainsKey("X-Save-Path") ? Uri.UnescapeDataString(headers["X-Save-Path"]) : "";
                        string safe = SafeRelPath(rel);
                        string full;
                        if (safe == null || !TryResolveSaveRootPath(safe, out full))
                        {
                            WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("invalid-save-path"));
                            return;
                        }
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8",
                            Encoding.UTF8.GetBytes(File.Exists(full) ? "yes" : "no"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8",
                            Encoding.UTF8.GetBytes("save-exists-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/save-file")
                {
                    // 편집한 파일을 SaveRoot 아래에 바로 쓴다. 경로는 X-Save-Path(퍼센트 인코딩), 내용은 본문.
                    try
                    {
                        string rel = headers.ContainsKey("X-Save-Path") ? Uri.UnescapeDataString(headers["X-Save-Path"]) : "";
                        string safe = SafeRelPath(rel);
                        if (safe == null) safe = "practice.py";
                        string full;
                        if (!TryResolveSaveRootPath(safe, out full))
                        {
                            WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("invalid-save-path"));
                            return;
                        }
                        string dir = Path.GetDirectoryName(full);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        WriteFileAtomically(full, body);
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(full));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("save-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (path == "/can-complete")
                {
                    // 로컬 파이썬 + Jedi 사용 가능 여부(없으면 1회 설치 시도). 프런트가 에디터 시작 시 백그라운드로 호출.
                    bool ok = false;
                    try { ok = EnsureJedi(); } catch { ok = false; }
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ok ? "yes" : "no"));
                }
                else if (method == "GET" && path == "/python-import-index")
                {
                    // 설치된 패키지의 메타데이터와 Python 소스만 읽어 만든 자동 import 색인.
                    // 실제 패키지 import/실행은 하지 않으며, 생성 작업은 백그라운드에서 한 번만 한다.
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(PythonImportIndexJson()));
                }
                else if (method == "POST" && path == "/exam-receive-start")
                {
                    // 선생님이 [제출 받기]를 켠다. 본문 JSON = {examId, title} — examId 가 있으면 그 시험만 받는다.
                    string bodyText = "";
                    try { bodyText = Encoding.UTF8.GetString(body); } catch { }
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(ExamReceiveStart(ExamJsonString(bodyText, "examId"), ExamJsonString(bodyText, "title"))));
                }
                else if (method == "POST" && path == "/exam-receive-stop")
                {
                    ExamReceiveStop();
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"open\":false}"));
                }
                else if (method == "GET" && path.StartsWith("/exam-receive-status", StringComparison.Ordinal))
                {
                    int since = 0;
                    int qm = path.IndexOf('?');
                    if (qm >= 0)
                    {
                        foreach (string part in path.Substring(qm + 1).Split('&'))
                        {
                            if (part.StartsWith("since=", StringComparison.Ordinal)) int.TryParse(part.Substring(6), out since);
                        }
                    }
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(ExamReceiveStatusJson(since)));
                }
                else if (method == "POST" && path == "/python-project-sync")
                {
                    // 작업공간의 .py 를 임시 폴더에 미러링 → 다음 자동완성부터 Jedi 가 프로젝트 모듈을 안다.
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(SyncPythonProjectMirror(body)));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("project-sync-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/complete")
                {
                    // Jedi 문맥 자동완성. 본문 JSON = {source, line(1-based), column(0-based)}. 결과 JSON = {ok, items:[{name,type}]}
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JediComplete(body)));
                    }
                    catch (PythonMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-python"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("complete-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/definition")
                {
                    // Jedi 정의 위치. 본문 JSON = {source, line(1-based), column(0-based)}.
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JediDefinition(body)));
                    }
                    catch (PythonMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-python"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("definition-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (path.StartsWith("/local-file?", StringComparison.Ordinal))
                {
                    try
                    {
                        byte[] fileData;
                        string fileName;
                        if (TryReadLocalFile(QueryValue(path, "path"), out fileData, out fileName))
                            WriteResponse(stream, "200 OK", "application/octet-stream", fileData);
                        else
                            // 자동 복원·정의 이동은 없어졌거나 지원하지 않는 로컬 파일을 정상적인 폴백으로 취급한다.
                            // 404는 브라우저 개발자 도구에 불필요한 빨간 오류를 남기므로 빈 성공 응답으로 알린다.
                            WriteResponse(stream, "204 No Content", "application/octet-stream", new byte[0]);
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("file-read-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (path.StartsWith("/pyodide/", StringComparison.Ordinal))
                {
                    // 번들된 Pyodide 코어를 로컬에서 서빙(오프라인 실행용). 없으면 404 → 앱이 CDN 으로 폴백.
                    try
                    {
                        byte[] pyData;
                        string pyType;
                        if (TryReadPyodideFile(path, out pyData, out pyType))
                            WriteResponse(stream, "200 OK", pyType, pyData);
                        else
                            WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("pyodide-not-bundled"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("pyodide-read-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path == "/js-npm-status")
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JsNpmStatus()));
                }
                else if (method == "GET" && path == "/js-npm-list")
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(ListJsNpmPackages()));
                }
                else if (method == "GET" && path.StartsWith("/js-npm-bundle?", StringComparison.Ordinal))
                {
                    byte[] bundle;
                    if (TryReadJsNpmBundle(QueryValue(path, "id"), out bundle))
                        WriteResponse(stream, "200 OK", "text/javascript; charset=utf-8", bundle);
                    else
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("npm-package-not-found"));
                }
                else if (method == "POST" && path == "/js-npm-install-start")
                {
                    string confirmed;
                    if (!headers.TryGetValue("x-classdock-npm-confirm", out confirmed) || confirmed != "1")
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("npm-confirmation-required"));
                        return;
                    }
                    try
                    {
                        string json = StartJsNpmInstall(body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("npm-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/js-npm-install-poll", StringComparison.Ordinal))
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(PollJsNpmInstall(QueryValue(path, "id"), QueryValue(path, "from"))));
                }
                else if (method == "POST" && path.StartsWith("/js-npm-install-cancel", StringComparison.Ordinal))
                {
                    CancelJsNpmInstall(QueryValue(path, "id"));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "POST" && path.StartsWith("/js-npm-delete", StringComparison.Ordinal))
                {
                    bool deleted = DeleteJsNpmPackage(QueryValue(path, "id"));
                    WriteResponse(stream, deleted ? "200 OK" : "404 Not Found", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes(deleted ? "ok" : "npm-package-not-found"));
                }
                else if (method == "POST" && path == "/pip-install")
                {
                    string pipConfirmed;
                    if (!headers.TryGetValue("x-classdock-pip-confirm", out pipConfirmed) || pipConfirmed != "1")
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("pip-confirmation-required"));
                        return;
                    }
                    // 설치된 파이썬에 패키지 설치(pip). 본문 = 공백/줄바꿈으로 구분한 패키지 이름들.
                    try
                    {
                        string json = PipInstall(body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (PythonMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-python"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("pip-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/pip-install-start")
                {
                    string pipConfirmed;
                    if (!headers.TryGetValue("x-classdock-pip-confirm", out pipConfirmed) || pipConfirmed != "1")
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("pip-confirmation-required"));
                        return;
                    }
                    // 설치를 시작만 하고 즉시 id 를 돌려준다 — 로그는 /pip-install-poll 로 흘려 보낸다.
                    try
                    {
                        string json = StartPipInstall(body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (PythonMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-python"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("pip-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/pip-install-poll", StringComparison.Ordinal))
                {
                    string json = PollPipInstall(QueryValue(path, "id"), QueryValue(path, "from"));
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                }
                else if (method == "POST" && path.StartsWith("/pip-install-cancel", StringComparison.Ordinal))
                {
                    CancelPipInstall(QueryValue(path, "id"));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "GET" && path == "/db-capability")
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(DbCapability()));
                }
                else if (method == "POST" && path == "/db-session-open")
                {
                    try
                    {
                        string json = StartDbSession(body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (PythonMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-python"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-open-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/db-schema?", StringComparison.Ordinal))
                {
                    try
                    {
                        // mode = tables(기본, 트리) · columns(자동완성용 전체 컬럼) · erd(관계도 일괄 메타데이터)
                        string schemaMode = QueryValue(path, "mode");
                        string schemaAction = schemaMode == "columns" ? "schema-columns" : (schemaMode == "erd" ? "erd" : "schema");
                        string json = DbMetadataRequest(QueryValue(path, "id"), "{\"action\":" + JsonString(schemaAction) + "}");
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-schema-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/db-table?", StringComparison.Ordinal))
                {
                    try
                    {
                        // mode = table(기본) · columns(컬럼) · children(트리 하위 객체) · count · ddl · info
                        string mode = QueryValue(path, "mode");
                        if (mode != "columns" && mode != "children" && mode != "count" && mode != "ddl" && mode != "info") mode = "table";
                        string request = "{\"action\":" + JsonString(mode)
                            + ",\"name\":" + JsonString(DbCheckField(QueryValue(path, "name"), "table", 128, false))
                            + ",\"database\":" + JsonString(DbCheckField(QueryValue(path, "database"), "database", 64, true)) + "}";
                        string json = DbMetadataRequest(QueryValue(path, "id"), request);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-table-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/db-object?", StringComparison.Ordinal))
                {
                    try
                    {
                        string kind = QueryValue(path, "kind").ToLowerInvariant();
                        if (kind != "procedure" && kind != "function" && kind != "event" && kind != "trigger")
                            throw new InvalidOperationException("db-bad-object-kind");
                        string request = "{\"action\":\"object-ddl\",\"kind\":" + JsonString(kind)
                            + ",\"name\":" + JsonString(DbCheckField(QueryValue(path, "name"), "object", 128, false))
                            + ",\"database\":" + JsonString(DbCheckField(QueryValue(path, "database"), "database", 64, true)) + "}";
                        string json = DbMetadataRequest(QueryValue(path, "id"), request);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-object-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/db-dependencies?", StringComparison.Ordinal))
                {
                    try
                    {
                        string kind = QueryValue(path, "kind").ToLowerInvariant();
                        if (kind != "table" && kind != "view" && kind != "column" && kind != "index"
                            && kind != "foreignkey" && kind != "procedure" && kind != "function"
                            && kind != "event" && kind != "trigger")
                            throw new InvalidOperationException("db-bad-object-kind");
                        string request = "{\"action\":\"dependencies\",\"kind\":" + JsonString(kind)
                            + ",\"name\":" + JsonString(DbCheckField(QueryValue(path, "name"), "object", 128, false))
                            + ",\"table\":" + JsonString(DbCheckField(QueryValue(path, "table"), "table", 128, true))
                            + ",\"database\":" + JsonString(DbCheckField(QueryValue(path, "database"), "database", 64, true)) + "}";
                        string json = DbMetadataRequest(QueryValue(path, "id"), request);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-dependencies-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/db-use?", StringComparison.Ordinal))
                {
                    try
                    {
                        string request = "{\"action\":\"use\",\"name\":"
                            + JsonString(DbCheckField(QueryValue(path, "name"), "database", 64, false)) + "}";
                        string json = DbMetadataRequest(QueryValue(path, "id"), request);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-use-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/db-tx?", StringComparison.Ordinal))
                {
                    try
                    {
                        // op = commit · rollback · autocommit(on=0|1) · state
                        string op = QueryValue(path, "op");
                        string request;
                        if (op == "commit") request = "{\"action\":\"commit\"}";
                        else if (op == "rollback") request = "{\"action\":\"rollback\"}";
                        else if (op == "state") request = "{\"action\":\"tx\"}";
                        else if (op == "autocommit")
                            request = "{\"action\":\"autocommit\",\"on\":" + (QueryValue(path, "on") == "0" ? "false" : "true") + "}";
                        else throw new InvalidOperationException("db-bad-tx-op");
                        string json = DbMetadataRequest(QueryValue(path, "id"), request);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-tx-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/db-page?", StringComparison.Ordinal))
                {
                    try
                    {
                        int pageSet = 0, pageOffset = 0, pageLimit = 1000;
                        int.TryParse(QueryValue(path, "set"), out pageSet);
                        int.TryParse(QueryValue(path, "offset"), out pageOffset);
                        if (!int.TryParse(QueryValue(path, "limit"), out pageLimit)) pageLimit = 1000;
                        if (pageSet < 0) pageSet = 0;
                        if (pageOffset < 0) pageOffset = 0;
                        string request = "{\"action\":\"page\",\"set\":" + pageSet
                            + ",\"offset\":" + pageOffset + ",\"limit\":" + pageLimit + "}";
                        string json = DbMetadataRequest(QueryValue(path, "id"), request);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-page-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/db-query?", StringComparison.Ordinal))
                {
                    try
                    {
                        string json = StartDbQuery(QueryValue(path, "id"), body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-query-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/db-cell?", StringComparison.Ordinal))
                {
                    try
                    {
                        // 고치기 전에 값 전체를 다시 읽는다(표에 실린 값은 500자에서 잘려 있을 수 있다).
                        string json = DbCellRequest(QueryValue(path, "id"), body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-cell-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/db-apply?", StringComparison.Ordinal))
                {
                    try
                    {
                        string json = DbApplyRequest(QueryValue(path, "id"), body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-apply-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/db-dump?", StringComparison.Ordinal))
                {
                    try
                    {
                        string json = StartDbDump(QueryValue(path, "id"), body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-dump-failed: " + FlattenMessage(ex)));
                    }
                }
                // 적재는 덤프와 같은 작업 목록에 들어가고 폴링·취소는 /db-query-* 를 그대로 쓴다.
                else if (method == "POST" && path.StartsWith("/db-import?", StringComparison.Ordinal))
                {
                    try
                    {
                        string json = StartDbImport(QueryValue(path, "id"), body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("db-import-failed: " + FlattenMessage(ex)));
                    }
                }
                // 덤프도 쿼리와 같은 작업 목록에 들어간다. 폴링·취소 경로를 함께 쓴다.
                else if (method == "GET" && path.StartsWith("/db-dump-poll", StringComparison.Ordinal))
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(PollDbQuery(QueryValue(path, "job"))));
                }
                else if (method == "GET" && path.StartsWith("/db-query-poll", StringComparison.Ordinal))
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(PollDbQuery(QueryValue(path, "job"))));
                }
                else if (method == "POST" && path.StartsWith("/db-query-cancel", StringComparison.Ordinal))
                {
                    CancelDbQuery(QueryValue(path, "job"));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "POST" && path.StartsWith("/db-session-close", StringComparison.Ordinal))
                {
                    StopDbSession(QueryValue(path, "id"));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "POST" && path == "/python-kernel-start-bundle")
                {
                    try
                    {
                        string id = StartPythonKernel(body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"id\":" + JsonString(id) + "}"));
                    }
                    catch (PythonMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-python"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("kernel-start-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/python-kernel-exec?", StringComparison.Ordinal))
                {
                    try
                    {
                        string json = ExecutePythonKernel(QueryValue(path, "id"), body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("kernel-exec-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/python-kernel-stop?", StringComparison.Ordinal))
                {
                    StopPythonKernel(QueryValue(path, "id"));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "GET" && path.StartsWith("/python-kernel-file?", StringComparison.Ordinal))
                {
                    byte[] fileData; string fileName;
                    if (TryGetKernelFile(QueryValue(path, "id"), QueryValue(path, "name"), out fileData, out fileName))
                        WriteResponse(stream, "200 OK", "application/octet-stream", fileData);
                    else
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("file-not-found"));
                }
                else if (method == "POST" && path.StartsWith("/python-session-start-bundle", StringComparison.Ordinal))
                {
                    try
                    {
                        string id = StartPythonSession(body, true);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"id\":" + JsonString(id) + "}"));
                    }
                    catch (PythonMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-python"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("session-start-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/python-session-start", StringComparison.Ordinal))
                {
                    try
                    {
                        string id = StartPythonSession(body, false);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"id\":" + JsonString(id) + "}"));
                    }
                    catch (PythonMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-python"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("session-start-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/python-session-poll", StringComparison.Ordinal))
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(PollPythonSession(QueryValue(path, "id"), QueryValue(path, "so"), QueryValue(path, "se"))));
                }
                else if (method == "POST" && path.StartsWith("/python-session-input", StringComparison.Ordinal))
                {
                    SendPythonSessionInput(QueryValue(path, "id"), Encoding.UTF8.GetString(body));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "POST" && path.StartsWith("/python-session-stop", StringComparison.Ordinal))
                {
                    StopPythonSession(QueryValue(path, "id"));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "POST" && path == "/terminal-session-open")
                {
                    try
                    {
                        string json = OpenTerminalSession(body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("terminal-start-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/terminal-session-run", StringComparison.Ordinal))
                {
                    try
                    {
                        RunTerminalCommand(QueryValue(path, "id"), body);
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "409 Conflict", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("terminal-run-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/terminal-session-poll", StringComparison.Ordinal))
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(PollTerminalSession(QueryValue(path, "id"))));
                }
                else if (method == "POST" && path.StartsWith("/terminal-session-stop", StringComparison.Ordinal))
                {
                    StopTerminalSession(QueryValue(path, "id"));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "POST" && path == "/terminal-complete")
                {
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(TerminalCompletionJson(body)));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("terminal-complete-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path == "/ssh-capability")
                {
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(ClassDockSshTerminal.CapabilityJson()));
                }
                else if (method == "POST" && path == "/ssh-key-pick")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    bool started = ClassDockSshTerminal.StartPrivateKeyPicker();
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(started ? "opened" : "opening"));
                }
                else if (method == "GET" && path == "/ssh-key-pick-status")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(ClassDockSshTerminal.PrivateKeyPickerStatusJson()));
                }
                else if (method == "POST" && path == "/ssh-upload-pick")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    bool started = ClassDockSshTerminal.StartUploadPicker();
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(started ? "opened" : "opening"));
                }
                else if (method == "GET" && path == "/ssh-upload-pick-status")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(ClassDockSshTerminal.UploadPickerStatusJson()));
                }
                else if (method == "POST" && path == "/ssh-host-key-scan")
                {
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(ClassDockSshTerminal.ScanHostKey(body)));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "502 Bad Gateway", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ssh-host-key-scan-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/ssh-host-key-trust")
                {
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(ClassDockSshTerminal.TrustHostKey(body)));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "409 Conflict", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ssh-host-key-trust-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/ssh-session-open")
                {
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(ClassDockSshTerminal.Open(body)));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "502 Bad Gateway", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ssh-session-open-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/ssh-session-input", StringComparison.Ordinal))
                {
                    try
                    {
                        ClassDockSshTerminal.Input(QueryValue(path, "id"), body);
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "409 Conflict", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ssh-input-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/ssh-session-poll", StringComparison.Ordinal))
                {
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes(ClassDockSshTerminal.Poll(QueryValue(path, "id"), QueryValue(path, "offset"))));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ssh-poll-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/ssh-session-resize", StringComparison.Ordinal))
                {
                    try
                    {
                        ClassDockSshTerminal.Resize(QueryValue(path, "id"), body);
                        WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "409 Conflict", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ssh-resize-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/ssh-session-stop", StringComparison.Ordinal))
                {
                    ClassDockSshTerminal.Stop(QueryValue(path, "id"));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "POST" && path.StartsWith("/ssh-file-", StringComparison.Ordinal))
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes(ClassDockSshTerminal.FileRequest(path.Substring("/ssh-file-".Length), body)));
                    }
                    catch (Exception ex)
                    {
                        string error = ex.Message.StartsWith("ssh-file-", StringComparison.Ordinal) ? ex.Message : "ssh-file-request";
                        WriteResponse(stream, "409 Conflict", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(error));
                    }
                }
                else if (method == "GET" && path.StartsWith("/ssh-file-job?", StringComparison.Ordinal))
                {
                    try { WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(ClassDockSshTerminal.FileStatus(QueryValue(path, "id")))); }
                    catch { WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ssh-file-expired")); }
                }
                else if (method == "GET" && path.StartsWith("/ssh-file-content?", StringComparison.Ordinal))
                {
                    try { WriteResponse(stream, "200 OK", "application/octet-stream", ClassDockSshTerminal.FileContent(QueryValue(path, "id"))); }
                    catch { WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ssh-file-expired")); }
                }
                else if (method == "POST" && path == "/ssh-upload-start")
                {
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes(ClassDockSshTerminal.StartUpload(body)));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "409 Conflict", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ssh-upload-start-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "GET" && path.StartsWith("/ssh-upload-poll", StringComparison.Ordinal))
                {
                    try
                    {
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes(ClassDockSshTerminal.PollUpload(QueryValue(path, "id"), QueryValue(path, "offset"))));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ssh-upload-poll-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path.StartsWith("/ssh-upload-cancel", StringComparison.Ordinal))
                {
                    ClassDockSshTerminal.CancelUpload(QueryValue(path, "id"));
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                }
                else if (method == "GET" && path.StartsWith("/tile-proxy?", StringComparison.Ordinal))
                {
                    // 노트북 PDF 지도 스냅샷용 — sandbox iframe 의 fetch 가 차단되는 타일을 서버가 대신 받아온다
                    byte[] tileData; string tileMime;
                    if (TryProxyMapTile(QueryValue(path, "u"), out tileData, out tileMime))
                        WriteCorsResponse(stream, "200 OK", tileMime, tileData);
                    else
                        WriteCorsResponse(stream, "502 Bad Gateway", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("tile-proxy-failed"));
                }
                else if (method == "GET" && path == "/can-proxy-tiles")
                {
                    // 지도 문서가 "이 런처가 타일을 대신 받아 디스크에 남겨 주는가"를 묻는 자리.
                    // 파일 저장 가능 여부(/can-save-file)와는 다른 능력이라 따로 둔다 — Go 폴백 런처는
                    // 파일 저장은 못 해도 타일 프록시는 한다.
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("yes"));
                }
                else if (method == "GET" && path.StartsWith("/geocode?", StringComparison.Ordinal))
                {
                    byte[] found; string error;
                    if (TryGeocodePlace(QueryValue(path, "q"), QueryValue(path, "provider"), ReadGeocodeSpot(path), out found, out error))
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", found);
                    else
                        WriteResponse(stream, error == "kakao-key-required" ? "428 Precondition Required" : "502 Bad Gateway",
                            "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(error));
                }
                else if (method == "GET" && path == "/can-proxy-subway")
                {
                    // 환율의 /can-proxy-rates 와 같은 자리 — 능력마다 프로브를 따로 둔다.
                    // 런처 없이 연 브라우저는 이 주소에서 404 를 받고 화면이 이유를 밝힌다.
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("yes"));
                }
                else if (method == "GET" && path.StartsWith("/subway-position?", StringComparison.Ordinal))
                {
                    string line = (QueryValue(path, "line") ?? "").Trim();
                    if (!ValidSubwayLine(line))
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("subway-bad-request"));
                        return;
                    }
                    byte[] subwayData; bool subwayCached; string subwayError;
                    if (TrySubwayPosition(line, out subwayData, out subwayCached, out subwayError))
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", subwayData,
                            subwayCached ? "X-ClassDock-Subway-Cached: 1\r\n" : null);
                    else
                        WriteResponse(stream, subwayError == "subway-key-required" ? "428 Precondition Required" : "502 Bad Gateway",
                            "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(subwayError));
                }
                else if (method == "GET" && path == "/subway-key-status")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(SubwayKeyStatusJson()));
                }
                else if (method == "POST" && path.StartsWith("/subway-key", StringComparison.Ordinal))
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    string subwayKeyError;
                    bool subwayKeySaved = TrySetSubwayKey(Encoding.UTF8.GetString(body ?? new byte[0]),
                        QueryValue(path, "remember") == "1", out subwayKeyError);
                    WriteResponse(stream, subwayKeySaved ? "200 OK" : "400 Bad Request", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes(subwayKeySaved ? "ok" : subwayKeyError));
                }
                else if (method == "DELETE" && path == "/subway-key")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    bool subwayKeyCleared = ClearSubwayKey();
                    WriteResponse(stream, subwayKeyCleared ? "200 OK" : "500 Internal Server Error", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes(subwayKeyCleared ? "ok" : "subway-key-clear-failed"));
                }
                else if (method == "GET" && path == "/can-proxy-rates")
                {
                    // 환율 창이 "이 런처가 환율을 대신 받아 주는가" 를 묻는 자리. 타일 프록시와 다른 능력이라
                    // 프로브를 따로 둔다 — 지도 없이 환율만 쓰는 자리도 있고, 능력마다 따로 물어야 한다.
                    WriteResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("yes"));
                }
                else if (method == "GET" && path.StartsWith("/exchange-rate?", StringComparison.Ordinal))
                {
                    string queryError;
                    RateQuery rateQuery = ReadRateQuery(path, out queryError);
                    if (rateQuery == null)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(queryError));
                        return;
                    }
                    byte[] rateData; bool rateCached; string rateError;
                    if (TryExchangeRate(rateQuery, out rateData, out rateCached, out rateError))
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", rateData,
                            rateCached ? "X-ClassDock-Rate-Cached: 1\r\n" : null);
                    else
                        WriteResponse(stream, rateError == "rate-key-required" ? "428 Precondition Required" : "502 Bad Gateway",
                            "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(rateError));
                }
                else if (method == "GET" && path == "/exchange-rate-key-status")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(ExchangeRateKeyStatusJson()));
                }
                else if (method == "POST" && path.StartsWith("/exchange-rate-key", StringComparison.Ordinal))
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    string error;
                    bool saved = TrySetExchangeRateKey(Encoding.UTF8.GetString(body ?? new byte[0]), QueryValue(path, "remember") == "1", out error);
                    WriteResponse(stream, saved ? "200 OK" : "400 Bad Request", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes(saved ? "ok" : error));
                }
                else if (method == "DELETE" && path == "/exchange-rate-key")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    bool cleared = ClearExchangeRateKey();
                    WriteResponse(stream, cleared ? "200 OK" : "500 Internal Server Error", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes(cleared ? "ok" : "exchange-rate-key-clear-failed"));
                }
                else if (method == "GET" && path == "/map-search-key-status")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(KakaoMapKeyStatusJson()));
                }
                else if (method == "POST" && path.StartsWith("/map-search-key", StringComparison.Ordinal))
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    string error;
                    bool saved = TrySetKakaoMapKey(Encoding.UTF8.GetString(body ?? new byte[0]), QueryValue(path, "remember") == "1", out error);
                    WriteResponse(stream, saved ? "200 OK" : "400 Bad Request", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes(saved ? "ok" : error));
                }
                else if (method == "DELETE" && path == "/map-search-key")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    bool cleared = ClearKakaoMapKey();
                    WriteResponse(stream, cleared ? "200 OK" : "500 Internal Server Error", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes(cleared ? "ok" : "map-search-key-clear-failed"));
                }
                else if (method == "POST" && path.StartsWith("/map-search-provider", StringComparison.Ordinal))
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    string provider = QueryValue(path, "value") == "kakao" ? "kakao" : "osm";
                    bool saved = SaveMapSearchProvider(provider);
                    WriteResponse(stream, saved ? "200 OK" : "500 Internal Server Error", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes(saved ? "ok" : "map-search-provider-save-failed"));
                }
                else if (method == "GET" && path == "/tile-cache-status")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(TileCacheStatusJson()));
                }
                else if (method == "POST" && path == "/tile-cache-clear")
                {
                    if (!HasLocalActionHeader(headers))
                    {
                        WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("action-header-required"));
                        return;
                    }
                    bool cleared = ClearTileCache();
                    WriteResponse(stream, cleared ? "200 OK" : "500 Internal Server Error",
                        "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(cleared ? "ok" : "tile-cache-clear-failed"));
                }
                else if (method == "GET" && path.StartsWith("/python-session-file", StringComparison.Ordinal))
                {
                    // 실행이 만든 출력 파일 1개 내려주기(작업폴더 안으로 제한). 파일명은 프런트의 <a download> 가 지정.
                    byte[] fileData; string fileName;
                    if (TryGetSessionFile(QueryValue(path, "id"), QueryValue(path, "name"), out fileData, out fileName))
                        WriteResponse(stream, "200 OK", "application/octet-stream", fileData);
                    else
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("file-not-found"));
                }
                else if (method == "POST" && path == "/run-python")
                {
                    if (body.Length == 0)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("empty-body"));
                        return;
                    }
                    try
                    {
                        string json = RunPython(body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (PythonMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-python"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("run-failed: " + FlattenMessage(ex)));
                    }
                }
                else if (method == "POST" && path == "/run-python-bundle")
                {
                    if (body.Length == 0)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("empty-body"));
                        return;
                    }
                    try
                    {
                        string json = RunPythonBundle(body);
                        WriteResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }
                    catch (PythonMissingException)
                    {
                        WriteResponse(stream, "501 Not Implemented", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("no-python"));
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, "500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("run-failed: " + FlattenMessage(ex)));
                    }
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
    }

    static bool ValidHeartbeatId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 100) return false;
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) return false;
        }
        return true;
    }

    static void TouchHeartbeatClient(string id)
    {
        if (!ValidHeartbeatId(id)) return;
        lock (HeartbeatLock)
        {
            HeartbeatClients[id] = DateTime.UtcNow;
            HeartbeatSeen = true;
            NoHeartbeatClientsSince = DateTime.MaxValue;
        }
    }

    static void CloseHeartbeatClient(string id)
    {
        if (!ValidHeartbeatId(id)) return;
        lock (HeartbeatLock)
        {
            HeartbeatClients.Remove(id);
            if (HeartbeatSeen && HeartbeatClients.Count == 0) NoHeartbeatClientsSince = DateTime.UtcNow;
        }
    }

    // origin(포트) 무관 설정 저장소 읽기. 파일이 없거나 오류면 빈 JSON 객체를 돌려준다.
    static string LoadAppState()
    {
        lock (AppStateLock)
        {
            try
            {
                if (File.Exists(AppStatePath))
                {
                    string s = File.ReadAllText(AppStatePath, Encoding.UTF8);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }
            return "{}";
        }
    }

    // localStorage 스냅샷 JSON 을 그대로 저장한다(임시파일에 쓰고 교체해 부분 기록을 방지).
    static void SaveAppState(byte[] body)
    {
        if (body == null || body.Length == 0) return;
        if (body.Length > AppStateMaxBytes) throw new Exception("state-too-large");
        lock (AppStateLock)
        {
            string dir = Path.GetDirectoryName(AppStatePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            WriteFileAtomically(AppStatePath, body);
        }
    }

    static string DiagnosticJsonBody(byte[] body, int maxBytes)
    {
        if (body == null || body.Length == 0 || body.Length > maxBytes) throw new InvalidDataException("diagnostic-size");
        string json = Encoding.UTF8.GetString(body).Trim();
        if (json.Length < 2 || json[0] != '{' || json[json.Length - 1] != '}')
            throw new InvalidDataException("diagnostic-json");
        // JSONL 한 줄 형식을 지킨다. JSON.stringify가 만든 문자열 안 줄바꿈은 이미 \\n 으로 이스케이프된다.
        if (json.IndexOf('\r') >= 0 || json.IndexOf('\n') >= 0) throw new InvalidDataException("diagnostic-line");
        return json;
    }

    static void RotateDiagnosticLogsIfNeeded(int incomingBytes)
    {
        long current = File.Exists(DiagnosticsLogPath) ? new FileInfo(DiagnosticsLogPath).Length : 0;
        if (current + incomingBytes <= DiagnosticsLogMaxBytes) return;
        string oldest = DiagnosticsLogPath + "." + DiagnosticsArchiveCount;
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int index = DiagnosticsArchiveCount - 1; index >= 1; index--)
        {
            string from = DiagnosticsLogPath + "." + index;
            string to = DiagnosticsLogPath + "." + (index + 1);
            if (File.Exists(from)) File.Move(from, to);
        }
        if (File.Exists(DiagnosticsLogPath)) File.Move(DiagnosticsLogPath, DiagnosticsLogPath + ".1");
    }

    static void AppendDiagnosticEvent(byte[] body)
    {
        string json = DiagnosticJsonBody(body, DiagnosticsEventMaxBytes);
        byte[] line = Encoding.UTF8.GetBytes(json + "\n");
        lock (DiagnosticsLock)
        {
            Directory.CreateDirectory(DiagnosticsDir);
            RotateDiagnosticLogsIfNeeded(line.Length);
            using (FileStream stream = new FileStream(DiagnosticsLogPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                stream.Write(line, 0, line.Length);
        }
    }

    static void SaveDiagnosticSession(byte[] body)
    {
        string json = DiagnosticJsonBody(body, DiagnosticsSessionMaxBytes);
        lock (DiagnosticsLock)
        {
            Directory.CreateDirectory(DiagnosticsDir);
            string tmp = DiagnosticsSessionPath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(DiagnosticsSessionPath)) File.Delete(DiagnosticsSessionPath);
            File.Move(tmp, DiagnosticsSessionPath);
        }
    }

    static string LoadDiagnosticSession()
    {
        lock (DiagnosticsLock)
        {
            try { return File.Exists(DiagnosticsSessionPath) ? File.ReadAllText(DiagnosticsSessionPath, Encoding.UTF8) : "{}"; }
            catch { return "{}"; }
        }
    }

    static string LoadDiagnosticEvents(int limit)
    {
        limit = Math.Max(1, Math.Min(1000, limit));
        List<string> newest = new List<string>();
        lock (DiagnosticsLock)
        {
            for (int archive = 0; archive <= DiagnosticsArchiveCount && newest.Count < limit; archive++)
            {
                string path = archive == 0 ? DiagnosticsLogPath : DiagnosticsLogPath + "." + archive;
                if (!File.Exists(path)) continue;
                string[] lines;
                try { lines = File.ReadAllLines(path, Encoding.UTF8); }
                catch { continue; }
                for (int index = lines.Length - 1; index >= 0 && newest.Count < limit; index--)
                {
                    string line = (lines[index] ?? "").Trim();
                    if (line.Length >= 2 && line[0] == '{' && line[line.Length - 1] == '}') newest.Add(line);
                }
            }
        }
        newest.Reverse();
        return "[" + string.Join(",", newest.ToArray()) + "]";
    }

    static void ClearDiagnosticEvents()
    {
        lock (DiagnosticsLock)
        {
            for (int archive = 0; archive <= DiagnosticsArchiveCount; archive++)
            {
                string path = archive == 0 ? DiagnosticsLogPath : DiagnosticsLogPath + "." + archive;
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
    }

    static void OpenDiagnosticsFolder()
    {
        Directory.CreateDirectory(DiagnosticsDir);
        Process.Start(new ProcessStartInfo { FileName = DiagnosticsDir, UseShellExecute = true });
    }

    public static void RecordLauncherFatal(Exception error)
    {
        try
        {
            string message = FlattenMessage(error);
            string[] privateRoots = {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            foreach (string value in privateRoots)
                if (!string.IsNullOrEmpty(value)) message = message.Replace(value, "[경로]");
            if (message.Length > 1200) message = message.Substring(0, 1200) + "…";
            string json = "{\"version\":1,\"id\":" + JsonString("launcher-" + DateTime.UtcNow.Ticks)
                + ",\"sessionId\":\"launcher\",\"at\":" + JsonString(DateTime.UtcNow.ToString("o"))
                + ",\"level\":\"error\",\"type\":\"launcher_fatal\",\"message\":"
                + JsonString(string.IsNullOrEmpty(message) ? "ClassDock 런처 오류" : message)
                + ",\"context\":{\"screen\":\"launcher\"},\"details\":{\"exception\":"
                + JsonString(error == null ? "Exception" : error.GetType().Name) + "}}";
            AppendDiagnosticEvent(Encoding.UTF8.GetBytes(json));
        }
        catch { }
    }

    // 직전 인스턴스가 기록한 포트를 읽는다(없거나 이상하면 0).
    static int ReadInstancePort()
    {
        try
        {
            if (File.Exists(InstancePortPath))
            {
                int p;
                if (int.TryParse((File.ReadAllText(InstancePortPath) ?? "").Trim(), out p) && p > 0 && p <= 65535) return p;
            }
        }
        catch { }
        return 0;
    }

    // 현재 인스턴스가 실제로 바인딩한 포트를 기록한다.
    static void WriteInstancePort(int port)
    {
        try
        {
            string dir = Path.GetDirectoryName(InstancePortPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(InstancePortPath, port.ToString());
        }
        catch { }
    }

    // 앱 모드 설정을 읽는다. 파일이 없거나 읽지 못하면 지금까지의 동작(기본 브라우저)을 유지한다.
    static bool LoadAppMode()
    {
        try
        {
            if (File.Exists(AppModeConfigPath)) return (File.ReadAllText(AppModeConfigPath) ?? "").Trim() == "1";
        }
        catch { }
        return false;
    }

    static void SaveAppMode(bool on)
    {
        string dir = Path.GetDirectoryName(AppModeConfigPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(AppModeConfigPath, on ? "1" : "0", new UTF8Encoding(false));
    }

    // 기본 브라우저의 ProgId. 크롬을 쓰는데 엣지 창이 뜨는 일이 없도록 앱 모드 브라우저 선택에 참고한다.
    static string DefaultBrowserProgId()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice"))
            {
                if (key != null) return ((key.GetValue("ProgId") as string) ?? "").ToLowerInvariant();
            }
        }
        catch { }
        return "";
    }

    // --app= 창을 띄울 수 있는 크로미움 브라우저 경로. 없으면 null(= 앱 모드 불가).
    // 기본 브라우저가 크롬이면 크롬을, 그 밖에는 윈도우에 항상 있는 엣지를 먼저 찾는다.
    static string FindChromiumBrowser()
    {
        bool chromeFirst = DefaultBrowserProgId().Contains("chrome");
        string[] relatives = chromeFirst
            ? new string[] { @"Google\Chrome\Application\chrome.exe", @"Microsoft\Edge\Application\msedge.exe" }
            : new string[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" };
        string[] roots = new string[] {
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("LocalAppData")   // 크롬 사용자 단독 설치
        };
        foreach (string relative in relatives)
        {
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                try
                {
                    string exe = Path.Combine(root, relative);
                    if (File.Exists(exe)) return exe;
                }
                catch { }
            }
        }
        return null;
    }

    // 앱 화면을 브라우저로 연다. appMode 면 탭·주소창이 없는 --app 창으로,
    // 앱 모드가 꺼져 있거나 크로미움을 찾지 못하면 지금까지처럼 기본 브라우저로 연다.
    static bool OpenAppUrl(string url, bool appMode)
    {
        if (appMode)
        {
            string exe = FindChromiumBrowser();
            if (exe != null)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = exe;
                    psi.Arguments = "--app=" + url + " --window-size=1440,900";
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                    return true;
                }
                catch { }   // 앱 모드로 못 띄우면 기본 브라우저로 폴백해 최소한 화면은 뜨게 한다
            }
        }
        Process.Start(url);
        return false;
    }

    // 포트 파일만으로는 두 프로세스가 동시에 시작하는 순간을 막을 수 없으므로 OS 뮤텍스로 보완한다.
    // 뮤텍스 생성 자체가 실패하는 제한된 환경에서는 기존 포트 기반 동작을 유지한다.
    static bool TryAcquireSingleInstanceMutex()
    {
        try
        {
            bool createdNew;
            SingleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
            if (createdNew) return true;
            try { return SingleInstanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { return true; }
        }
        catch { return true; }
    }

    // 지정 포트에 이미 '우리' 서버가 떠 있는지 확인(/ping 응답의 X-App 헤더로 식별). 외부 앱이면 false.
    // 이제 "기록된 포트" 한 곳만 확인하므로, 우리 서버면 즉시 응답(수십 ms)·죽은 포트면 즉시 거부된다.
    // 드물게 외부 앱이 그 포트를 물고 응답만 안 하는 경우를 대비해 타임아웃은 짧게 둔다.
    static bool IsOurServerAt(int port)
    {
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/ping");
            req.Method = "GET";
            req.Timeout = 700;
            req.ReadWriteTimeout = 700;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                return resp.Headers["X-App"] == "classdock";
            }
        }
        catch { return false; }
    }

    static void WriteResponse(Stream stream, string status, string contentType, byte[] body)
    {
        WriteResponse(stream, status, contentType, body, null);
    }

    // extraHeader 는 이미 "이름: 값\r\n" 꼴로 끝난 ASCII 한 줄이어야 한다(환율 저장본 표시 등).
    static void WriteResponse(Stream stream, string status, string contentType, byte[] body, string extraHeader)
    {
        string header =
            "HTTP/1.1 " + status + "\r\n" +
            "Content-Type: " + contentType + "\r\n" +
            "Content-Length: " + body.Length + "\r\n" +
            (extraHeader ?? "") +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "X-App: classdock\r\n" +      // 우리 서버 식별용(중복 실행 시 단일 인스턴스 판별)
            ConnectionHeader(stream) +
            "\r\n";
        WriteHeaderAndBody(stream, Encoding.ASCII.GetBytes(header), body);
    }

    static string ConnectionHeader(Stream stream)
    {
        HttpConnectionStream connection = stream as HttpConnectionStream;
        return (connection != null && connection.KeepAlive) ? "Connection: keep-alive\r\nKeep-Alive: timeout=30\r\n" : "Connection: close\r\n";
    }

    // 헤더와 본문을 한 번에 보낸다. 두 번 나눠 쓰면 Nagle 과 상대의 지연 ACK 가 겹쳐 늦어질 수 있다.
    // 큰 본문(앱 HTML 33MB 등)까지 복사하면 메모리를 두 배로 쓰므로 작은 응답에만 합친다.
    static void WriteHeaderAndBody(Stream stream, byte[] headerBytes, byte[] body)
    {
        if (body.Length > 0 && body.Length <= 64 * 1024)
        {
            byte[] packet = new byte[headerBytes.Length + body.Length];
            Buffer.BlockCopy(headerBytes, 0, packet, 0, headerBytes.Length);
            Buffer.BlockCopy(body, 0, packet, headerBytes.Length, body.Length);
            stream.Write(packet, 0, packet.Length);
            return;
        }
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (body.Length > 0) stream.Write(body, 0, body.Length);
    }

    // Range 헤더 한 구간(bytes=시작-끝 / bytes=-뒤에서N)만 해석한다. 여러 구간 요청은 다루지 않는다
    // — 브라우저의 영상 재생은 항상 한 구간만 요청한다.
    static bool TryParseByteRange(string headerValue, long total, out long start, out long end)
    {
        start = 0;
        end = total - 1;
        string value = (headerValue ?? "").Trim();
        if (total <= 0 || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        value = value.Substring(6).Trim();
        if (value.IndexOf(',') >= 0) return false;
        int dash = value.IndexOf('-');
        if (dash < 0) return false;
        string left = value.Substring(0, dash).Trim();
        string right = value.Substring(dash + 1).Trim();
        if (left.Length == 0)
        {
            long suffix;
            if (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out suffix) || suffix <= 0) return false;
            start = suffix >= total ? 0 : total - suffix;
            end = total - 1;
            return true;
        }
        if (!long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0 || start >= total) return false;
        if (right.Length == 0) end = total - 1;
        else if (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start) return false;
        if (end >= total) end = total - 1;
        return true;
    }

    // 디스크에서 소켓으로 바로 흘려보낸다. byte[] 로 한 번에 읽으면 .NET 배열 상한(2GB) 때문에
    // 큰 영상은 아예 열 수 없고, 그보다 작아도 파일 크기만큼 메모리를 통째로 쓴다.
    // Range 를 지원해야 <video> 의 탐색(시킹)과 재생 중 이어받기가 동작한다.
    // 쓰기·삭제 공유를 허용해서 연다 — 기본값(FileShare.Read)은 다른 프로세스가 쓰기로 잡고 있는
    // 파일(실행 중인 파이썬의 로그 등)을 공유 위반으로 거부해, 폴더 동기화 전체를 막아 버린다.
    static void WriteFileStreamResponse(Stream stream, string full, string contentType, Dictionary<string, string> headers)
    {
        long total;
        using (FileStream probe = new FileStream(full, FileMode.Open, FileAccess.Read,
                                                 FileShare.ReadWrite | FileShare.Delete))
        {
            total = probe.Length;
        }
        long start = 0, end = total - 1;
        string rangeHeader = null;
        bool partial = headers != null && headers.TryGetValue("Range", out rangeHeader)
            && TryParseByteRange(rangeHeader, total, out start, out end);
        long length = total <= 0 ? 0 : end - start + 1;
        string header =
            "HTTP/1.1 " + (partial ? "206 Partial Content" : "200 OK") + "\r\n" +
            "Content-Type: " + contentType + "\r\n" +
            "Content-Length: " + length + "\r\n" +
            "Accept-Ranges: bytes\r\n" +
            (partial ? "Content-Range: bytes " + start + "-" + end + "/" + total + "\r\n" : "") +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "X-App: classdock\r\n" +
            ConnectionHeader(stream) +
            "\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (length <= 0) return;
        // 상대가 버퍼를 다 채우면 몇 분씩 읽지 않는다(재생을 멈춰 둔 영상). 그동안 Write 가 막히므로
        // 이 응답에서만 보내기 제한을 늘리고, 끝나면 원래 값으로 되돌린다.
        int savedTimeout = 0;
        bool timeoutChanged = false;
        try { savedTimeout = stream.WriteTimeout; stream.WriteTimeout = 10 * 60 * 1000; timeoutChanged = true; }
        catch { timeoutChanged = false; }
        long left = length;
        try
        {
            using (FileStream fs = new FileStream(full, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete, 1024 * 1024))
            {
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buffer = new byte[1024 * 1024];
                while (left > 0)
                {
                    int want = (int)Math.Min(buffer.Length, left);
                    int got = fs.Read(buffer, 0, want);
                    if (got <= 0) break;
                    stream.Write(buffer, 0, got);
                    left -= got;
                }
            }
        }
        // 머리말을 이미 보낸 뒤라 오류 응답으로 바꿔 쓸 수 없다(읽던 파일이 사라지거나 상대가 끊은 경우).
        catch { }
        finally
        {
            if (timeoutChanged) try { stream.WriteTimeout = savedTimeout; } catch { }
            // 약속한 Content-Length 를 채우지 못했으면 0 으로 메우는 대신 연결을 닫아
            // 상대가 잘린 응답임을 알게 한다.
            if (left > 0)
            {
                HttpConnectionStream truncated = stream as HttpConnectionStream;
                if (truncated != null) truncated.KeepAlive = false;
            }
        }
    }

    // sandbox(origin null) iframe 의 fetch 가 읽을 수 있도록 CORS 를 허용한 응답 — 지도 타일 프록시 전용
    static void WriteCorsResponse(Stream stream, string status, string contentType, byte[] body)
    {
        string header =
            "HTTP/1.1 " + status + "\r\n" +
            "Content-Type: " + contentType + "\r\n" +
            "Content-Length: " + body.Length + "\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Cache-Control: max-age=600\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "X-App: classdock\r\n" +
            ConnectionHeader(stream) +
            "\r\n";
        WriteHeaderAndBody(stream, Encoding.ASCII.GetBytes(header), body);
    }

    /* ===== 지도 타일 프록시 =====
       두 곳이 쓴다. (1) 노트북 PDF 의 지도 스냅샷 — 캡처 라이브러리가 타일을 fetch 로 다시 받아
       인라인하는데 sandbox iframe 의 fetch 는 Origin: null 로 나가 OSM 정책에 차단된다(화면의 <img>
       요청은 통과). (2) 지도 문서(.map) 의 배경 타일 — 이쪽은 화면 표시부터 프록시를 거친다.
       SSRF 방지를 위해 https + 알려진 타일 호스트만 허용한다.

       캐시는 두 층이다. 메모리는 같은 화면을 다시 그릴 때, 디스크는 "인터넷 없는 교실"을 위한 것이다.
       디스크가 있어야 하는 이유: 런처는 실행할 때마다 다른 포트를 잡으므로 브라우저 origin 이 매번
       바뀐다 → IndexedDB·Cache API 는 다음 실행에서 남의 저장소가 되어 못 읽는다. 한 번 본 지역을
       다음 수업에서도 열려면 서버가 파일로 들고 있어야 한다. */
    static readonly string[] TileProxyHosts = {
        "tile.openstreetmap.org", "basemaps.cartocdn.com", "tile.opentopomap.org",
        "server.arcgisonline.com", "tiles.stadiamaps.com", "tile.thunderforest.com"
    };
    sealed class TileMemoryEntry
    {
        public byte[] Data;
        public string Mime;
        public DateTime CachedAtUtc;
        public TileMemoryEntry(byte[] data, string mime, DateTime cachedAtUtc)
        { Data = data; Mime = mime; CachedAtUtc = cachedAtUtc; }
    }
    static readonly object TileCacheLock = new object();
    static readonly Dictionary<string, TileMemoryEntry> TileCache = new Dictionary<string, TileMemoryEntry>();
    static bool TileTlsReady;
    static readonly string TileCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassDock", "tile-cache");
    const long TileCacheMaxBytes = 400L * 1024 * 1024;
    static readonly TimeSpan TileCacheMaxAge = TimeSpan.FromDays(7);
    static readonly object TileDiskLock = new object();
    static long TileDiskBytes = -1;
    static readonly string[] TileCacheExtensions = { ".png", ".jpg", ".webp" };

    static string TileCacheKey(string url)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
            var text = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) text.Append(b.ToString("x2"));
            return text.ToString();
        }
    }
    static string TileCacheExtensionFor(string mime)
    {
        string value = (mime ?? "").ToLowerInvariant();
        if (value.Contains("jpeg") || value.Contains("jpg")) return ".jpg";
        if (value.Contains("webp")) return ".webp";
        return ".png";
    }
    // 파일이 한 폴더에 수만 개 쌓이면 탐색기도 열거도 느려진다 — 해시 앞 두 글자로 256칸에 나눈다.
    static string TileCacheFile(string key, string ext)
    {
        return Path.Combine(TileCacheDir, key.Substring(0, 2), key + ext);
    }
    static bool TryReadCachedTile(string url, out byte[] data, out string mime, out DateTime cachedAtUtc)
    {
        data = null; mime = "image/png"; cachedAtUtc = DateTime.MinValue;
        try
        {
            string key = TileCacheKey(url);
            foreach (string ext in TileCacheExtensions)
            {
                string file = TileCacheFile(key, ext);
                if (!File.Exists(file)) continue;
                cachedAtUtc = File.GetLastWriteTimeUtc(file);
                data = File.ReadAllBytes(file);
                mime = ext == ".jpg" ? "image/jpeg" : (ext == ".webp" ? "image/webp" : "image/png");
                return data.Length > 0;
            }
        }
        catch { data = null; }
        return false;
    }
    static bool IsTileCacheFresh(DateTime cachedAtUtc)
    {
        return cachedAtUtc != DateTime.MinValue && DateTime.UtcNow - cachedAtUtc <= TileCacheMaxAge;
    }
    static void WriteCachedTile(string url, byte[] data, string mime)
    {
        if (data == null || data.Length == 0) return;
        try
        {
            string key = TileCacheKey(url);
            string file = TileCacheFile(key, TileCacheExtensionFor(mime));
            lock (TileDiskLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                if (TileDiskBytes < 0) TileDiskBytes = TileCacheFiles().Sum(f => f.Length);
                long replacedBytes = 0;
                foreach (string ext in TileCacheExtensions)
                {
                    string previous = TileCacheFile(key, ext);
                    if (!File.Exists(previous)) continue;
                    long previousBytes = 0;
                    try { previousBytes = new FileInfo(previous).Length; } catch { }
                    if (string.Equals(previous, file, StringComparison.OrdinalIgnoreCase)) replacedBytes += previousBytes;
                    else
                    {
                        try { File.Delete(previous); replacedBytes += previousBytes; } catch { }
                    }
                }
                // 같은 타일을 동시에 두 번 받아도 반쯤 쓰인 파일이 남지 않게 임시 이름으로 쓰고 옮긴다.
                string temp = file + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                File.WriteAllBytes(temp, data);
                if (File.Exists(file)) File.Delete(file);
                File.Move(temp, file);
                TileDiskBytes = Math.Max(0, TileDiskBytes - replacedBytes) + data.LongLength;
                if (TileDiskBytes > TileCacheMaxBytes) SweepTileCache();
            }
        }
        catch { }
    }
    // 상한을 넘으면 오래 전에 받은 것부터 80% 아래로 내려갈 때까지 지운다.
    static FileInfo[] TileCacheFiles()
    {
        if (!Directory.Exists(TileCacheDir)) return new FileInfo[0];
        return new DirectoryInfo(TileCacheDir).GetFiles("*", SearchOption.AllDirectories)
            .Where(file => TileCacheExtensions.Contains(file.Extension.ToLowerInvariant())).ToArray();
    }
    static void SweepTileCache()
    {
        try
        {
            var files = TileCacheFiles();
            long total = 0;
            foreach (var file in files) total += file.Length;
            if (total > TileCacheMaxBytes)
            {
                Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                long target = (long)(TileCacheMaxBytes * 0.8);
                foreach (var file in files)
                {
                    if (total <= target) break;
                    long size = file.Length;
                    try { file.Delete(); total -= size; } catch { }
                }
            }
            TileDiskBytes = total;
        }
        catch { TileDiskBytes = -1; }
    }
    static string TileCacheStatusJson()
    {
        long bytes = 0; int count = 0;
        try
        {
            lock (TileDiskLock)
            {
                foreach (var file in TileCacheFiles()) { bytes += file.Length; count++; }
                TileDiskBytes = bytes;
            }
        }
        catch { }
        return "{\"files\":" + count + ",\"bytes\":" + bytes + ",\"maxBytes\":" + TileCacheMaxBytes + "}";
    }
    static bool ClearTileCache()
    {
        try
        {
            lock (TileDiskLock)
            {
                if (Directory.Exists(TileCacheDir)) Directory.Delete(TileCacheDir, true);
                TileDiskBytes = 0;
            }
            lock (TileCacheLock) TileCache.Clear();
            return true;
        }
        catch { return false; }
    }
    /* ===== 장소 이름 검색(지오코딩) =====
       브라우저에서 API 를 직접 부르지 않고 런처를 거친다. OSM 은 식별 User-Agent·호출 간격을 한 곳에서
       지키고, 카카오 REST API 키는 HTML·localStorage·작업공간에 노출하지 않는다. Windows 에서 '기억'
       을 켜면 DPAPI(CurrentUser)로 암호화해 같은 Windows 사용자만 복호화할 수 있게 저장한다. */
    const string DefaultGeocodeEndpoint = "https://nominatim.openstreetmap.org/search";
    const string GeocodeEndpointEnvironment = "CLASSDOCK_GEOCODER_URL";
    const string KakaoAddressEndpoint = "https://dapi.kakao.com/v2/local/search/address.json";
    const string KakaoKeywordEndpoint = "https://dapi.kakao.com/v2/local/search/keyword.json";
    /* 같은 REST 키로 쓰는 나머지 Local API. 신청이 따로 필요 없고 도메인 등록도 없다.
       category  = 반경 안의 학교·병원 같은 갈래별 장소
       coord2address / coord2regioncode = 찍은 자리의 주소·행정구역 */
    const string KakaoCategoryEndpoint = "https://dapi.kakao.com/v2/local/search/category.json";
    const string KakaoCoordAddressEndpoint = "https://dapi.kakao.com/v2/local/geo/coord2address.json";
    const string KakaoCoordRegionEndpoint = "https://dapi.kakao.com/v2/local/geo/coord2regioncode.json";
    /* 자동차 길찾기만 Local API 가 아닌 카카오모빌리티 쪽이다(호스트도 다르다). 그래도 키는 Local
       API 에 쓰던 그 REST 키 하나뿐이고, 콘솔에서 따로 켤 제품 설정은 없다(2026-08-25 실제 키로
       확인). 다만 하루 무료 몫이 이 API 에 따로 매겨지므로 부르는 자리를 한 곳(표시 잇는 길찾기)
       으로 좁히고, 같은 표시 배치로는 두 번 묻지 않게 화면에서 답을 담아 둔다. */
    const string KakaoDirectionsEndpoint = "https://apis-navi.kakaomobility.com/v1/directions";
    /* 장소 이름 검색으로 돌려줄 후보 수. 화면 목록(map-viewer.js MAP_SEARCH_RESULT_MAX)·Go 폴백
       런처(main.go geocodeResultLimit)와 같은 값이어야 한다 — 한쪽만 올리면 다른 쪽에서 잘린다. */
    const string GeocodeResultLimit = "8";      // 주소 뒤에 그대로 붙이는 값이라 문자열로 둔다
    const int GeocodeMinIntervalMs = 1100;      // 정책상 초당 1건 — 여유를 조금 둔다
    const int GeocodeMaxBytes = 512 * 1024;
    /* 길찾기는 roads[].vertexes 전체를 돌려주므로 장소 검색보다 정상 응답이 훨씬 크다. 카카오가
       허용하는 장거리·경유지 경로도 받을 수 있게 전용 상한을 두되, 무제한으로 읽지는 않는다. */
    const int DirectionsMaxBytes = 8 * 1024 * 1024;
    static readonly object GeocodeLock = new object();
    static DateTime GeocodeLastCall = DateTime.MinValue;
    static readonly Dictionary<string, byte[]> GeocodeCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    static readonly object KakaoMapKeyLock = new object();
    static readonly byte[] KakaoMapKeyEntropy = Encoding.UTF8.GetBytes("ClassDock.KakaoMapKey.v1");
    static readonly string KakaoMapKeyFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassDock", "kakao-map-key.bin");
    static readonly string MapSearchProviderFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassDock", "map-search-provider.txt");
    static bool KakaoMapKeyLoaded;
    static string KakaoMapKey = "";
    static Uri GeocodeEndpoint()
    {
        string configured = (Environment.GetEnvironmentVariable(GeocodeEndpointEnvironment) ?? "").Trim();
        Uri endpoint;
        if (!Uri.TryCreate(configured.Length > 0 ? configured : DefaultGeocodeEndpoint, UriKind.Absolute, out endpoint)
            || endpoint.Scheme != "https")
            Uri.TryCreate(DefaultGeocodeEndpoint, UriKind.Absolute, out endpoint);
        return endpoint;
    }
    static bool ValidKakaoMapKey(string value)
    {
        string key = (value ?? "").Trim();
        if (key.Length < 16 || key.Length > 128) return false;
        foreach (char ch in key)
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')) return false;
        return true;
    }
    static string CurrentKakaoMapKey()
    {
        lock (KakaoMapKeyLock)
        {
            if (!KakaoMapKeyLoaded)
            {
                KakaoMapKeyLoaded = true;
                try
                {
                    if (File.Exists(KakaoMapKeyFile))
                    {
                        byte[] encrypted = File.ReadAllBytes(KakaoMapKeyFile);
                        byte[] plain = ProtectedData.Unprotect(encrypted, KakaoMapKeyEntropy, DataProtectionScope.CurrentUser);
                        string key = Encoding.UTF8.GetString(plain).Trim();
                        if (ValidKakaoMapKey(key)) KakaoMapKey = key;
                    }
                }
                catch { KakaoMapKey = ""; }
            }
            return KakaoMapKey;
        }
    }
    static bool KakaoMapKeyRemembered()
    {
        try { return File.Exists(KakaoMapKeyFile) && CurrentKakaoMapKey().Length > 0; }
        catch { return false; }
    }
    static string CurrentMapSearchProvider()
    {
        try
        {
            if (File.Exists(MapSearchProviderFile))
            {
                string saved = File.ReadAllText(MapSearchProviderFile, Encoding.UTF8).Trim().ToLowerInvariant();
                if (saved == "kakao" || saved == "osm") return saved;
            }
        }
        catch { }
        // 이 기능을 넣기 전에 이미 키를 저장한 사용자는 앱 모드에서 바로 카카오를 이어 쓴다.
        return CurrentKakaoMapKey().Length > 0 ? "kakao" : "osm";
    }
    static bool SaveMapSearchProvider(string value)
    {
        string provider = value == "kakao" ? "kakao" : "osm";
        try
        {
            string dir = Path.GetDirectoryName(MapSearchProviderFile);
            Directory.CreateDirectory(dir);
            File.WriteAllText(MapSearchProviderFile, provider, new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }
    static string KakaoMapKeyStatusJson()
    {
        return "{\"hasKey\":" + (CurrentKakaoMapKey().Length > 0 ? "true" : "false")
            + ",\"remembered\":" + (KakaoMapKeyRemembered() ? "true" : "false")
            + ",\"persistentSupported\":true,\"provider\":" + JsonString(CurrentMapSearchProvider()) + "}";
    }
    static bool SaveProtectedKakaoMapKey(string key)
    {
        try
        {
            string dir = Path.GetDirectoryName(KakaoMapKeyFile);
            Directory.CreateDirectory(dir);
            byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), KakaoMapKeyEntropy, DataProtectionScope.CurrentUser);
            string temp = KakaoMapKeyFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            if (File.Exists(KakaoMapKeyFile)) File.Delete(KakaoMapKeyFile);
            File.Move(temp, KakaoMapKeyFile);
            return true;
        }
        catch { return false; }
    }
    static bool ClearKakaoMapKey()
    {
        lock (KakaoMapKeyLock)
        {
            KakaoMapKeyLoaded = true;
            KakaoMapKey = "";
            try { if (File.Exists(KakaoMapKeyFile)) File.Delete(KakaoMapKeyFile); }
            catch { return false; }
        }
        lock (GeocodeLock) GeocodeCache.Clear();
        SaveMapSearchProvider("osm");
        return true;
    }
    static bool TrySetKakaoMapKey(string value, bool remember, out string error)
    {
        string key = (value ?? "").Trim();
        error = "kakao-key-invalid";
        if (!ValidKakaoMapKey(key)) return false;
        byte[] probe;
        if (!TryFetchGeocode("서울특별시 중구 세종대로 110", "kakao-address", key, null, out probe, out error)) return false;
        string previous = CurrentKakaoMapKey();
        lock (KakaoMapKeyLock)
        {
            if (remember)
            {
                if (!SaveProtectedKakaoMapKey(key)) { KakaoMapKey = previous; error = "kakao-key-save-failed"; return false; }
            }
            else
            {
                try { if (File.Exists(KakaoMapKeyFile)) File.Delete(KakaoMapKeyFile); }
                catch { KakaoMapKey = previous; error = "kakao-key-save-failed"; return false; }
            }
            KakaoMapKeyLoaded = true;
            KakaoMapKey = key;
        }
        lock (GeocodeLock) GeocodeCache.Clear();
        SaveMapSearchProvider("kakao");
        error = "";
        return true;
    }
    /* 검색어 말고 좌표로 부르는 요청(반경 시설·좌표→주소)의 딸린 값.
       브라우저가 보낸 문자열을 그대로 URL 에 붙이지 않고 여기서 숫자·코드 꼴만 통과시킨다. */
    class GeocodeSpot
    {
        public string X = "";
        public string Y = "";
        public string Radius = "";
        public string Category = "";
        public string Page = "";
        // 길찾기만 점이 둘 이상이다 — 도착점(X2·Y2)과 사이에 들르는 곳(Via, "x,y|x,y" 꼴).
        public string X2 = "";
        public string Y2 = "";
        public string Via = "";
        public string Priority = "";
        public string Avoid = "";
        public string Fuel = "";
        public string Hipass = "";
        public string Alternatives = "";
        public bool HasPoint { get { return X.Length > 0 && Y.Length > 0; } }
        public bool HasEnd { get { return X2.Length > 0 && Y2.Length > 0; } }
        public string CacheKey
        {
            get { return X + "|" + Y + "|" + Radius + "|" + Category + "|" + Page + "|" + X2 + "|" + Y2 + "|" + Via
                + "|" + Priority + "|" + Avoid + "|" + Fuel + "|" + Hipass + "|" + Alternatives; }
        }
    }
    static string GeocodeNumber(string value, double min, double max)
    {
        double parsed;
        if (!double.TryParse((value ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return "";
        if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < min || parsed > max) return "";
        return parsed.ToString("0.######", CultureInfo.InvariantCulture);
    }
    /* 들르는 곳 목록("x,y|x,y")도 좌표와 같은 규칙으로 다시 짠다 — 브라우저가 보낸 글자를
       그대로 붙이지 않고 숫자로 읽힌 것만 카카오가 받는 꼴로 되돌려 준다. 카카오 상한이 5 개다. */
    const int GeocodeViaMax = 5;
    static string GeocodeVia(string raw)
    {
        List<string> points = new List<string>();
        foreach (string piece in (raw ?? "").Split('|'))
        {
            if (points.Count >= GeocodeViaMax) break;
            string[] parts = piece.Split(',');
            if (parts.Length != 2) continue;
            string x = GeocodeNumber(parts[0], -180, 180);
            string y = GeocodeNumber(parts[1], -85, 85);
            if (x.Length == 0 || y.Length == 0) continue;
            points.Add(x + "," + y);
        }
        return string.Join("|", points.ToArray());
    }
    static string DirectionsChoice(string raw, string fallback, params string[] allowed)
    {
        string value = (raw ?? "").Trim();
        foreach (string item in allowed)
            if (string.Equals(value, item, StringComparison.OrdinalIgnoreCase)) return item;
        return fallback;
    }
    static string DirectionsAvoid(string raw)
    {
        string[] allowed = { "ferries", "toll", "motorway", "schoolzone", "uturn" };
        HashSet<string> requested = new HashSet<string>((raw ?? "").Split('|'), StringComparer.OrdinalIgnoreCase);
        List<string> clean = new List<string>();
        foreach (string item in allowed) if (requested.Contains(item)) clean.Add(item);
        return string.Join("|", clean.ToArray());
    }
    static GeocodeSpot ReadGeocodeSpot(string path)
    {
        GeocodeSpot spot = new GeocodeSpot();
        spot.X = GeocodeNumber(QueryValue(path, "x"), -180, 180);
        spot.Y = GeocodeNumber(QueryValue(path, "y"), -85, 85);
        spot.X2 = GeocodeNumber(QueryValue(path, "x2"), -180, 180);
        spot.Y2 = GeocodeNumber(QueryValue(path, "y2"), -85, 85);
        spot.Via = GeocodeVia(QueryValue(path, "via"));
        spot.Priority = DirectionsChoice(QueryValue(path, "priority"), "RECOMMEND", "RECOMMEND", "TIME", "DISTANCE");
        spot.Avoid = DirectionsAvoid(QueryValue(path, "avoid"));
        spot.Fuel = DirectionsChoice(QueryValue(path, "fuel"), "GASOLINE", "GASOLINE", "DIESEL", "LPG");
        spot.Hipass = DirectionsChoice(QueryValue(path, "hipass"), "false", "true", "false");
        spot.Alternatives = DirectionsChoice(QueryValue(path, "alternatives"), "false", "true", "false");
        spot.Radius = GeocodeNumber(QueryValue(path, "radius"), 1, 20000);      // 카카오 반경 상한
        spot.Page = GeocodeNumber(QueryValue(path, "page"), 1, 3);
        // 카카오 카테고리 코드는 언제나 영문 두 글자 + 숫자 한 글자다(SC4·CS2 …).
        string category = (QueryValue(path, "category") ?? "").Trim().ToUpperInvariant();
        if (category.Length == 3 && category[0] >= 'A' && category[0] <= 'Z'
            && category[1] >= 'A' && category[1] <= 'Z' && category[2] >= '0' && category[2] <= '9')
            spot.Category = category;
        return spot;
    }
    static bool TryFetchGeocode(string q, string provider, string kakaoKey, GeocodeSpot spot, out byte[] data, out string error)
    {
        data = null; error = "geocode-failed";
        bool kakao = provider.StartsWith("kakao-", StringComparison.Ordinal);
        if (spot == null) spot = new GeocodeSpot();
        try
        {
            if (!kakao)
            {
                lock (GeocodeLock)
                {
                    double waited = (DateTime.UtcNow - GeocodeLastCall).TotalMilliseconds;
                    if (waited < GeocodeMinIntervalMs) Thread.Sleep((int)(GeocodeMinIntervalMs - waited));
                    GeocodeLastCall = DateTime.UtcNow;
                }
            }
            if (!TileTlsReady)
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                TileTlsReady = true;
            }
            string url;
            if (provider == "kakao-coord2address" || provider == "kakao-coord2region")
            {
                string endpoint = provider == "kakao-coord2region" ? KakaoCoordRegionEndpoint : KakaoCoordAddressEndpoint;
                url = endpoint + "?x=" + spot.X + "&y=" + spot.Y;
            }
            else if (provider == "kakao-directions")
            {
                url = KakaoDirectionsEndpoint + "?origin=" + spot.X + "," + spot.Y
                    + "&destination=" + spot.X2 + "," + spot.Y2
                    + (spot.Via.Length > 0 ? "&waypoints=" + Uri.EscapeDataString(spot.Via) : "")
                    + "&priority=" + spot.Priority
                    + (spot.Avoid.Length > 0 ? "&avoid=" + Uri.EscapeDataString(spot.Avoid) : "")
                    + "&car_fuel=" + spot.Fuel + "&car_hipass=" + spot.Hipass
                    + "&alternatives=" + spot.Alternatives + "&road_details=false&summary=false";
            }
            else if (provider == "kakao-category")
            {
                url = KakaoCategoryEndpoint + "?category_group_code=" + spot.Category
                    + "&x=" + spot.X + "&y=" + spot.Y
                    + "&radius=" + (spot.Radius.Length > 0 ? spot.Radius : "1000")
                    + "&size=15&sort=distance&page=" + (spot.Page.Length > 0 ? spot.Page : "1");
            }
            else if (kakao)
            {
                string endpoint = provider == "kakao-keyword" ? KakaoKeywordEndpoint : KakaoAddressEndpoint;
                // 키워드 검색에 기준점이 오면 그 둘레만 본다 — 갈래에 없는 말로 주변 시설을 찾는
                // 길이라(로또·빵집 …) 갈래 검색과 같은 쪽수(15개·페이지)로 받는다.
                bool around = provider == "kakao-keyword" && spot.HasPoint;
                url = endpoint + "?size=" + (around ? "15" : GeocodeResultLimit) + "&query=" + Uri.EscapeDataString(q);
                if (around)
                    url += "&x=" + spot.X + "&y=" + spot.Y + "&sort=distance"
                        + "&page=" + (spot.Page.Length > 0 ? spot.Page : "1")
                        + (spot.Radius.Length > 0 ? "&radius=" + spot.Radius : "");
            }
            else if (provider == "osm-reverse")
            {
                // Nominatim 의 역지오코딩은 같은 서버의 이웃 경로다(/search → /reverse).
                string basePath = GeocodeEndpoint().GetLeftPart(UriPartial.Path);
                if (basePath.EndsWith("/search", StringComparison.Ordinal))
                    basePath = basePath.Substring(0, basePath.Length - "/search".Length) + "/reverse";
                url = basePath + "?format=jsonv2&zoom=18&accept-language=ko&lat=" + spot.Y + "&lon=" + spot.X;
            }
            else
            {
                Uri endpoint = GeocodeEndpoint();
                url = endpoint.GetLeftPart(UriPartial.Path) + "?format=jsonv2&limit=" + GeocodeResultLimit + "&accept-language=ko&q=" + Uri.EscapeDataString(q);
            }
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "ClassDock/1.0 (local classroom app; https://github.com/songhwaseong/ClassDock)";
            request.Accept = "application/json";
            if (kakao) request.Headers[HttpRequestHeader.Authorization] = "KakaoAK " + kakaoKey;
            request.Timeout = 12000;
            request.ReadWriteTimeout = 12000;
            using (WebResponse response = request.GetResponse())
            using (Stream body = response.GetResponseStream())
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[8192];
                int read; long total = 0;
                int maxBytes = provider == "kakao-directions" ? DirectionsMaxBytes : GeocodeMaxBytes;
                while ((read = body.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > maxBytes) { error = "geocode-too-large"; return false; }
                    buffer.Write(chunk, 0, read);
                }
                data = buffer.ToArray();
            }
            return true;
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            error = kakao && response != null && (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                ? "kakao-key-invalid" : "geocode-failed";
            data = null; return false;
        }
        catch { error = "geocode-failed"; data = null; return false; }
    }
    static readonly string[] GeocodeProviders = {
        "osm", "osm-reverse", "kakao-address", "kakao-keyword",
        "kakao-category", "kakao-coord2address", "kakao-coord2region", "kakao-directions"
    };
    static bool TryGeocodePlace(string query, string requestedProvider, GeocodeSpot spot, out byte[] data, out string error)
    {
        data = null; error = "geocode-failed";
        if (spot == null) spot = new GeocodeSpot();
        string q = (query ?? "").Trim();
        string provider = Array.IndexOf(GeocodeProviders, requestedProvider ?? "") >= 0 ? requestedProvider : "osm";
        // 좌표로 부르는 갈래는 검색어 대신 기준점이 있어야 한다.
        bool needsPoint = provider == "kakao-category" || provider == "kakao-coord2address"
            || provider == "kakao-coord2region" || provider == "osm-reverse" || provider == "kakao-directions";
        if (needsPoint)
        {
            if (!spot.HasPoint) { error = "geocode-bad-point"; return false; }
            if (provider == "kakao-category" && spot.Category.Length == 0) { error = "geocode-bad-category"; return false; }
            // 길찾기는 출발점만으로는 뜻이 없다 — 도착점이 빠지면 카카오에 묻지 않고 여기서 끊는다.
            if (provider == "kakao-directions" && !spot.HasEnd) { error = "geocode-bad-point"; return false; }
        }
        else if (q.Length == 0 || q.Length > 200) { error = "geocode-bad-query"; return false; }
        string kakaoKey = "";
        if (provider.StartsWith("kakao-", StringComparison.Ordinal))
        {
            kakaoKey = CurrentKakaoMapKey();
            if (kakaoKey.Length == 0) { error = "kakao-key-required"; return false; }
        }
        string cacheKey = provider + "\n" + q + "\n" + spot.CacheKey;
        /* 길찾기에는 현재 교통 속도·통제 상황이 반영된다. 화면 안의 짧은 중복은 JS가 막으므로,
           런처의 무기한 장소 검색 캐시에는 넣지 않아 저장 지도를 다시 열면 새 답을 받게 한다. */
        bool cacheable = provider != "kakao-directions";
        if (cacheable)
            lock (GeocodeLock) if (GeocodeCache.TryGetValue(cacheKey, out data)) return true;
        if (!TryFetchGeocode(q, provider, kakaoKey, spot, out data, out error)) return false;
        if (cacheable) lock (GeocodeLock)
        {
            if (GeocodeCache.Count > 200) GeocodeCache.Clear();
            GeocodeCache[cacheKey] = data;
        }
        return true;
    }

    /* ===== 환율 =====
       지도 타일과 같은 이유로 런처가 대신 받아 온다 — 실행마다 포트(=origin)가 바뀌어 브라우저
       저장소는 다음 수업까지 남지 않고, 수출입은행은 CORS 를 열어 주지 않아 브라우저에서 직접
       부를 수도 없다. 인증키도 HTML·작업공간에 두지 않고 여기서만 헤더처럼 붙인다(카카오 키와 같은 규칙).

       런처는 받아 온 JSON 을 **그대로** 돌려준다. 뜻풀이는 src/js/exchange-rate.js 한 곳에만 둔다 —
       런처가 둘(이 파일·main.go)이라 파싱을 옮겨 오면 같은 규칙을 두 언어로 두 번 틀리게 된다. */
    const string KoreaEximRateEndpoint = "https://oapi.koreaexim.go.kr/site/program/financial/exchangeJSON";
    const string EcbRateEndpoint = "https://api.frankfurter.dev/v1/";
    const long RateMaxBytes = 512 * 1024;
    const long RateCacheMaxBytes = 20L * 1024 * 1024;
    // 지난 날짜의 환율은 다시 바뀌지 않는다. 오늘 값만 잠깐 뒤에 다시 받아 본다(고시는 오전 11시 무렵).
    static readonly TimeSpan RateTodayCacheMaxAge = TimeSpan.FromMinutes(20);
    static readonly object RateCacheLock = new object();
    static readonly string RateCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassDock", "rate-cache");
    static readonly object ExchangeRateKeyLock = new object();
    static readonly byte[] ExchangeRateKeyEntropy = Encoding.UTF8.GetBytes("ClassDock.ExchangeRateKey.v1");
    static readonly string ExchangeRateKeyFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassDock", "exchange-rate-key.bin");
    static bool ExchangeRateKeyLoaded;
    static string ExchangeRateKey = "";

    static bool ValidExchangeRateKey(string value)
    {
        string key = (value ?? "").Trim();
        if (key.Length < 12 || key.Length > 128) return false;
        foreach (char ch in key)
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')) return false;
        return true;
    }
    static string CurrentExchangeRateKey()
    {
        lock (ExchangeRateKeyLock)
        {
            if (!ExchangeRateKeyLoaded)
            {
                ExchangeRateKeyLoaded = true;
                try
                {
                    if (File.Exists(ExchangeRateKeyFile))
                    {
                        byte[] encrypted = File.ReadAllBytes(ExchangeRateKeyFile);
                        byte[] plain = ProtectedData.Unprotect(encrypted, ExchangeRateKeyEntropy, DataProtectionScope.CurrentUser);
                        string key = Encoding.UTF8.GetString(plain).Trim();
                        if (ValidExchangeRateKey(key)) ExchangeRateKey = key;
                    }
                }
                catch { ExchangeRateKey = ""; }
            }
            return ExchangeRateKey;
        }
    }
    static bool ExchangeRateKeyRemembered()
    {
        try { return File.Exists(ExchangeRateKeyFile) && CurrentExchangeRateKey().Length > 0; }
        catch { return false; }
    }
    static string ExchangeRateKeyStatusJson()
    {
        return "{\"hasKey\":" + (CurrentExchangeRateKey().Length > 0 ? "true" : "false")
            + ",\"remembered\":" + (ExchangeRateKeyRemembered() ? "true" : "false")
            + ",\"persistentSupported\":true}";
    }
    static bool SaveProtectedExchangeRateKey(string key)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExchangeRateKeyFile));
            byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), ExchangeRateKeyEntropy, DataProtectionScope.CurrentUser);
            string temp = ExchangeRateKeyFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            if (File.Exists(ExchangeRateKeyFile)) File.Delete(ExchangeRateKeyFile);
            File.Move(temp, ExchangeRateKeyFile);
            return true;
        }
        catch { return false; }
    }
    static bool ClearExchangeRateKey()
    {
        lock (ExchangeRateKeyLock)
        {
            ExchangeRateKeyLoaded = true;
            ExchangeRateKey = "";
            try { if (File.Exists(ExchangeRateKeyFile)) File.Delete(ExchangeRateKeyFile); }
            catch { return false; }
        }
        return true;
    }
    static bool TrySetExchangeRateKey(string value, bool remember, out string error)
    {
        string key = (value ?? "").Trim();
        error = "rate-key-invalid";
        if (!ValidExchangeRateKey(key)) return false;
        /* 시험 조회는 '가장 가까운 지난 영업일' 로 건다. 오늘로 걸면 주말이나 오전 고시 전에는
           키가 멀쩡해도 빈 배열이 와서 "키가 틀렸다" 고 잘못 알린다. */
        RateQuery probe = new RateQuery();
        probe.Source = "koreaexim";
        probe.Date = LastWeekdayCompact();
        byte[] body; string fetchError;
        if (!TryFetchExchangeRate(probe, key, out body, out fetchError)) { error = fetchError; return false; }
        string text = Encoding.UTF8.GetString(body);
        if (text.Contains("\"result\":3")) { error = "rate-key-invalid"; return false; }
        if (text.Contains("\"result\":4")) { error = "rate-limit-reached"; return false; }
        string previous = CurrentExchangeRateKey();
        lock (ExchangeRateKeyLock)
        {
            if (remember)
            {
                if (!SaveProtectedExchangeRateKey(key)) { ExchangeRateKey = previous; error = "rate-key-save-failed"; return false; }
            }
            else
            {
                try { if (File.Exists(ExchangeRateKeyFile)) File.Delete(ExchangeRateKeyFile); }
                catch { ExchangeRateKey = previous; error = "rate-key-save-failed"; return false; }
            }
            ExchangeRateKeyLoaded = true;
            ExchangeRateKey = key;
        }
        error = "";
        return true;
    }
    // 키 시험용 — 오늘부터 거슬러 올라가 처음 만나는 평일(YYYYMMDD). 공휴일까지는 보지 않는다.
    static string LastWeekdayCompact()
    {
        DateTime day = DateTime.Now.Date.AddDays(-1);
        for (int i = 0; i < 7; i++)
        {
            if (day.DayOfWeek != DayOfWeek.Saturday && day.DayOfWeek != DayOfWeek.Sunday) break;
            day = day.AddDays(-1);
        }
        return day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    /* 브라우저가 보낸 문자열을 URL 에 그대로 붙이지 않고 여기서 꼴부터 맞춰 본다(지오코딩의 GeocodeSpot 과 같은 규칙). */
    class RateQuery
    {
        public string Source = "";
        public string Date = "";        // koreaexim: YYYYMMDD · ecb: YYYY-MM-DD
        public string Start = "";
        public string End = "";
        public string Symbols = "";
        public string CacheKey
        {
            get { return Source + "|" + Date + "|" + Start + "|" + End + "|" + Symbols; }
        }
        // 캐시를 영구로 둘지 가르는 기준일 — 조회 구간에서 가장 나중 날짜.
        public string NewestDay
        {
            get { return End.Length > 0 ? End : Date; }
        }
    }
    static string RateDate(string value, bool compact)
    {
        string text = (value ?? "").Trim();
        DateTime parsed;
        if (!DateTime.TryParseExact(text, compact ? "yyyyMMdd" : "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) return "";
        return text;
    }
    static string RateSymbols(string value)
    {
        string text = (value ?? "").Trim().ToUpperInvariant();
        if (text.Length == 0 || text.Length > 60) return "";
        foreach (string part in text.Split(','))
        {
            if (part.Length < 3 || part.Length > 4) return "";
            foreach (char ch in part) if (ch < 'A' || ch > 'Z') return "";
        }
        return text;
    }
    static readonly string[] RateSources = { "koreaexim", "ecb", "ecb-series" };
    static RateQuery ReadRateQuery(string path, out string error)
    {
        error = "rate-bad-request";
        RateQuery query = new RateQuery();
        string source = (QueryValue(path, "source") ?? "").Trim();
        if (Array.IndexOf(RateSources, source) < 0) return null;
        query.Source = source;
        if (source == "ecb-series")
        {
            query.Start = RateDate(QueryValue(path, "start"), false);
            query.End = RateDate(QueryValue(path, "end"), false);
            query.Symbols = RateSymbols(QueryValue(path, "symbols"));
            if (query.Start.Length == 0 || query.End.Length == 0 || query.Symbols.Length == 0) return null;
            if (String.CompareOrdinal(query.Start, query.End) > 0) return null;
        }
        else
        {
            query.Date = RateDate(QueryValue(path, "date"), source == "koreaexim");
            if (query.Date.Length == 0) return null;
        }
        error = "";
        return query;
    }

    static string RateCacheFile(string cacheKey)
    {
        return Path.Combine(RateCacheDir, TileCacheKey(cacheKey) + ".json");
    }
    /* 지난 날짜의 값은 다시 바뀌지 않으므로 그대로 믿고, 오늘 값만 20분이 지나면 새로 받는다.
       '오늘' 은 이 PC 의 달력 날짜다 — 수출입은행 고시가 한국 시간 기준이고 교실 PC 도 같은 시간대다. */
    static bool RateCacheFresh(RateQuery query, DateTime writtenUtc)
    {
        string newest = (query.NewestDay ?? "").Replace("-", "");
        string today = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (newest.Length == 8 && String.CompareOrdinal(newest, today) < 0) return true;
        return DateTime.UtcNow - writtenUtc <= RateTodayCacheMaxAge;
    }
    static bool TryReadCachedRate(RateQuery query, out byte[] data, out bool fresh)
    {
        data = null; fresh = false;
        try
        {
            lock (RateCacheLock)
            {
                string file = RateCacheFile(query.CacheKey);
                if (!File.Exists(file)) return false;
                data = File.ReadAllBytes(file);
                fresh = RateCacheFresh(query, File.GetLastWriteTimeUtc(file));
            }
            return data != null && data.Length > 0;
        }
        catch { data = null; fresh = false; return false; }
    }
    /* 잘못을 담은 응답은 캐시하지 않는다 — 인증 오류(result 3)나 아직 고시 전인 빈 배열을
       디스크에 남기면 키를 고치거나 11시가 지난 뒤에도 같은 잘못이 계속 되살아난다. */
    static bool RateBodyCacheable(byte[] data)
    {
        if (data == null || data.Length < 8) return false;
        string text = Encoding.UTF8.GetString(data);
        if (text.Trim() == "[]") return false;
        return !text.Contains("\"result\":2") && !text.Contains("\"result\":3") && !text.Contains("\"result\":4");
    }
    static void WriteCachedRate(RateQuery query, byte[] data)
    {
        if (!RateBodyCacheable(data)) return;
        try
        {
            lock (RateCacheLock)
            {
                Directory.CreateDirectory(RateCacheDir);
                string file = RateCacheFile(query.CacheKey);
                string temp = file + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                File.WriteAllBytes(temp, data);
                if (File.Exists(file)) File.Delete(file);
                File.Move(temp, file);
                SweepRateCache();
            }
        }
        catch { }
    }
    // 하루치 JSON 이 10KB 남짓이라 좀처럼 차지 않지만, 기간 조회를 반복하면 늘어난다 — 오래된 것부터 지운다.
    static void SweepRateCache()
    {
        try
        {
            if (!Directory.Exists(RateCacheDir)) return;
            var files = new DirectoryInfo(RateCacheDir).GetFiles("*.json");
            long total = files.Sum(f => f.Length);
            if (total <= RateCacheMaxBytes) return;
            long target = (long)(RateCacheMaxBytes * 0.8);
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= target) break;
                long size = file.Length;
                try { file.Delete(); total -= size; } catch { }
            }
        }
        catch { }
    }

    static bool TryFetchExchangeRate(RateQuery query, string key, out byte[] data, out string error)
    {
        data = null; error = "rate-failed";
        try
        {
            if (!TileTlsReady)
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                TileTlsReady = true;
            }
            string url;
            if (query.Source == "koreaexim")
                url = KoreaEximRateEndpoint + "?authkey=" + Uri.EscapeDataString(key)
                    + "&searchdate=" + query.Date + "&data=AP01";
            else if (query.Source == "ecb-series")
                url = EcbRateEndpoint + query.Start + ".." + query.End + "?symbols=" + query.Symbols;
            else
                url = EcbRateEndpoint + query.Date;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "ClassDock/1.0 (local classroom app; https://github.com/songhwaseong/ClassDock)";
            request.Accept = "application/json";
            request.Timeout = 12000;
            request.ReadWriteTimeout = 12000;
            using (WebResponse response = request.GetResponse())
            using (Stream body = response.GetResponseStream())
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[8192];
                int read; long total = 0;
                while ((read = body.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > RateMaxBytes) { error = "rate-too-large"; return false; }
                    buffer.Write(chunk, 0, read);
                }
                data = buffer.ToArray();
            }
            error = "";
            return true;
        }
        catch (WebException ex)
        {
            /* Frankfurter 는 자료가 없는 날짜에 404 로 답한다 — 연결이 끊긴 것과 구분해 알려야
               "인터넷을 확인하세요" 라는 엉뚱한 안내가 뜨지 않는다. */
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null && response.StatusCode == HttpStatusCode.NotFound) error = "rate-no-data";
            data = null; return false;
        }
        catch { data = null; return false; }
    }

    /* 캐시 → 없거나 낡았으면 새로 받기 → 받기에 실패하면 낡은 캐시라도 돌려주기(타일 프록시와 같은 순서).
       cached=true 는 "받아오지 못해 저장해 둔 값을 대신 내준다" 는 뜻이고, 화면은 이걸 보고
       '○○일 기준 저장본' 이라고 밝힌다. */
    static bool TryExchangeRate(RateQuery query, out byte[] data, out bool cached, out string error)
    {
        data = null; cached = false; error = "rate-failed";
        string key = "";
        if (query.Source == "koreaexim")
        {
            key = CurrentExchangeRateKey();
            if (key.Length == 0) { error = "rate-key-required"; return false; }
        }
        byte[] stored; bool fresh;
        bool hasStored = TryReadCachedRate(query, out stored, out fresh);
        if (hasStored && fresh) { data = stored; error = ""; return true; }
        byte[] fetched; string fetchError;
        if (TryFetchExchangeRate(query, key, out fetched, out fetchError))
        {
            WriteCachedRate(query, fetched);
            data = fetched; error = "";
            return true;
        }
        if (hasStored) { data = stored; cached = true; error = ""; return true; }
        error = fetchError;
        return false;
    }

    /* ===== 지하철 실시간 열차 위치 =====
       main.go 의 같은 이름 상수·목록과 짝이다. 런처가 대신 받는 까닭이 환율과 조금 다르다 —
       이 API 는 CORS 를 열어 두었지만 https 를 아예 안 받는다. 브라우저에서 직접 부르면
       https 로 연 화면에서 막히고, 무엇보다 인증키가 화면 코드에 그대로 드러난다.
       (키가 URL 로 평문 전송되는 것은 제공처 사정이라 이쪽에서 어쩔 수 없다. 읽기 전용·무료 키다.) */
    const string SubwayPositionEndpoint = "http://swopenapi.seoul.go.kr/api/subway/";
    const int SubwayRowLimit = 400;             // 가장 붐비는 노선의 열차 수보다 넉넉히(상한은 1000)
    const long SubwayMaxBytes = 512 * 1024;
    // 하루 1,000회 제한이 빡빡해 캐시가 절약이 아니라 필수다. 한 교실에서 화면 여럿이 같은 노선을
    // 봐도 상류 호출은 이 간격에 한 번으로 묶인다. 열차 보고 간격이 30초 안팎이라 화면은 안 끊긴다.
    static readonly TimeSpan SubwayCacheMaxAge = TimeSpan.FromSeconds(12);
    static readonly TimeSpan SubwayStaleMaxAge = TimeSpan.FromMinutes(1);

    /* 노선 이름이 그대로 URL 경로에 들어가므로 목록에 있는 것만 통과시킨다.
       src/js/subway-stations.js 의 SUBWAY_LINES 키, main.go 의 subwayLines 와 같은 목록이어야 한다
       (tests/subway-stations.test.js 가 세 곳을 함께 본다). 김포골드라인은 이 API 가 다루지 않는다. */
    static readonly string[] SubwayLines = {
        "1호선", "2호선", "3호선", "4호선", "5호선", "6호선", "7호선", "8호선", "9호선",
        "수인분당선", "신분당선", "경의중앙선", "공항철도", "우이신설선", "경춘선", "서해선"
    };

    class SubwayCacheEntry
    {
        public byte[] Data;
        public DateTime AtUtc;
        public SubwayCacheEntry(byte[] data, DateTime atUtc) { Data = data; AtUtc = atUtc; }
    }
    static readonly object SubwayCacheLock = new object();
    // 노선마다 한 칸뿐이라(16개) 따로 비울 일이 없다.
    static readonly Dictionary<string, SubwayCacheEntry> SubwayCache = new Dictionary<string, SubwayCacheEntry>();

    static readonly object SubwayKeyLock = new object();
    static readonly byte[] SubwayKeyEntropy = Encoding.UTF8.GetBytes("ClassDock.SubwayKey.v1");
    static readonly string SubwayKeyFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassDock", "subway-key.bin");
    static bool SubwayKeyLoaded;
    static string SubwayKey = "";

    static bool ValidSubwayLine(string name)
    {
        return Array.IndexOf(SubwayLines, (name ?? "").Trim()) >= 0;
    }
    static bool ValidSubwayKey(string value)
    {
        string key = (value ?? "").Trim();
        if (key.Length < 12 || key.Length > 128) return false;
        foreach (char ch in key)
            if (!(ch >= '0' && ch <= '9' || ch >= 'a' && ch <= 'z' || ch >= 'A' && ch <= 'Z' || ch == '-' || ch == '_'))
                return false;
        return true;
    }
    static string CurrentSubwayKey()
    {
        lock (SubwayKeyLock)
        {
            if (!SubwayKeyLoaded)
            {
                SubwayKeyLoaded = true;
                try
                {
                    if (File.Exists(SubwayKeyFile))
                    {
                        byte[] encrypted = File.ReadAllBytes(SubwayKeyFile);
                        byte[] plain = ProtectedData.Unprotect(encrypted, SubwayKeyEntropy, DataProtectionScope.CurrentUser);
                        string key = Encoding.UTF8.GetString(plain).Trim();
                        if (ValidSubwayKey(key)) SubwayKey = key;
                    }
                }
                catch { SubwayKey = ""; }
            }
            return SubwayKey;
        }
    }
    static bool SubwayKeyRemembered()
    {
        try { return File.Exists(SubwayKeyFile) && CurrentSubwayKey().Length > 0; }
        catch { return false; }
    }
    static string SubwayKeyStatusJson()
    {
        return "{\"hasKey\":" + (CurrentSubwayKey().Length > 0 ? "true" : "false")
            + ",\"remembered\":" + (SubwayKeyRemembered() ? "true" : "false")
            + ",\"persistentSupported\":true}";
    }
    static bool SaveProtectedSubwayKey(string key)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SubwayKeyFile));
            byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), SubwayKeyEntropy, DataProtectionScope.CurrentUser);
            string temp = SubwayKeyFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            if (File.Exists(SubwayKeyFile)) File.Delete(SubwayKeyFile);
            File.Move(temp, SubwayKeyFile);
            return true;
        }
        catch { return false; }
    }
    static bool ClearSubwayKey()
    {
        lock (SubwayKeyLock)
        {
            SubwayKeyLoaded = true;
            SubwayKey = "";
            try { if (File.Exists(SubwayKeyFile)) File.Delete(SubwayKeyFile); }
            catch { return false; }
        }
        lock (SubwayCacheLock) SubwayCache.Clear();   // 키를 지우면 그 키로 받아 둔 것도 남기지 않는다
        return true;
    }
    static bool TrySetSubwayKey(string value, bool remember, out string error)
    {
        string key = (value ?? "").Trim();
        error = "subway-key-invalid";
        if (!ValidSubwayKey(key)) return false;
        /* 시험 조회는 늘 열차가 있는 2호선으로 건다. 심야에는 INFO-200(자료 없음)이 올 수 있는데
           그것도 '키는 멀쩡하다' 는 뜻이라 TryFetchSubwayPosition 이 정상으로 돌려준다. */
        byte[] probe; string fetchError;
        if (!TryFetchSubwayPosition("2호선", key, out probe, out fetchError)) { error = fetchError; return false; }
        string previous = CurrentSubwayKey();
        lock (SubwayKeyLock)
        {
            if (remember)
            {
                if (!SaveProtectedSubwayKey(key)) { SubwayKey = previous; error = "subway-key-save-failed"; return false; }
            }
            else
            {
                try { if (File.Exists(SubwayKeyFile)) File.Delete(SubwayKeyFile); }
                catch { SubwayKey = previous; error = "subway-key-save-failed"; return false; }
            }
            SubwayKeyLoaded = true;
            SubwayKey = key;
        }
        error = "";
        return true;
    }

    /* 이 API 는 오류도 HTTP 200 으로 준다 — 본문의 code 를 봐야 한다(수출입은행 환율과 같은 함정).
       INFO-000 정상 · INFO-100 인증키 오류 · INFO-200 자료 없음(심야·이 API 가 안 다루는 노선).
       정상일 때는 code 가 errorMessage 안에 있고 오류일 때는 맨 바깥에 있는데, 어느 쪽이든
       본문에 그 문자열이 한 번만 나오므로 찾아보기만 해도 가른다(작은 JSON 이라 파서를 두지 않는다). */
    static string SubwayResultCode(byte[] data)
    {
        string text = Encoding.UTF8.GetString(data ?? new byte[0]);
        if (text.Contains("\"INFO-100\"")) return "INFO-100";
        if (text.Contains("\"INFO-200\"")) return "INFO-200";
        if (text.Contains("\"INFO-000\"")) return "INFO-000";
        return "";
    }

    static bool TryFetchSubwayPosition(string line, string key, out byte[] data, out string error)
    {
        data = null; error = "subway-failed";
        try
        {
            string url = SubwayPositionEndpoint + Uri.EscapeDataString(key) + "/json/realtimePosition/0/"
                + SubwayRowLimit.ToString(CultureInfo.InvariantCulture) + "/" + Uri.EscapeDataString(line);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "ClassDock/1.0 (local classroom app; https://github.com/songhwaseong/ClassDock)";
            request.Accept = "application/json";
            request.Timeout = 12000;
            request.ReadWriteTimeout = 12000;
            using (WebResponse response = request.GetResponse())
            using (Stream body = response.GetResponseStream())
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[8192];
                int read; long total = 0;
                while ((read = body.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > SubwayMaxBytes) { error = "subway-too-large"; return false; }
                    buffer.Write(chunk, 0, read);
                }
                data = buffer.ToArray();
            }
            string code = SubwayResultCode(data);
            // INFO-200 은 '지금 이 노선에 열차가 없다' 는 정상 답이라 그대로 내보낸다.
            if (code == "INFO-000" || code == "INFO-200") { error = ""; return true; }
            data = null;
            error = code == "INFO-100" ? "subway-key-invalid" : "subway-failed";
            return false;
        }
        catch { data = null; return false; }
    }

    /* 캐시 → 낡았으면 새로 받기 → 받기에 실패하면 1분 안쪽 캐시라도 내주기.
       오래된 값을 오래 붙들지는 않는다 — 낡은 열차 위치는 없는 것보다 나쁘다. */
    static bool TrySubwayPosition(string line, out byte[] data, out bool cached, out string error)
    {
        data = null; cached = false; error = "subway-failed";
        string key = CurrentSubwayKey();
        if (key.Length == 0) { error = "subway-key-required"; return false; }
        SubwayCacheEntry stored = null;
        lock (SubwayCacheLock) SubwayCache.TryGetValue(line, out stored);
        if (stored != null && DateTime.UtcNow - stored.AtUtc < SubwayCacheMaxAge)
        {
            data = stored.Data; error = "";
            return true;
        }
        byte[] fetched; string fetchError;
        if (TryFetchSubwayPosition(line, key, out fetched, out fetchError))
        {
            lock (SubwayCacheLock) SubwayCache[line] = new SubwayCacheEntry(fetched, DateTime.UtcNow);
            data = fetched; error = "";
            return true;
        }
        if (stored != null && DateTime.UtcNow - stored.AtUtc < SubwayStaleMaxAge)
        {
            data = stored.Data; cached = true; error = "";
            return true;
        }
        error = fetchError;
        return false;
    }

    static bool TryProxyMapTile(string url, out byte[] data, out string mime)
    {
        data = null; mime = "image/png";
        byte[] staleData = null;
        string staleMime = "image/png";
        try
        {
            Uri uri;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out uri)) return false;
            if (uri.Scheme != "https") return false;
            string host = uri.Host.ToLowerInvariant();
            bool allowed = false;
            foreach (string candidate in TileProxyHosts)
                if (host == candidate || host.EndsWith("." + candidate, StringComparison.Ordinal)) { allowed = true; break; }
            if (!allowed) return false;
            lock (TileCacheLock)
            {
                TileMemoryEntry cached;
                if (TileCache.TryGetValue(url, out cached))
                {
                    if (IsTileCacheFresh(cached.CachedAtUtc))
                    {
                        data = cached.Data; mime = cached.Mime;
                        return data != null && data.Length > 0;
                    }
                    staleData = cached.Data; staleMime = cached.Mime;
                }
            }
            // 7일 안에 받은 타일은 그대로 쓴다. 만료된 타일은 새로 받되, 오프라인이면 catch 에서
            // stale 복사본을 반환해 인터넷 없는 교실에서도 전에 본 지역은 계속 열리게 한다.
            DateTime cachedAtUtc;
            if (staleData == null && TryReadCachedTile(url, out data, out mime, out cachedAtUtc))
            {
                if (IsTileCacheFresh(cachedAtUtc))
                {
                    lock (TileCacheLock) TileCache[url] = new TileMemoryEntry(data, mime, cachedAtUtc);
                    return true;
                }
                staleData = data; staleMime = mime;
            }
            if (!TileTlsReady)
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                TileTlsReady = true;
            }
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.UserAgent = "ClassDock/1.0 (local classroom app; PDF export)";
            request.Accept = "image/*";
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            using (WebResponse response = request.GetResponse())
            using (Stream body = response.GetResponseStream())
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[16384];
                int read; long total = 0;
                while ((read = body.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > 2 * 1024 * 1024)
                    {
                        if (staleData != null) { data = staleData; mime = staleMime; return true; }
                        return false;   // 타일치고 비정상적으로 크면 중단
                    }
                    buffer.Write(chunk, 0, read);
                }
                if (!string.IsNullOrEmpty(response.ContentType)) mime = response.ContentType;
                data = buffer.ToArray();
            }
            lock (TileCacheLock)
            {
                if (TileCache.Count > 500) TileCache.Clear();   // 단순 상한 — 지도 몇 장 분량이면 충분
                TileCache[url] = new TileMemoryEntry(data, mime, DateTime.UtcNow);
            }
            WriteCachedTile(url, data, mime);
            return true;
        }
        catch
        {
            // 갱신에 실패해도 디스크에 남은 만료 타일은 오프라인 fallback으로 계속 쓴다.
            if (staleData != null) { data = staleData; mime = staleMime; return true; }
            DateTime cachedAtUtc;
            if (TryReadCachedTile(url, out data, out mime, out cachedAtUtc)) return true;
            data = null; return false;
        }
    }

    // 번들된 Pyodide 코어 파일(vendor/pyodide/<파일명>)을 안전하게 읽어 적절한 MIME 과 돌려준다.
    // path 는 "/pyodide/<파일명>[?v=..]" 형태 — 파일명만 허용하고 하위경로·상위탈출(..)은 막는다.
    static bool TryReadPyodideFile(string path, out byte[] data, out string contentType)
    {
        data = null; contentType = "application/octet-stream";
        int q = path.IndexOf('?');
        string rel = (q >= 0 ? path.Substring(0, q) : path).Substring("/pyodide/".Length);
        rel = Uri.UnescapeDataString(rel);
        if (rel.Length == 0 || rel.IndexOf('/') >= 0 || rel.IndexOf('\\') >= 0 || rel.Contains("..")) return false;
        string full = Path.Combine(PyodideDir, rel);
        if (!File.Exists(full)) return false;
        data = File.ReadAllBytes(full);
        string ext = Path.GetExtension(rel).ToLowerInvariant();
        if (ext == ".wasm") contentType = "application/wasm";
        else if (ext == ".js") contentType = "text/javascript; charset=utf-8";
        else if (ext == ".json") contentType = "application/json; charset=utf-8";
        return true;
    }

    // ===== 최근 작업공간 — 사용자가 고른 원본 파일과 상대경로를 앱 전용 저장소에 보관 =====
    // 저장 본문 안의 레코드 위치 — 파일 데이터는 복사하지 않고 경로와 구간만 기억해 둔다.
    class WorkspaceBodyRecord
    {
        public string Path;
        public int Offset;      // body 안에서 이 파일의 데이터가 시작하는 위치
        public int Length;
    }

    // 본문을 끝까지 검증하면서 레코드 목록만 만든다(데이터 복사 없음).
    static List<WorkspaceBodyRecord> IndexWorkspaceBody(byte[] body)
    {
        List<WorkspaceBodyRecord> records = new List<WorkspaceBodyRecord>();
        if (body == null || body.Length == 0) return records;
        if (body.Length > WorkspaceMaxBytes) throw new Exception("workspace-too-large");
        int pos = 0;
        int count = ReadBundleInt(body, ref pos);
        if (count < 0 || count > 10000) throw new Exception("bad-workspace");
        for (int i = 0; i < count; i++)
        {
            string rel = ReadBundleString(body, ref pos);
            int len = ReadBundleInt(body, ref pos);
            if (len < 0 || pos + len > body.Length) throw new Exception("bad-workspace");
            string safe = SafeRelPath(rel);
            if (safe == null) throw new Exception("bad-workspace-path");
            WorkspaceBodyRecord record = new WorkspaceBodyRecord();
            record.Path = safe.Replace('\\', '/');
            record.Offset = pos;
            record.Length = len;
            records.Add(record);
            pos += len;
        }
        if (pos != body.Length) throw new Exception("bad-workspace");
        return records;
    }

    // 같은 경로가 두 번 오면 마지막 것이 이긴다(예전 Dictionary 병합과 같은 규칙).
    static List<WorkspaceBodyRecord> DedupeWorkspaceRecords(List<WorkspaceBodyRecord> records)
    {
        Dictionary<string, WorkspaceBodyRecord> byPath = new Dictionary<string, WorkspaceBodyRecord>(StringComparer.OrdinalIgnoreCase);
        List<string> order = new List<string>();
        foreach (WorkspaceBodyRecord record in records)
        {
            if (!byPath.ContainsKey(record.Path)) order.Add(record.Path);
            byPath[record.Path] = record;
        }
        List<WorkspaceBodyRecord> unique = new List<WorkspaceBodyRecord>(order.Count);
        foreach (string path in order) unique.Add(byPath[path]);
        return unique;
    }

    static void WriteWorkspaceInt(Stream stream, int value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 24) & 0xFF));
    }

    static int ReadWorkspaceInt(Stream stream)
    {
        int b0 = stream.ReadByte(), b1 = stream.ReadByte(), b2 = stream.ReadByte(), b3 = stream.ReadByte();
        if ((b0 | b1 | b2 | b3) < 0) throw new Exception("bad-workspace");
        return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
    }

    static byte[] ReadWorkspaceExact(Stream stream, int length)
    {
        byte[] buffer = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = stream.Read(buffer, read, length - read);
            if (n <= 0) throw new Exception("bad-workspace");
            read += n;
        }
        return buffer;
    }

    static void CopyWorkspaceBytes(Stream from, Stream to, int length, byte[] buffer)
    {
        int left = length;
        while (left > 0)
        {
            int want = left < buffer.Length ? left : buffer.Length;
            int n = from.Read(buffer, 0, want);
            if (n <= 0) throw new Exception("bad-workspace");
            to.Write(buffer, 0, n);
            left -= n;
        }
    }

    // 기존 workspace.bin 을 레코드 단위로 흘려 읽어 임시 파일에 다시 쓴다.
    // drop 에 있는 경로는 건너뛰고, appendRecords 가 있으면(병합 저장) 그 뒤에 이어 붙인다.
    // 예전 방식(전체 읽기 → 항목별 파싱 → 직렬화 → ToArray)은 최종 크기의 4~5배를 한꺼번에
    // 잡아서 상한을 올릴 수 없었다. 여기서는 1MB 버퍼만 쓰므로 파일 크기와 무관하다.
    static int RewriteWorkspace(bool keepExisting, HashSet<string> drop, byte[] appendBody, List<WorkspaceBodyRecord> appendRecords)
    {
        string dir = Path.GetDirectoryName(WorkspacePath);
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, ".classdock-save-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        int count = 0;
        long total = 4;
        try
        {
            using (FileStream outStream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                created = true;
                WriteWorkspaceInt(outStream, 0);          // 개수는 다 쓴 뒤 되돌아와 채운다
                byte[] buffer = new byte[1 << 20];
                if (keepExisting && File.Exists(WorkspacePath))
                {
                    using (FileStream inStream = new FileStream(WorkspacePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                    {
                        int oldCount = ReadWorkspaceInt(inStream);
                        if (oldCount < 0 || oldCount > 10000) throw new Exception("bad-workspace");
                        for (int i = 0; i < oldCount; i++)
                        {
                            int pathLength = ReadWorkspaceInt(inStream);
                            if (pathLength < 0 || pathLength > 65535) throw new Exception("bad-workspace");
                            byte[] pathBytes = ReadWorkspaceExact(inStream, pathLength);
                            int dataLength = ReadWorkspaceInt(inStream);
                            if (dataLength < 0) throw new Exception("bad-workspace");
                            string path = Encoding.UTF8.GetString(pathBytes).Replace('\\', '/');
                            if (drop != null && drop.Contains(path))
                            {
                                inStream.Seek(dataLength, SeekOrigin.Current);
                                continue;
                            }
                            count++;
                            total += 8 + pathLength + dataLength;
                            if (count > 10000) throw new Exception("bad-workspace");
                            if (total > WorkspaceMaxBytes) throw new Exception("workspace-too-large");
                            WriteWorkspaceInt(outStream, pathLength);
                            outStream.Write(pathBytes, 0, pathLength);
                            WriteWorkspaceInt(outStream, dataLength);
                            CopyWorkspaceBytes(inStream, outStream, dataLength, buffer);
                        }
                    }
                }
                if (appendRecords != null)
                {
                    foreach (WorkspaceBodyRecord record in appendRecords)
                    {
                        byte[] pathBytes = Encoding.UTF8.GetBytes(record.Path);
                        count++;
                        total += 8 + pathBytes.Length + record.Length;
                        if (count > 10000) throw new Exception("bad-workspace");
                        if (total > WorkspaceMaxBytes) throw new Exception("workspace-too-large");
                        WriteWorkspaceInt(outStream, pathBytes.Length);
                        outStream.Write(pathBytes, 0, pathBytes.Length);
                        WriteWorkspaceInt(outStream, record.Length);
                        outStream.Write(appendBody, record.Offset, record.Length);
                    }
                }
                outStream.Seek(0, SeekOrigin.Begin);
                WriteWorkspaceInt(outStream, count);
                outStream.Flush(true); // 최종 항목 개수까지 기록한 다음 교체한다.
            }
            if (count == 0)      // 남은 항목이 없으면 빈 묶음을 남기지 않는다(닫은 파일 정리와 같은 규칙)
            {
                File.Delete(WorkspacePath);
                return 0;
            }
            // 교체 실패 시 원본 삭제나 직접 덮어쓰기로 우회하지 않는다.
            if (File.Exists(WorkspacePath)) File.Replace(temp, WorkspacePath, null);
            else File.Move(temp, WorkspacePath);
            return count;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temp); }
                catch (Exception ex) { Debug.WriteLine("workspace-temp-cleanup-failed: " + ex.Message); }
            }
        }
    }

    // 교체 저장은 브라우저가 보낸 작업공간 바이너리 자체가 최종 저장 형식과 같다.
    // 경로·길이·중복을 검증한 뒤 그대로 기록하면 파일 데이터를 항목별로 복사하고
    // 다시 하나의 큰 배열로 직렬화하는 과정을 생략할 수 있다.
    static bool CanSaveWorkspaceDirectly(byte[] body, out int count)
    {
        count = 0;
        if (body == null || body.Length < 4) return false;
        if (body.Length > WorkspaceMaxBytes) throw new Exception("workspace-too-large");
        int pos = 0;
        count = ReadBundleInt(body, ref pos);
        if (count < 0 || count > 10000) throw new Exception("bad-workspace");
        bool direct = true;
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            string rel = ReadBundleString(body, ref pos);
            int len = ReadBundleInt(body, ref pos);
            if (len < 0 || pos + len > body.Length) throw new Exception("bad-workspace");
            string safe = SafeRelPath(rel);
            if (safe == null) throw new Exception("bad-workspace-path");
            string normalized = safe.Replace('\\', '/');
            if (!string.Equals(rel.Replace('\\', '/'), normalized, StringComparison.Ordinal) || !seen.Add(normalized))
                direct = false;
            pos += len;
        }
        if (pos != body.Length) throw new Exception("bad-workspace");
        return direct;
    }

    static void WriteWorkspaceAtomically(byte[] saved)
    {
        string dir = Path.GetDirectoryName(WorkspacePath);
        Directory.CreateDirectory(dir);
        WriteFileAtomically(WorkspacePath, saved);
    }

    static int SaveWorkspace(byte[] body, bool replace)
    {
        int directCount = 0;
        bool direct = replace && CanSaveWorkspaceDirectly(body, out directCount);
        List<WorkspaceBodyRecord> incoming = direct ? null : DedupeWorkspaceRecords(IndexWorkspaceBody(body));
        lock (WorkspaceLock)
        {
            if (direct)
            {
                WriteWorkspaceAtomically(body);
                return directCount;
            }
            // 병합 저장은 같은 경로의 예전 내용만 걷어내고 나머지는 그대로 흘려 보낸다.
            // 교체 저장인데 직접 쓰기 조건(중복·비정규 경로)에 안 맞으면 기존 내용은 버린다.
            HashSet<string> drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (WorkspaceBodyRecord record in incoming) drop.Add(record.Path);
            return RewriteWorkspace(!replace, drop, body, incoming);
        }
    }

    static byte[] LoadWorkspace()
    {
        lock (WorkspaceLock)
        {
            try { return File.Exists(WorkspacePath) ? File.ReadAllBytes(WorkspacePath) : new byte[0]; }
            catch { return new byte[0]; }
        }
    }

    static void ClearWorkspace()
    {
        lock (WorkspaceLock)
        {
            try { if (File.Exists(WorkspacePath)) File.Delete(WorkspacePath); } catch { }
        }
    }

    static int RemoveWorkspaceFiles(byte[] body)
    {
        int pos = 0;
        int count = ReadBundleInt(body, ref pos);
        if (count < 0 || count > 10000) throw new Exception("bad-workspace-remove");
        HashSet<string> remove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            string safe = SafeRelPath(ReadBundleString(body, ref pos));
            if (safe != null) remove.Add(safe.Replace('\\', '/'));
        }
        if (pos != body.Length) throw new Exception("bad-workspace-remove");

        lock (WorkspaceLock)
        {
            if (!File.Exists(WorkspacePath)) return 0;
            return RewriteWorkspace(true, remove, null, null);
        }
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
        Exception hiddenError = null;
        try
        {
            // 먼저 창을 만들지 않고 변환해 PowerPoint가 화면이나 작업 표시줄에 나타나는 일을 줄인다.
            return RunPowerPointExportAttempt(inPath, outPath, false);
        }
        catch (Exception ex)
        {
            hiddenError = ex;
        }

        try
        {
            // 숨김 Open이 불안정한 일부 Office 빌드에서만 최소화된 창으로 다시 시도한다.
            return RunPowerPointExportAttempt(inPath, outPath, true);
        }
        catch (Exception ex)
        {
            throw new Exception("hidden-open failed: " + FlattenMessage(hiddenError) + "; windowed-open failed: " + FlattenMessage(ex), ex);
        }
    }

    static byte[] RunPowerPointExportAttempt(string inPath, string outPath, bool withWindow)
    {
        Type pptType = Type.GetTypeFromProgID("PowerPoint.Application");
        if (pptType == null) throw new PowerPointMissingException();

        object app = null, presentations = null, pres = null;
        try
        {
            app = Activator.CreateInstance(pptType);
            TrySet(app, "DisplayAlerts", 1);        // ppAlertsNone — 대화상자 억제
            TrySet(app, "AutomationSecurity", 3);   // msoAutomationSecurityForceDisable — 매크로 차단
            if (withWindow)
            {
                TrySet(app, "Visible", -1);          // msoTrue. Some Office builds crash on hidden Open.
                TrySet(app, "WindowState", 2);       // ppWindowMinimized
            }
            presentations = Get(app, "Presentations");
            // Open(FileName, ReadOnly=msoTrue(-1), Untitled=msoFalse(0), WithWindow=msoFalse(0))
            pres = InvokeRetry(presentations, "Open", new object[] { inPath, -1, 0, withWindow ? -1 : 0 });
            // SaveAs(FileName, ppSaveAsPDF=32, EmbedTrueTypeFonts=msoFalse(0))
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            InvokeRetry(pres, "SaveAs", new object[] { outPath, 32, 0 });
            InvokeRetry(pres, "Close", null);
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

    // ===== 대화형 파이썬 세션 — input() 프롬프트마다 브라우저에서 한 줄씩 전달 =====
    static string QueryValue(string path, string key)
    {
        int q = path.IndexOf('?');
        if (q < 0) return "";
        string[] pairs = path.Substring(q + 1).Split('&');
        foreach (string pair in pairs)
        {
            int eq = pair.IndexOf('=');
            string name = eq >= 0 ? pair.Substring(0, eq) : pair;
            if (name == key) return Uri.UnescapeDataString(eq >= 0 ? pair.Substring(eq + 1) : "");
        }
        return "";
    }

    // 드라이브 루트(예: D:\)를 루트로 써도 검증이 깨지지 않게 정규화.
    // "D:\" 를 TrimEnd 하면 "D:" 가 되는데, Path.GetFullPath("D:") 는 그 드라이브의
    // '현재 폴더'(예: D:\my)로 풀리므로 이후 재검증이 전부 실패한다 → 드라이브 루트는 구분자를 유지.
    static string NormalizeRootForCheck(string root)
    {
        string normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 2 && normalized[1] == ':') normalized += Path.DirectorySeparatorChar;
        return normalized;
    }

    static bool IsPathInsideRoot(string root, string candidate, bool allowRoot=true)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(candidate)) return false;
        string normalizedRoot = NormalizeRootForCheck(root);
        string normalizedCandidate = Path.GetFullPath(candidate);
        if (allowRoot && string.Equals(
            normalizedCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // 저장 루트 아래에서 이미 존재하는 재분석 지점(심볼릭 링크·junction)을 거치지 않게 한다.
    // 상대경로 검증만으로는 저장 루트 안의 링크가 외부 파일을 가리키는 경우를 막을 수 없기 때문이다.
    static bool HasReparsePointBelowRoot(string root, string full)
    {
        try
        {
            string normalizedRoot = NormalizeRootForCheck(root);
            string normalizedFull = Path.GetFullPath(full);
            if (!IsPathInsideRoot(normalizedRoot, normalizedFull, true)) return true;
            string relative = normalizedFull.Substring(normalizedRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = normalizedRoot;
            foreach (string part in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (!File.Exists(current) && !Directory.Exists(current)) continue;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }
        catch { return true; }
    }

    static bool TryResolveSaveRootPath(string relativePath, out string full)
    {
        full = "";
        string safe = SafeRelPath(relativePath);
        if (safe == null) return false;
        try
        {
            string root = Path.GetFullPath(CurrentSaveRoot());
            string candidate = Path.GetFullPath(Path.Combine(root, safe));
            if (!IsPathInsideRoot(root, candidate, false) || HasReparsePointBelowRoot(root, candidate)) return false;
            full = candidate;
            return true;
        }
        catch { return false; }
    }

    static bool TryResolveSourceFolderPath(string id, string relativePath, bool allowRoot, out string root, out string full)
    {
        root = "";
        full = "";
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (SourceFolderLock)
        {
            if (!SourceFolders.TryGetValue(id, out root)) return false;
        }
        try
        {
            root = Path.GetFullPath(root);
            string rel = (relativePath ?? "").Trim();
            if (rel.Length == 0)
            {
                if (!allowRoot) return false;
                full = root;
                return Directory.Exists(full);
            }
            string safe = SafeRelPath(rel);
            if (safe == null) return false;
            string candidate = Path.GetFullPath(Path.Combine(root, safe));
            if (!IsPathInsideRoot(root, candidate, false) || HasReparsePointBelowRoot(root, candidate)) return false;
            full = candidate;
            return true;
        }
        catch { return false; }
    }

    static long UnixMilliseconds(DateTime value)
    {
        DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    }

    static string SourceFolderEntryJson(string id, string relativePath)
    {
        string root, full;
        if (!TryResolveSourceFolderPath(id, relativePath, true, out root, out full))
            throw new UnauthorizedAccessException("bad-source-folder-path");
        if (Directory.Exists(full))
        {
            DirectoryInfo info = new DirectoryInfo(full);
            return "{\"kind\":\"directory\",\"name\":" + JsonString(info.Name)
                + ",\"size\":0,\"lastModified\":" + UnixMilliseconds(info.LastWriteTimeUtc) + "}";
        }
        if (File.Exists(full))
        {
            FileInfo info = new FileInfo(full);
            return "{\"kind\":\"file\",\"name\":" + JsonString(info.Name)
                + ",\"size\":" + info.Length
                + ",\"lastModified\":" + UnixMilliseconds(info.LastWriteTimeUtc) + "}";
        }
        throw new FileNotFoundException("source-entry-not-found");
    }

    static string SourceFolderListJson(string id, string relativePath)
    {
        string root, full;
        if (!TryResolveSourceFolderPath(id, relativePath, true, out root, out full) || !Directory.Exists(full))
            throw new DirectoryNotFoundException("source-directory-not-found");
        List<FileSystemInfo> entries = new List<FileSystemInfo>();
        foreach (FileSystemInfo entry in new DirectoryInfo(full).GetFileSystemInfos())
        {
            // 숨김 폴더(.git 등)는 순회하지 않되 .env 같은 점 파일은 기존 폴더 열기와 동일하게 전달한다.
            if (entry is DirectoryInfo && entry.Name.StartsWith(".", StringComparison.Ordinal)) continue;
            try { if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue; }
            catch { continue; }
            entries.Add(entry);
        }
        entries.Sort(delegate(FileSystemInfo a, FileSystemInfo b)
        {
            bool ad = a is DirectoryInfo, bd = b is DirectoryInfo;
            if (ad != bd) return ad ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        StringBuilder json = new StringBuilder("{\"items\":[");
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0) json.Append(',');
            FileSystemInfo entry = entries[i];
            FileInfo file = entry as FileInfo;
            json.Append("{\"kind\":").Append(JsonString(file == null ? "directory" : "file"))
                .Append(",\"name\":").Append(JsonString(entry.Name))
                .Append(",\"size\":").Append(file == null ? 0 : file.Length)
                .Append(",\"lastModified\":").Append(UnixMilliseconds(entry.LastWriteTimeUtc))
                .Append('}');
        }
        return json.Append("]}").ToString();
    }

    // 같은 디렉터리에서 기록을 마친 파일만 교체한다. 교체를 지원하지 않거나 실패하면
    // 원본 삭제/직접 덮어쓰기로 우회하지 않아야 저장 실패 때 기존 내용을 보존할 수 있다.
    static void WriteFileAtomically(string full, byte[] body)
    {
        if (body == null) throw new ArgumentNullException("body");
        string temp = Path.Combine(Path.GetDirectoryName(full), ".classdock-save-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            using (FileStream output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                output.Write(body, 0, body.Length);
                output.Flush(true);
            }
            if (File.Exists(full)) File.Replace(temp, full, null);
            else File.Move(temp, full);
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temp); }
                catch (Exception ex) { Debug.WriteLine("save-temp-cleanup-failed: " + ex.Message); }
            }
        }
    }

    static void WriteSourceFolderFile(string id, string relativePath, byte[] body)
    {
        string root, full;
        if (!TryResolveSourceFolderPath(id, relativePath, false, out root, out full))
            throw new UnauthorizedAccessException("bad-source-folder-path");
        string parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("source-parent-not-found");
        if (Directory.Exists(full)) throw new IOException("source-entry-is-directory");
        WriteFileAtomically(full, body);
    }

    static void CreateSourceFolderDirectory(string id, string relativePath)
    {
        string root, full;
        if (!TryResolveSourceFolderPath(id, relativePath, false, out root, out full))
            throw new UnauthorizedAccessException("bad-source-folder-path");
        if (File.Exists(full)) throw new IOException("source-entry-is-file");
        Directory.CreateDirectory(full);
    }

    static void RemoveSourceFolderEntry(string id, string relativePath, bool recursive)
    {
        string root, full;
        if (!TryResolveSourceFolderPath(id, relativePath, false, out root, out full))
            throw new UnauthorizedAccessException("bad-source-folder-path");
        if (File.Exists(full)) { File.Delete(full); return; }
        if (Directory.Exists(full)) { Directory.Delete(full, recursive); return; }
        throw new FileNotFoundException("source-entry-not-found");
    }

    static bool TryReadLocalFile(string path, out byte[] data, out string fileName)
    {
        data = null;
        fileName = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try
        {
            if (Path.IsPathRooted(path))
            {
                full = Path.GetFullPath(path);
            }
            else
            {
                if (!TryResolveSaveRootPath(path, out full)) return false;
            }
        }
        catch { return false; }
        if (!File.Exists(full)) return false;
        string ext = Path.GetExtension(full).ToLowerInvariant();
        if (ext != ".py" && ext != ".pyw" && ext != ".pyi" && ext != ".txt"
            && ext != ".db" && ext != ".sqlite" && ext != ".sqlite3") return false;
        FileInfo info;
        try { info = new FileInfo(full); }
        catch { return false; }
        if (info.Length > 5 * 1024 * 1024) return false;
        data = File.ReadAllBytes(full);
        fileName = Path.GetFileName(full);
        return true;
    }

    static string StartPythonKernel(byte[] body)
    {
        string interp = FindPython();
        if (interp == null) throw new PythonMissingException();
        if (body == null || body.Length == 0 || body.Length > 60 * 1024 * 1024)
            throw new Exception("bad-kernel-bundle");

        SweepPythonKernels();
        string id = Guid.NewGuid().ToString("N");
        string tempRoot = Path.Combine(Path.GetTempPath(), "moidapy_kernel_" + id);
        string runnerPath = Path.Combine(Path.GetTempPath(), "moidapy_kernel_runner_" + id + ".py");
        Directory.CreateDirectory(tempRoot);
        try
        {
            int pos = 0;
            string target = ReadBundleString(body, ref pos);
            if (SafeRelPath(target) == null) throw new Exception("bad-target");
            int count = ReadBundleInt(body, ref pos);
            if (count < 0 || count > 100000) throw new Exception("bad-bundle");
            for (int i = 0; i < count; i++)
            {
                string rel = ReadBundleString(body, ref pos);
                int len = ReadBundleInt(body, ref pos);
                if (len < 0 || pos + len > body.Length) throw new Exception("bad-bundle");
                string safe = SafeRelPath(rel);
                if (safe != null)
                {
                    string full = Path.Combine(tempRoot, safe);
                    string dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllBytes(full, SubBytes(body, pos, len));
                }
                pos += len;
            }
            if (pos < body.Length) ReadBundleString(body, ref pos); // 초기 stdin(커널에서는 셀별 전달)
            string requestedCwd = "";
            if (pos < body.Length) requestedCwd = ReadBundleString(body, ref pos);
            if (pos < body.Length)
            {
                int dirCount = ReadBundleInt(body, ref pos);
                if (dirCount < 0 || dirCount > 100000) throw new Exception("bad-bundle");
                for (int i = 0; i < dirCount; i++)
                {
                    string safeDir = SafeRelPath(ReadBundleString(body, ref pos));
                    if (!string.IsNullOrEmpty(safeDir)) Directory.CreateDirectory(Path.Combine(tempRoot, safeDir));
                }
            }
            if (pos != body.Length) throw new Exception("bad-bundle");

            string workDir = ResolveBundleWorkDir(tempRoot, requestedCwd, tempRoot);
            File.WriteAllBytes(runnerPath, PythonKernelRunner);

            string args = (interp == "py" ? "-3 " : "") + "-u -X utf8 \"" + runnerPath + "\"";
            ProcessStartInfo psi = new ProcessStartInfo(interp, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.WorkingDirectory = workDir;
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["MPLBACKEND"] = "Agg";
            psi.EnvironmentVariables["CLASSDOCK_KERNEL_ROOT"] = tempRoot;

            PythonKernel kernel = new PythonKernel();
            kernel.Id = id;
            kernel.TempRoot = tempRoot;
            kernel.RunnerPath = runnerPath;
            kernel.Process = new Process();
            kernel.Process.StartInfo = psi;
            kernel.Process.Start();
            StartLimitedReader(kernel.Process.StandardError, kernel.Stderr);
            lock (PyKernelsLock) PyKernels[id] = kernel;
            return id;
        }
        catch
        {
            try { if (File.Exists(runnerPath)) File.Delete(runnerPath); } catch { }
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
            throw;
        }
    }

    static string ExecutePythonKernel(string id, byte[] body)
    {
        PythonKernel kernel;
        lock (PyKernelsLock) if (!PyKernels.TryGetValue(id ?? "", out kernel))
            throw new Exception("kernel-not-found");
        if (body == null || body.Length == 0 || body.Length > 20 * 1024 * 1024)
            throw new Exception("bad-kernel-request");

        int pos = 0;
        int sourceLen = ReadBundleInt(body, ref pos);
        if (sourceLen < 0 || pos + sourceLen > body.Length) throw new Exception("bad-kernel-request");
        string source = Encoding.UTF8.GetString(body, pos, sourceLen);
        pos += sourceLen;
        int stdinLen = ReadBundleInt(body, ref pos);
        if (stdinLen < 0 || pos + stdinLen != body.Length) throw new Exception("bad-kernel-request");
        string stdin = Encoding.UTF8.GetString(body, pos, stdinLen);

        lock (kernel.ExecLock)
        {
            if (kernel.Process == null || kernel.Process.HasExited)
                throw new Exception("kernel-stopped: " + kernel.Stderr.GetText());
            kernel.LastUsed = DateTime.UtcNow;
            string request = "{\"action\":\"exec\",\"source\":" + JsonString(source) + ",\"stdin\":" + JsonString(stdin) + "}";
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(request));
            kernel.Process.StandardInput.WriteLine(encoded);
            kernel.Process.StandardInput.Flush();

            string responseLine = null;
            Exception readError = null;
            Thread reader = new Thread(delegate()
            {
                try { responseLine = kernel.Process.StandardOutput.ReadLine(); }
                catch (Exception ex) { readError = ex; }
            });
            reader.IsBackground = true;
            reader.Start();
            bool timedOut = false;
            bool memoryLimit = false;
            Stopwatch watch = Stopwatch.StartNew();
            while (!reader.Join(250))
            {
                if (watch.ElapsedMilliseconds >= PythonKernelExecutionTimeoutMs) { timedOut = true; break; }
                if (ProcessTreeWorkingSetBytes(kernel.Process.Id) > PythonProcessMemoryLimitBytes) { memoryLimit = true; break; }
            }
            if (timedOut || memoryLimit)
            {
                KillProcessTree(kernel.Process);
                try { reader.Join(2000); } catch { }
                kernel.Stderr.AppendLine(memoryLimit
                    ? "[메모리 제한: 노트북 커널 실행이 4GB를 넘어 종료했습니다.]"
                    : "[시간 초과: 노트북 셀 실행이 10분을 넘어 종료했습니다.]");
                throw new Exception(memoryLimit ? "kernel-memory-limit" : "kernel-timeout");
            }
            if (readError != null) throw readError;
            if (string.IsNullOrEmpty(responseLine))
                throw new Exception("kernel-stopped: " + kernel.Stderr.GetText());
            byte[] decoded;
            try { decoded = Convert.FromBase64String(responseLine.Trim()); }
            catch { throw new Exception("bad-kernel-response: " + responseLine); }
            string json = Encoding.UTF8.GetString(decoded);
            if (!json.TrimStart().StartsWith("{", StringComparison.Ordinal))
                throw new Exception("bad-kernel-response");
            kernel.LastUsed = DateTime.UtcNow;
            return json;
        }
    }

    static void StopPythonKernel(string id)
    {
        PythonKernel kernel = null;
        lock (PyKernelsLock)
        {
            if (PyKernels.TryGetValue(id ?? "", out kernel)) PyKernels.Remove(id ?? "");
        }
        if (kernel == null) return;
        KillProcessTree(kernel.Process);
        try { if (File.Exists(kernel.RunnerPath)) File.Delete(kernel.RunnerPath); } catch { }
        try { if (Directory.Exists(kernel.TempRoot)) Directory.Delete(kernel.TempRoot, true); } catch { }
    }

    static bool TryGetKernelFile(string id, string name, out byte[] data, out string fileName)
    {
        data = null; fileName = null;
        PythonKernel kernel;
        lock (PyKernelsLock) if (!PyKernels.TryGetValue(id ?? "", out kernel)) return false;
        string safe = SafeRelPath(name);
        if (safe == null) return false;
        string full = Path.Combine(kernel.TempRoot, safe);
        if (!File.Exists(full)) return false;
        try
        {
            FileInfo info = new FileInfo(full);
            if (info.Length > 40 * 1024 * 1024) return false;
            data = File.ReadAllBytes(full);
        }
        catch { return false; }
        fileName = Path.GetFileName(full);
        return true;
    }

    static void SweepPythonKernels()
    {
        List<PythonKernel> stale = new List<PythonKernel>();
        lock (PyKernelsLock)
        {
            List<PythonKernel> all = new List<PythonKernel>(PyKernels.Values);
            all.Sort(delegate(PythonKernel a, PythonKernel b) { return a.LastUsed.CompareTo(b.LastUsed); });
            DateTime now = DateTime.UtcNow;
            foreach (PythonKernel kernel in all)
                if ((now - kernel.LastUsed).TotalHours > 2) stale.Add(kernel);
            for (int i = 0; i < all.Count - 8; i++)
                if (!stale.Contains(all[i])) stale.Add(all[i]);
        }
        foreach (PythonKernel kernel in stale) StopPythonKernel(kernel.Id);
    }

    /* ── 원격 MySQL 접속 ─────────────────────────────────────────────────────────────
       db_worker.py 를 접속 하나당 하나씩 띄우고 base64(JSON) 한 줄을 주고받는다.
       런처는 워커 응답의 JSON 을 열어 보지 않는다. 응답 첫 글자('+'/'-')로 성공 여부만 보고
       본문은 그대로 브라우저에 넘긴다. 비밀번호는 stdin 의 connect 요청에만 실리고
       명령행·환경변수·로그·디스크 어디에도 남지 않는다. */

    static readonly System.Text.RegularExpressions.Regex DbHostRe =
        new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9._:\[\]-]{1,255}$");

    // 접속 정보는 SQL 로 조립되지 않고 JSON 값으로만 워커에 건너간다. 여기서 막는 것은
    // 길이 폭주와 제어문자(로그·프로토콜 줄바꿈을 깨뜨리는 값)다.
    static string DbCheckField(string value, string name, int max, bool allowEmpty)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            if (allowEmpty) return "";
            throw new Exception("db-missing-" + name);
        }
        if (text.Length > max) throw new Exception("db-long-" + name);
        foreach (char c in text) if (c < 0x20 || c == 0x7f) throw new Exception("db-bad-" + name);
        return text;
    }

    static DbSession RequireDbSession(string id)
    {
        DbSession session;
        lock (DbSessionsLock) if (!DbSessions.TryGetValue(id ?? "", out session)) throw new Exception("db-session-not-found");
        return session;
    }

    static void DbWriteLine(DbSession session, string requestJson)
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(requestJson));
        // 취소는 쿼리가 ExecLock 을 쥐고 있는 동안에도 나가야 하므로 stdin 잠금을 따로 둔다.
        lock (session.StdinLock)
        {
            session.Process.StandardInput.WriteLine(encoded);
            session.Process.StandardInput.Flush();
        }
    }

    // 취소는 응답을 만들지 않는다(fire and forget). 결과는 취소당한 쿼리 자신의 응답으로 드러난다.
    static void DbSendCancel(DbSession session)
    {
        try { DbWriteLine(session, "{\"action\":\"cancel\"}"); } catch { }
    }

    /* 요청 하나를 보내고 응답 한 줄을 받는다. 워커가 '*' 로 시작하는 진행 보고를 흘릴 수 있어
       최종 응답('+' / '-')이 올 때까지 읽는다. onProgress 는 그 진행 JSON 을 그대로 받는다.
       제한 시간은 "줄 하나를 기다리는 시간"이다 — 진행 보고가 오는 동안에는 다시 잡힌다.
       덤프처럼 몇십 분 걸리는 작업도 살아 있는 한 끊기지 않고, 조용히 멈추면 제때 끊긴다. */
    static string DbExchange(DbSession session, string requestJson, int timeoutMs)
    {
        return DbExchange(session, requestJson, timeoutMs, null, null);
    }

    static string DbExchange(DbSession session, string requestJson, int timeoutMs, Action<string> onProgress)
    {
        return DbExchange(session, requestJson, timeoutMs, onProgress, null);
    }

    /* job 이 있으면 요청을 실제로 보내는 순간을 기록한다. 취소가 먼저 들어온 대기 작업은
       워커에 보내지 않고 여기서 끝낸다. Started 와 ActiveJobId 를 나눠 두는 이유는 같은
       세션에서 앞 작업이 끝나는 찰나의 취소가 다음 작업을 죽이지 않게 하기 위해서다. */
    static string DbExchange(DbSession session, string requestJson, int timeoutMs,
                             Action<string> onProgress, DbQueryJob job)
    {
        lock (session.ExecLock)
        {
            if (session.Process == null || session.Process.HasExited)
                throw new Exception("db-session-stopped: " + session.Stderr.GetText());
            session.LastUsed = DateTime.UtcNow;
            if (job != null)
            {
                lock (job.Sync)
                {
                    if (job.CancelRequested)
                        return "-{\"ok\":false,\"code\":\"cancelled\",\"detail\":\"작업을 시작하기 전에 취소했습니다.\"}";
                    // 요청 쓰기와 활성 작업 표시는 같은 stdin 잠금 안에서 바뀐다. 취소 쪽도
                    // 이 잠금을 잡고 id 를 다시 보므로 다음 작업으로 새는 틈이 없다.
                    lock (session.StdinLock)
                    {
                        job.Started = true;
                        session.ActiveJobId = job.Id;
                        DbWriteLine(session, requestJson);
                    }
                }
            }
            else DbWriteLine(session, requestJson);

            try
            {
                while (true)
                {
                    string responseLine = null;
                    Exception readError = null;
                    Thread reader = new Thread(delegate()
                    {
                        try { responseLine = session.Process.StandardOutput.ReadLine(); }
                        catch (Exception ex) { readError = ex; }
                    });
                    reader.IsBackground = true;
                    reader.Start();
                    if (!reader.Join(timeoutMs))
                    {
                        // 제한 시간을 넘겼다 = 워커가 서버 응답을 기다리는 중이다. 먼저 취소를 보내 서버 쪽
                        // 쿼리를 끊고, 그래도 돌아오지 않으면 커넥션을 물고 있는 프로세스를 접는다.
                        DbSendCancel(session);
                        if (!reader.Join(5000))
                        {
                            KillProcessTree(session.Process);
                            try { reader.Join(2000); } catch { }
                            throw new Exception("db-timeout");
                        }
                    }
                    if (readError != null) throw readError;
                    if (string.IsNullOrEmpty(responseLine))
                        throw new Exception("db-session-stopped: " + session.Stderr.GetText());

                    string line = responseLine.Trim();
                    if (line.Length < 2 || (line[0] != '+' && line[0] != '-' && line[0] != '*'))
                        throw new Exception("bad-db-response");
                    byte[] decoded;
                    try { decoded = Convert.FromBase64String(line.Substring(1)); }
                    catch { throw new Exception("bad-db-response"); }
                    session.LastUsed = DateTime.UtcNow;
                    string body = Encoding.UTF8.GetString(decoded);
                    if (line[0] == '*')
                    {
                        if (onProgress != null) onProgress(body);
                        continue;                       // 아직 하는 중이다. 다음 줄을 기다린다.
                    }
                    return line[0] + body;
                }
            }
            finally
            {
                if (job != null)
                {
                    lock (session.StdinLock)
                        if (session.ActiveJobId == job.Id) session.ActiveJobId = "";
                }
            }
        }
    }

    static bool DbResponseOk(string response) { return response.Length > 0 && response[0] == '+'; }
    static string DbResponseBody(string response) { return response.Length > 0 ? response.Substring(1) : "{}"; }

    static string DbCapability()
    {
        string interp = FindPython();
        if (interp == null) return "{\"python\":false,\"driver\":false,\"version\":\"\"}";
        string version = "";
        try
        {
            string args = (interp == "py" ? "-3 " : "") + "-c \"import pymysql,sys;sys.stdout.write(pymysql.__version__)\"";
            ProcessStartInfo psi = new ProcessStartInfo(interp, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            using (Process probe = Process.Start(psi))
            {
                string output = probe.StandardOutput.ReadToEnd();
                probe.StandardError.ReadToEnd();
                if (!probe.WaitForExit(15000)) KillProcessTree(probe);
                else if (probe.ExitCode == 0) version = output.Trim();
            }
        }
        catch { }
        return "{\"python\":true,\"driver\":" + (version.Length > 0 ? "true" : "false")
            + ",\"version\":" + JsonString(version) + "}";
    }

    static string StartDbSession(byte[] body)
    {
        string interp = FindPython();
        if (interp == null) throw new PythonMissingException();
        if (body == null || body.Length == 0 || body.Length > 64 * 1024) throw new Exception("bad-db-request");

        int pos = 0;
        string host = DbCheckField(ReadBundleString(body, ref pos), "host", 255, false);
        string portText = ReadBundleString(body, ref pos);
        string database = DbCheckField(ReadBundleString(body, ref pos), "database", 64, true);
        string user = DbCheckField(ReadBundleString(body, ref pos), "user", 128, false);
        string password = ReadBundleString(body, ref pos);
        string readOnlyText = pos < body.Length ? ReadBundleString(body, ref pos) : "1";
        string autoCommitText = pos < body.Length ? ReadBundleString(body, ref pos) : "1";
        if (pos != body.Length) throw new Exception("bad-db-request");
        if (!DbHostRe.IsMatch(host)) throw new Exception("db-bad-host");
        if (password.Length > 1024) throw new Exception("db-long-password");
        int port;
        if (!int.TryParse(portText.Trim().Length == 0 ? "3306" : portText.Trim(), out port) || port < 1 || port > 65535)
            throw new Exception("db-bad-port");
        bool readOnly = readOnlyText != "0";
        bool autoCommit = readOnly || autoCommitText != "0";   // 읽기 전용은 확정할 것이 없으므로 늘 자동 커밋

        SweepDbSessions();
        lock (DbSessionsLock) if (DbSessions.Count >= MaxDbSessions) throw new Exception("db-too-many-sessions");

        string id = Guid.NewGuid().ToString("N");
        string runnerPath = Path.Combine(Path.GetTempPath(), "classdock_db_worker_" + id + ".py");
        File.WriteAllBytes(runnerPath, DbWorkerRunner);
        DbSession session = new DbSession();
        session.Id = id;
        session.RunnerPath = runnerPath;
        session.ReadOnly = readOnly;
        session.Label = user + "@" + host + ":" + port + (database.Length > 0 ? "/" + database : "");
        try
        {
            string args = (interp == "py" ? "-3 " : "") + "-u -X utf8 \"" + runnerPath + "\"";
            ProcessStartInfo psi = new ProcessStartInfo(interp, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.WorkingDirectory = Path.GetTempPath();
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

            session.Process = new Process();
            session.Process.StartInfo = psi;
            session.Process.Start();
            StartLimitedReader(session.Process.StandardError, session.Stderr);

            string connectRequest = "{\"action\":\"connect\",\"host\":" + JsonString(host)
                + ",\"port\":" + port
                + ",\"database\":" + JsonString(database)
                + ",\"user\":" + JsonString(user)
                + ",\"password\":" + JsonString(password)
                + ",\"readOnly\":" + (readOnly ? "true" : "false")
                + ",\"autoCommit\":" + (autoCommit ? "true" : "false") + "}";
            string response = DbExchange(session, connectRequest, DbMetadataTimeoutMs);
            if (!DbResponseOk(response))
            {
                StopDbProcess(session);
                return "{\"ok\":false,\"id\":\"\",\"info\":" + DbResponseBody(response) + "}";
            }
            lock (DbSessionsLock) DbSessions[id] = session;
            return "{\"ok\":true,\"id\":" + JsonString(id) + ",\"readOnly\":" + (readOnly ? "true" : "false")
                + ",\"autoCommit\":" + (autoCommit ? "true" : "false")
                + ",\"label\":" + JsonString(session.Label) + ",\"info\":" + DbResponseBody(response) + "}";
        }
        catch
        {
            StopDbProcess(session);
            throw;
        }
    }

    // 워커에 보내는 단순 요청(스키마·테이블·행 수·데이터베이스 전환). 결과를 그대로 돌려준다.
    static string DbMetadataRequest(string sessionId, string requestJson)
    {
        DbSession session = RequireDbSession(sessionId);
        string response = DbExchange(session, requestJson, DbMetadataTimeoutMs);
        return "{\"ok\":" + (DbResponseOk(response) ? "true" : "false") + ",\"info\":" + DbResponseBody(response) + "}";
    }

    static string StartDbQuery(string sessionId, byte[] body)
    {
        DbSession session = RequireDbSession(sessionId);
        if (body == null || body.Length == 0 || body.Length > 4 * 1024 * 1024) throw new Exception("bad-db-request");
        int pos = 0;
        string sql = ReadBundleString(body, ref pos);
        string timeoutText = pos < body.Length ? ReadBundleString(body, ref pos) : "";
        if (pos != body.Length) throw new Exception("bad-db-request");
        if (sql.Trim().Length == 0) throw new Exception("db-empty-sql");
        int seconds = DbQueryDefaultSeconds, parsed;
        if (int.TryParse(timeoutText.Trim(), out parsed) && parsed > 0)
            seconds = Math.Max(5, Math.Min(DbQueryMaxSeconds, parsed));

        SweepDbJobs();
        DbQueryJob job = new DbQueryJob();
        job.Id = Guid.NewGuid().ToString("N");
        job.SessionId = session.Id;
        lock (DbJobsLock) DbJobs[job.Id] = job;

        string request = "{\"action\":\"query\",\"sql\":" + JsonString(sql) + "}";
        Thread worker = new Thread(delegate()
        {
            string response = "", error = "";
            try { response = DbExchange(session, request, seconds * 1000, null, job); }
            catch (Exception ex) { error = FlattenMessage(ex); }
            lock (job.Sync)
            {
                job.ResultJson = response;
                job.Error = error;
                job.DoneAt = DateTime.UtcNow;
                job.Complete = true;
            }
            SweepDbJobs();
        });
        worker.IsBackground = true;
        worker.Start();
        return "{\"job\":" + JsonString(job.Id) + ",\"timeoutSeconds\":" + seconds + "}";
    }

    /* SQL 덤프를 시작한다. 본문은 길이 접두 문자열의 평평한 줄이다 —
       파일 이름 · 모드 · 옵션 여섯 개 · 데이터베이스 · 대상 수에 이어 (종류, 이름) 이 온다.

       파일 경로는 여기서만 만든다. 워커에는 다 만들어진 절대 경로를 넘긴다 —
       저장 위치를 SaveRoot 아래로 묶는 정책은 런처가 쥐고 있어야 뚫리지 않는다.
       SQL 문장은 만들지 않는다. 이름과 값을 JSON 값으로만 옮기고 조립은 워커가 한다. */
    static string StartDbDump(string sessionId, byte[] body)
    {
        DbSession session = RequireDbSession(sessionId);
        if (body == null || body.Length == 0 || body.Length > 1024 * 1024) throw new Exception("bad-db-request");
        int pos = 0;
        string fileName = ReadBundleString(body, ref pos);
        string mode = ReadBundleString(body, ref pos);
        string dropIfExists = ReadBundleString(body, ref pos);
        string createIfNotExists = ReadBundleString(body, ref pos);
        string insertForm = ReadBundleString(body, ref pos);
        string columnNames = ReadBundleString(body, ref pos);
        string rowLimit = ReadBundleString(body, ref pos);
        string consistent = ReadBundleString(body, ref pos);
        string database = DbCheckField(ReadBundleString(body, ref pos), "database", 64, true);
        int count;
        if (!int.TryParse(ReadBundleString(body, ref pos), out count) || count < 1 || count > MaxDbDumpObjects)
            throw new Exception("db-dump-bad-count");

        if (mode != "structure" && mode != "data" && mode != "both") throw new Exception("db-dump-bad-mode");
        if (insertForm != "insert" && insertForm != "ignore" && insertForm != "replace") insertForm = "insert";
        int limitValue;
        if (!int.TryParse(rowLimit, out limitValue) || limitValue < 0) limitValue = 0;

        StringBuilder objects = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            string kind = ReadBundleString(body, ref pos);
            string name = DbCheckField(ReadBundleString(body, ref pos), "object", 128, false);
            if (kind != "table" && kind != "view" && kind != "procedure"
                && kind != "function" && kind != "trigger" && kind != "event")
                throw new Exception("db-dump-bad-kind");
            if (objects.Length > 0) objects.Append(',');
            objects.Append("{\"kind\":").Append(JsonString(kind))
                   .Append(",\"name\":").Append(JsonString(name)).Append('}');
        }
        if (pos != body.Length) throw new Exception("bad-db-request");

        // 이름은 사용자가 준다. 확장자는 여기서 못 박는다 — 워커도 .sql 이 아니면 거절한다.
        string safe = SafeRelPath(fileName);
        if (safe == null) throw new Exception("db-dump-bad-path");
        if (!safe.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) safe += ".sql";
        string full;
        if (!TryResolveSaveRootPath(safe, out full)) throw new Exception("db-dump-bad-path");
        string folder = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        SweepDbJobs();
        DbQueryJob job = new DbQueryJob();
        job.Id = Guid.NewGuid().ToString("N");
        job.SessionId = session.Id;
        lock (DbJobsLock) DbJobs[job.Id] = job;

        string request = "{\"action\":\"dump\",\"path\":" + JsonString(full)
            + ",\"mode\":" + JsonString(mode)
            + ",\"database\":" + JsonString(database)
            + ",\"options\":{\"dropIfExists\":" + (dropIfExists == "1" ? "true" : "false")
            + ",\"createIfNotExists\":" + (createIfNotExists == "1" ? "true" : "false")
            + ",\"insertForm\":" + JsonString(insertForm)
            + ",\"columnNames\":" + (columnNames == "1" ? "true" : "false")
            + ",\"rowLimit\":" + limitValue
            + ",\"consistent\":" + (consistent == "1" ? "true" : "false") + "}"
            + ",\"objects\":[" + objects + "]}";

        Thread worker = new Thread(delegate()
        {
            string response = "", error = "";
            try
            {
                response = DbExchange(session, request, DbDumpIdleMs, delegate(string progress)
                {
                    lock (job.Sync) job.Progress = progress;
                }, job);
            }
            catch (Exception ex) { error = FlattenMessage(ex); }
            lock (job.Sync)
            {
                job.ResultJson = response;
                job.Error = error;
                job.DoneAt = DateTime.UtcNow;
                job.Complete = true;
            }
            SweepDbJobs();
        });
        worker.IsBackground = true;
        worker.Start();
        return "{\"job\":" + JsonString(job.Id) + ",\"path\":" + JsonString(full)
            + ",\"name\":" + JsonString(Path.GetFileName(full)) + "}";
    }

    /* 셀 값 읽기. 값에는 줄바꿈·따옴표가 들어올 수 있어 주소가 아니라 본문으로 받는다.
       런처는 SQL 을 만들지 않는다 — 이름과 값을 JSON 값으로만 옮기고 문장 조립은 워커가 한다. */
    static string DbCellRequest(string sessionId, byte[] body)
    {
        DbSession session = RequireDbSession(sessionId);
        if (body == null || body.Length == 0 || body.Length > 64 * 1024) throw new Exception("bad-db-request");
        int pos = 0;
        string database = ReadBundleString(body, ref pos);
        string table = ReadBundleString(body, ref pos);
        string column = ReadBundleString(body, ref pos);
        string keys = DbReadKeys(body, ref pos);
        if (pos != body.Length) throw new Exception("bad-db-request");
        if (table.Trim().Length == 0 || column.Trim().Length == 0) throw new Exception("db-bad-cell-target");

        string request = "{\"action\":\"cell-read\",\"database\":" + JsonString(database)
            + ",\"table\":" + JsonString(table) + ",\"column\":" + JsonString(column)
            + ",\"keys\":[" + keys + "]}";
        string response = DbExchange(session, request, DbMetadataTimeoutMs);
        return "{\"ok\":" + (DbResponseOk(response) ? "true" : "false") + ",\"info\":" + DbResponseBody(response) + "}";
    }

    // 기본키 조건: 개수 + (이름, 값) 짝. 이름도 값도 JSON 값으로만 옮긴다.
    static string DbReadKeys(byte[] body, ref int pos)
    {
        int count;
        if (!int.TryParse(ReadBundleString(body, ref pos), out count) || count < 1 || count > 64)
            throw new Exception("db-bad-cell-key");
        StringBuilder keys = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            string name = ReadBundleString(body, ref pos);
            string value = ReadBundleString(body, ref pos);
            if (name.Trim().Length == 0) throw new Exception("db-bad-cell-key");
            if (keys.Length > 0) keys.Append(',');
            keys.Append("{\"name\":").Append(JsonString(name)).Append(",\"value\":").Append(JsonString(value)).Append('}');
        }
        return keys.ToString();
    }

    /* 모아 둔 변경을 한 번에 적용한다. 본문은 길이 접두 문자열의 평평한 줄이다 —
       데이터베이스·테이블·건수에 이어 변경마다 갈래(update/delete/insert)와 그 값이 온다.
       런처는 갈래 이름만 알아보고 나머지는 JSON 값으로 옮긴다. 문장은 워커가 만든다. */
    static string DbApplyRequest(string sessionId, byte[] body)
    {
        DbSession session = RequireDbSession(sessionId);
        if (body == null || body.Length == 0 || body.Length > 4 * 1024 * 1024) throw new Exception("bad-db-request");
        int pos = 0;
        string database = ReadBundleString(body, ref pos);
        string table = ReadBundleString(body, ref pos);
        if (table.Trim().Length == 0) throw new Exception("db-bad-cell-target");
        int count;
        if (!int.TryParse(ReadBundleString(body, ref pos), out count) || count < 1 || count > 500)
            throw new Exception("db-bad-change-count");

        StringBuilder changes = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            string kind = ReadBundleString(body, ref pos);
            if (changes.Length > 0) changes.Append(',');
            changes.Append("{\"kind\":").Append(JsonString(kind));
            if (kind == "update")
            {
                string column = ReadBundleString(body, ref pos);
                string value = ReadBundleString(body, ref pos);
                string valueNull = ReadBundleString(body, ref pos);
                if (column.Trim().Length == 0) throw new Exception("db-bad-cell-target");
                changes.Append(",\"column\":").Append(JsonString(column));
                changes.Append(",\"value\":").Append(JsonString(value));
                changes.Append(",\"valueNull\":").Append(valueNull == "1" ? "true" : "false");
                changes.Append(",\"keys\":[").Append(DbReadKeys(body, ref pos)).Append(']');
            }
            else if (kind == "delete")
            {
                changes.Append(",\"keys\":[").Append(DbReadKeys(body, ref pos)).Append(']');
            }
            else if (kind == "insert")
            {
                int values;
                if (!int.TryParse(ReadBundleString(body, ref pos), out values) || values < 0 || values > 512)
                    throw new Exception("db-bad-change-count");
                StringBuilder cells = new StringBuilder();
                for (int c = 0; c < values; c++)
                {
                    string column = ReadBundleString(body, ref pos);
                    string value = ReadBundleString(body, ref pos);
                    string isNull = ReadBundleString(body, ref pos);
                    if (column.Trim().Length == 0) throw new Exception("db-bad-cell-target");
                    if (cells.Length > 0) cells.Append(',');
                    cells.Append("{\"column\":").Append(JsonString(column)).Append(",\"value\":").Append(JsonString(value))
                         .Append(",\"null\":").Append(isNull == "1" ? "true" : "false").Append('}');
                }
                changes.Append(",\"values\":[").Append(cells).Append(']');
            }
            else throw new Exception("db-bad-change-kind");
            changes.Append('}');
        }
        if (pos != body.Length) throw new Exception("bad-db-request");

        string request = "{\"action\":\"apply-edits\",\"database\":" + JsonString(database)
            + ",\"table\":" + JsonString(table) + ",\"changes\":[" + changes + "]}";
        string response = DbExchange(session, request, DbMetadataTimeoutMs);
        return "{\"ok\":" + (DbResponseOk(response) ? "true" : "false") + ",\"info\":" + DbResponseBody(response) + "}";
    }

    static string PollDbQuery(string jobId)
    {
        DbQueryJob job;
        lock (DbJobsLock) if (!DbJobs.TryGetValue(jobId ?? "", out job))
            return "{\"done\":true,\"ok\":false,\"info\":{\"ok\":false,\"code\":\"job-not-found\",\"detail\":\"\"}}";
        lock (job.Sync)
        {
            // 진행 보고는 워커가 보낸 JSON 그대로다(덤프처럼 오래 걸리는 작업만 보낸다).
            string progress = job.Progress.Length > 0 ? ",\"progress\":" + job.Progress : "";
            if (!job.Complete)
                return "{\"done\":false,\"cancelling\":" + (job.CancelRequested ? "true" : "false")
                    + progress + "}";
            if (job.Error.Length > 0)
                return "{\"done\":true,\"ok\":false,\"info\":{\"ok\":false,\"code\":"
                    + JsonString(job.CancelRequested ? "cancelled" : "exec-failed")
                    + ",\"detail\":" + JsonString(job.Error) + "}}";
            return "{\"done\":true,\"ok\":" + (DbResponseOk(job.ResultJson) ? "true" : "false")
                + ",\"cancelled\":" + (job.CancelRequested ? "true" : "false")
                + ",\"info\":" + DbResponseBody(job.ResultJson) + "}";
        }
    }

    static void CancelDbQuery(string jobId)
    {
        DbQueryJob job;
        lock (DbJobsLock) if (!DbJobs.TryGetValue(jobId ?? "", out job)) return;
        bool started;
        lock (job.Sync)
        {
            if (job.Complete || job.CancelRequested) return;
            job.CancelRequested = true;
            started = job.Started;
        }
        // ExecLock 앞에서 기다리는 작업은 DbExchange 가 CancelRequested 를 보고 보내지 않는다.
        // 여기서 cancel 을 보내면 현재 실행 중인 '다른' 작업을 죽이므로 반드시 멈춘다.
        if (!started) return;
        DbSession session;
        lock (DbSessionsLock) if (!DbSessions.TryGetValue(job.SessionId ?? "", out session)) return;
        lock (session.StdinLock)
        {
            // 작업이 막 끝난 경우 다음 요청으로 취소가 넘어가지 않게 id 를 다시 확인한다.
            if (session.ActiveJobId != job.Id) return;
            DbSendCancel(session);
        }
    }

    // 프로세스와 임시 러너만 정리한다(세션 목록에서 지우는 일은 호출부가 한다).
    static void StopDbProcess(DbSession session)
    {
        if (session == null) return;
        if (session.Process != null)
        {
            // 워커가 커넥션을 정상적으로 닫을 기회를 먼저 준다. 서버에 유령 커넥션을 남기지 않는다.
            try { if (!session.Process.HasExited) DbWriteLine(session, "{\"action\":\"close\"}"); } catch { }
            // 덤프는 cancel 뒤 임시 파일을 닫고 지울 시간이 필요하다. 평소에는 즉시 끝나며,
            // 실행 중인 작업이 있을 때만 최대 5초까지 정상 종료를 기다린다.
            try { if (!session.Process.WaitForExit(5000)) KillProcessTree(session.Process); }
            catch { KillProcessTree(session.Process); }
        }
        try { if (File.Exists(session.RunnerPath)) File.Delete(session.RunnerPath); } catch { }
    }

    static void StopDbSession(string id)
    {
        DbSession session = null;
        lock (DbSessionsLock) if (DbSessions.TryGetValue(id ?? "", out session)) DbSessions.Remove(id ?? "");
        StopDbProcess(session);
    }

    static void SweepDbSessions()
    {
        List<DbSession> stale = new List<DbSession>();
        lock (DbSessionsLock)
        {
            foreach (DbSession session in DbSessions.Values)
            {
                bool dead = false;
                try { dead = session.Process == null || session.Process.HasExited; } catch { dead = true; }
                if (dead || (DateTime.UtcNow - session.LastUsed).TotalMinutes > DbIdleMinutes) stale.Add(session);
            }
            foreach (DbSession session in stale) DbSessions.Remove(session.Id);
        }
        foreach (DbSession session in stale) StopDbProcess(session);
    }

    // 끝난 쿼리는 결과를 잠시 남겨 둔다(폴링이 늦게 와도 결과를 볼 수 있게).
    /* CSV·엑셀 적재. 덤프와 같은 모양으로 작업을 만들고 폴링·취소는 /db-query-* 를 함께 쓴다.
       런처는 여기서도 SQL 을 만들지 않는다 — 테이블·컬럼 이름과 값을 JSON 값으로 옮길 뿐이고,
       INSERT 문장은 워커가 자리표시자로 짓는다. 값이 문장에 붙는 순간 따옴표·NULL 구분이 무너진다.
       ⚠ 프런트가 싣는 차례(MNDbImport.requestValues)와 여기서 읽는 차례가 어긋나면 값이 옆
       컬럼으로 들어간다. /db-apply · /db-dump 와 같은 이유로 테스트가 두 쪽을 나란히 놓고 본다. */
    static string StartDbImport(string sessionId, byte[] body)
    {
        DbSession session = RequireDbSession(sessionId);
        if (body == null || body.Length == 0 || body.Length > MaxDbImportBytes) throw new Exception("bad-db-request");
        int pos = 0;
        string database = DbCheckField(ReadBundleString(body, ref pos), "database", 64, true);
        string table = DbCheckField(ReadBundleString(body, ref pos), "table", 128, false);
        string mode = ReadBundleString(body, ref pos);
        if (mode != "insert" && mode != "ignore" && mode != "update") throw new Exception("db-import-bad-mode");

        int columnCount;
        if (!int.TryParse(ReadBundleString(body, ref pos), out columnCount) || columnCount < 1 || columnCount > 512)
            throw new Exception("db-import-bad-count");
        StringBuilder columns = new StringBuilder();
        for (int i = 0; i < columnCount; i++)
        {
            string name = DbCheckField(ReadBundleString(body, ref pos), "column", 128, false);
            if (columns.Length > 0) columns.Append(',');
            columns.Append(JsonString(name));
        }

        int rowCount;
        if (!int.TryParse(ReadBundleString(body, ref pos), out rowCount) || rowCount < 1 || rowCount > MaxDbImportRows)
            throw new Exception("db-import-bad-rows");
        if ((long)rowCount * columnCount > MaxDbImportCells) throw new Exception("db-import-too-many-cells");
        StringBuilder rows = new StringBuilder();
        for (int r = 0; r < rowCount; r++)
        {
            if (rows.Length > 0) rows.Append(',');
            rows.Append('[');
            for (int c = 0; c < columnCount; c++)
            {
                string value = ReadBundleString(body, ref pos);
                string isNull = ReadBundleString(body, ref pos);
                if (c > 0) rows.Append(',');
                // NULL 은 JSON null 로 보낸다. 빈 문자열과 확실히 갈라야 두 값이 섞이지 않는다.
                rows.Append(isNull == "1" ? "null" : JsonString(value));
            }
            rows.Append(']');
        }
        if (pos != body.Length) throw new Exception("bad-db-request");

        SweepDbJobs();
        DbQueryJob job = new DbQueryJob();
        job.Id = Guid.NewGuid().ToString("N");
        job.SessionId = session.Id;
        lock (DbJobsLock) DbJobs[job.Id] = job;

        string request = "{\"action\":\"import-rows\",\"database\":" + JsonString(database)
            + ",\"table\":" + JsonString(table)
            + ",\"mode\":" + JsonString(mode)
            + ",\"columns\":[" + columns + "]"
            + ",\"rows\":[" + rows + "]}";

        Thread worker = new Thread(delegate()
        {
            string response = "", error = "";
            try
            {
                response = DbExchange(session, request, DbDumpIdleMs, delegate(string progress)
                {
                    lock (job.Sync) job.Progress = progress;
                }, job);
            }
            catch (Exception ex) { error = FlattenMessage(ex); }
            lock (job.Sync)
            {
                job.ResultJson = response;
                job.Error = error;
                job.DoneAt = DateTime.UtcNow;
                job.Complete = true;
            }
            SweepDbJobs();
        });
        worker.IsBackground = true;
        worker.Start();
        return "{\"job\":" + JsonString(job.Id) + ",\"rows\":" + rowCount + "}";
    }

    static void SweepDbJobs()
    {
        lock (DbJobsLock)
        {
            List<DbQueryJob> done = new List<DbQueryJob>();
            foreach (DbQueryJob job in DbJobs.Values) if (job.Complete) done.Add(job);
            done.Sort(delegate(DbQueryJob a, DbQueryJob b) { return a.DoneAt.CompareTo(b.DoneAt); });
            DateTime now = DateTime.UtcNow;
            List<DbQueryJob> remove = new List<DbQueryJob>();
            foreach (DbQueryJob job in done)
                if ((now - job.DoneAt).TotalMinutes > 10) remove.Add(job);
            for (int i = 0; i < done.Count - 16; i++)
                if (!remove.Contains(done[i])) remove.Add(done[i]);
            foreach (DbQueryJob job in remove) DbJobs.Remove(job.Id);
        }
    }

    static List<int> ProcessTreeIds(int rootPid)
    {
        var parent = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap != IntPtr.Zero && snap.ToInt64() != -1)
        {
            try
            {
                PROCESSENTRY32 pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (Process32First(snap, ref pe))
                {
                    do { parent[(int)pe.th32ProcessID] = (int)pe.th32ParentProcessID; }
                    while (Process32Next(snap, ref pe));
                }
            }
            finally { CloseHandle(snap); }
        }
        var tree = new HashSet<int>();
        tree.Add(rootPid);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var pair in parent)
                if (tree.Contains(pair.Value) && tree.Add(pair.Key)) changed = true;
        }
        return new List<int>(tree);
    }

    static void KillProcessTree(Process process)
    {
        if (process == null) return;
        int rootPid;
        try { rootPid = process.Id; } catch { return; }
        // taskkill 결과만 믿지 않는다. 부모가 먼저 끝났거나 새 자식이 생긴 경우에도
        // 스냅샷에서 찾은 후손 PID를 직접 종료하고 짧게 재확인한다.
        List<int> initialTree = ProcessTreeIds(rootPid);
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("taskkill", "/PID " + rootPid + " /T /F");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process killer = Process.Start(psi);
            if (killer != null) killer.WaitForExit(5000);
        }
        catch { }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            List<int> tree = attempt == 0 ? initialTree : ProcessTreeIds(rootPid);
            foreach (int pid in tree)
            {
                if (pid == rootPid) continue;
                try { using (Process child = Process.GetProcessById(pid)) child.Kill(); } catch { }
            }
            try { using (Process root = Process.GetProcessById(rootPid)) root.Kill(); } catch { }
            if (attempt < 2) Thread.Sleep(100);
        }
    }

    static void EnableJobKillOnClose(IntPtr job)
    {
        if (job == IntPtr.Zero) return;
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static long ProcessTreeWorkingSetBytes(int rootPid)
    {
        var parent = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap.ToInt64() == -1) return 0;
        try
        {
            PROCESSENTRY32 pe = new PROCESSENTRY32();
            pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
            if (Process32First(snap, ref pe))
            {
                do { parent[(int)pe.th32ProcessID] = (int)pe.th32ParentProcessID; }
                while (Process32Next(snap, ref pe));
            }
        }
        finally { CloseHandle(snap); }

        var tree = new HashSet<int>();
        tree.Add(rootPid);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var pair in parent)
                if (tree.Contains(pair.Value) && tree.Add(pair.Key)) changed = true;
        }
        long total = 0;
        foreach (int pid in tree)
        {
            try { using (Process child = Process.GetProcessById(pid)) total += child.WorkingSet64; }
            catch { }
        }
        return total;
    }

    static string ResolveTerminalWorkingDirectory(string requested, out bool fallbackUsed)
    {
        fallbackUsed = false;
        string fallback = CurrentSaveRoot();
        if (string.IsNullOrWhiteSpace(fallback) || !Directory.Exists(fallback))
            fallback = AppDomain.CurrentDomain.BaseDirectory;
        if (string.IsNullOrWhiteSpace(requested)) return Path.GetFullPath(fallback);

        string candidate = requested.Trim();
        if (!Path.IsPathRooted(candidate)) candidate = Path.Combine(fallback, candidate);
        candidate = Path.GetFullPath(candidate);
        if (File.Exists(candidate)) candidate = Path.GetDirectoryName(candidate);
        if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate)) return candidate;

        // 브라우저 폴더 드래그·복원 문서는 논리 경로만 알고 원본 절대경로에 접근하지 못할 수 있다.
        // 그 경로가 디스크에 없다고 명령 자체를 막지 말고, 가장 가까운 실제 상위 폴더에서 시작한다.
        fallbackUsed = true;
        string parent = candidate;
        while (!string.IsNullOrWhiteSpace(parent))
        {
            try
            {
                parent = Path.GetDirectoryName(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent)) return parent;
            }
            catch { break; }
        }
        return Path.GetFullPath(fallback);
    }

    static string PowerShellLiteral(string value)
    {
        return "'" + (value ?? "").Replace("'", "''") + "'";
    }

    static string TerminalCompletionJson(byte[] body)
    {
        if (body == null || body.Length == 0 || body.Length > 64 * 1024)
            throw new Exception("bad-terminal-completion");
        int pos = 0;
        string requestedCwd = ReadBundleString(body, ref pos);
        string fragment = ReadBundleString(body, ref pos);
        string directoryFlag = ReadBundleString(body, ref pos);
        if (pos != body.Length || fragment.IndexOf('\0') >= 0)
            throw new Exception("bad-terminal-completion");

        bool ignoredFallback;
        string cwd = ResolveTerminalWorkingDirectory(requestedCwd, out ignoredFallback);
        string typed = fragment ?? "";
        int separatorAt = Math.Max(typed.LastIndexOf('\\'), typed.LastIndexOf('/'));
        string typedDir = separatorAt >= 0 ? typed.Substring(0, separatorAt + 1) : "";
        string leaf = separatorAt >= 0 ? typed.Substring(separatorAt + 1) : typed;
        string lookupDir;
        if (typedDir.StartsWith("~\\", StringComparison.Ordinal) || typedDir.StartsWith("~/", StringComparison.Ordinal))
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            lookupDir = Path.Combine(profile, typedDir.Substring(2).Replace('/', Path.DirectorySeparatorChar));
        }
        else if (Path.IsPathRooted(typedDir))
        {
            lookupDir = Path.GetFullPath(typedDir);
        }
        else
        {
            lookupDir = Path.GetFullPath(Path.Combine(cwd, typedDir.Replace('/', Path.DirectorySeparatorChar)));
        }
        if (!Directory.Exists(lookupDir)) return "{\"items\":[]}";

        bool directoriesOnly = directoryFlag == "1";
        List<FileSystemInfo> matches = new List<FileSystemInfo>();
        try
        {
            DirectoryInfo directory = new DirectoryInfo(lookupDir);
            foreach (DirectoryInfo item in directory.GetDirectories())
                if (item.Name.StartsWith(leaf, StringComparison.OrdinalIgnoreCase)) matches.Add(item);
            if (!directoriesOnly)
                foreach (FileInfo item in directory.GetFiles())
                    if (item.Name.StartsWith(leaf, StringComparison.OrdinalIgnoreCase)) matches.Add(item);
        }
        catch { return "{\"items\":[]}"; }
        matches.Sort(delegate(FileSystemInfo a, FileSystemInfo b)
        {
            bool ad = a is DirectoryInfo;
            bool bd = b is DirectoryInfo;
            if (ad != bd) return ad ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        char separator = typedDir.IndexOf('/') >= 0 && typedDir.IndexOf('\\') < 0 ? '/' : '\\';
        StringBuilder json = new StringBuilder("{\"items\":[");
        int limit = Math.Min(matches.Count, 120);
        for (int i = 0; i < limit; i++)
        {
            if (i > 0) json.Append(',');
            FileSystemInfo item = matches[i];
            bool isDirectory = item is DirectoryInfo;
            string value = typedDir + item.Name + (isDirectory ? separator.ToString() : "");
            json.Append("{\"value\":").Append(JsonString(value))
                .Append(",\"directory\":").Append(isDirectory ? "true" : "false").Append('}');
        }
        return json.Append("]}").ToString();
    }

    static string OpenTerminalSession(byte[] body)
    {
        if (body == null || body.Length == 0 || body.Length > 64 * 1024)
            throw new Exception("bad-terminal-request");
        int pos = 0;
        string requestedCwd = ReadBundleString(body, ref pos);
        if (pos != body.Length || requestedCwd.IndexOf('\0') >= 0)
            throw new Exception("bad-terminal-request");

        SweepTerminalSessions();
        TerminalSession session = new TerminalSession();
        session.Id = Guid.NewGuid().ToString("N");
        bool cwdFallback;
        session.Cwd = ResolveTerminalWorkingDirectory(requestedCwd, out cwdFallback);
        session.CwdFallback = cwdFallback;
        session.Marker = "__CLASSDOCK_TERMINAL_DONE_" + session.Id + "__";
        session.ScriptPath = Path.Combine(Path.GetTempPath(), "classdock_terminal_" + session.Id + ".ps1");

        // 명령은 UTF-8 Base64 한 줄로 전달한다. 스크립트블록을 현재 범위에 dot-source하여
        // cd, 환경변수와 PowerShell 변수가 다음 명령에도 그대로 유지되게 한다.
        string script =
            "$ErrorActionPreference = 'Continue'\r\n" +
            "$mnUtf8 = New-Object System.Text.UTF8Encoding $false\r\n" +
            "[Console]::InputEncoding = $mnUtf8\r\n" +
            "[Console]::OutputEncoding = $mnUtf8\r\n" +
            "$OutputEncoding = $mnUtf8\r\n" +
            "Set-Location -LiteralPath " + PowerShellLiteral(session.Cwd) + "\r\n" +
            "$mnMarker = " + PowerShellLiteral(session.Marker) + "\r\n" +
            "while (($mnLine = [Console]::In.ReadLine()) -ne $null) {\r\n" +
            "  $mnSep = $mnLine.IndexOf('|')\r\n" +
            "  if ($mnSep -lt 1) { continue }\r\n" +
            "  $mnSeq = $mnLine.Substring(0, $mnSep)\r\n" +
            "  $mnExitCode = 0\r\n" +
            "  try {\r\n" +
            "    $mnCommand = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($mnLine.Substring($mnSep + 1)))\r\n" +
            "    $global:LASTEXITCODE = $null\r\n" +
            "    $mnErrorCount = $Error.Count\r\n" +
            // 지속형 while 안에서 객체를 그대로 내보내면 Get-ChildItem(ls)도 속성 목록으로 풀린다.
            // 실제 콘솔 호스트처럼 Out-Default를 거치게 해 기본 Table/List 뷰와 열 정렬을 적용한다.
            "    . ([ScriptBlock]::Create($mnCommand)) | Out-Default\r\n" +
            "    $mnPipelineSucceeded = $?\r\n" +
            "    $mnSucceeded = $mnPipelineSucceeded -and ($Error.Count -eq $mnErrorCount)\r\n" +
            "    if ($null -ne $LASTEXITCODE) { $mnExitCode = [int]$LASTEXITCODE }\r\n" +
            "    elseif (-not $mnSucceeded) { $mnExitCode = 1 }\r\n" +
            "  } catch {\r\n" +
            "    [Console]::Error.WriteLine($_.Exception.Message)\r\n" +
            "    $mnExitCode = 1\r\n" +
            "  } finally {\r\n" +
            "    $mnCwd = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Location).Path))\r\n" +
            "    [Console]::Out.WriteLine($mnMarker + '|' + $mnSeq + '|' + $mnExitCode + '|' + $mnCwd)\r\n" +
            "  }\r\n" +
            "}\r\n";
        File.WriteAllText(session.ScriptPath, script, new UTF8Encoding(true));

        string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string powershell = Path.Combine(systemDir, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";
        ProcessStartInfo psi = new ProcessStartInfo(
            powershell,
            "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + session.ScriptPath + "\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.WorkingDirectory = session.Cwd;

        session.Process = new Process();
        session.Process.StartInfo = psi;
        try
        {
            session.Process.Start();
            session.Input = new StreamWriter(session.Process.StandardInput.BaseStream, new UTF8Encoding(false));
            session.Input.AutoFlush = true;
            session.JobHandle = CreateJobObject(IntPtr.Zero, null);
            if (session.JobHandle != IntPtr.Zero)
            {
                EnableJobKillOnClose(session.JobHandle);
                if (!AssignProcessToJobObject(session.JobHandle, session.Process.Handle))
                {
                    CloseHandle(session.JobHandle);
                    session.JobHandle = IntPtr.Zero;
                }
            }
            lock (TerminalSessionsLock) TerminalSessions[session.Id] = session;
        }
        catch
        {
            if (session.JobHandle != IntPtr.Zero)
            {
                try { CloseHandle(session.JobHandle); } catch { }
                session.JobHandle = IntPtr.Zero;
            }
            try { if (session.Process != null) KillProcessTree(session.Process); } catch { }
            try { if (File.Exists(session.ScriptPath)) File.Delete(session.ScriptPath); } catch { }
            throw;
        }

        Thread outReader = StartTerminalOutputReader(session);
        Thread errReader = StartTerminalErrorReader(session);
        Thread watcher = new Thread(delegate()
        {
            bool exited = false;
            while (!exited)
            {
                try { exited = session.Process.WaitForExit(250); } catch { break; }
                if (exited) break;
                bool running;
                lock (session.Sync) { running = session.CommandRunning; }
                if (!running) continue;
                bool memoryLimit = ProcessTreeWorkingSetBytes(session.Process.Id) > PythonProcessMemoryLimitBytes;
                if (memoryLimit)
                {
                    lock (session.Sync)
                    {
                        session.Stderr.AppendLine("\n[메모리 제한: 터미널 명령이 4GB를 넘어 종료했습니다.]");
                    }
                    StopTerminalSession(session.Id);
                    break;
                }
            }
            try { session.Process.WaitForExit(2000); } catch { }
            try { outReader.Join(2000); errReader.Join(2000); } catch { }
            int processCode;
            try { processCode = session.Process.ExitCode; } catch { processCode = -1; }
            IntPtr completedJob = IntPtr.Zero;
            lock (session.Sync)
            {
                session.ShellExited = true;
                if (session.CommandRunning)
                {
                    session.CommandRunning = false;
                    session.CommandComplete = true;
                    session.ExitCode = session.StopRequested ? 130 : processCode;
                }
                session.DoneAt = DateTime.UtcNow;
                completedJob = session.JobHandle;
                session.JobHandle = IntPtr.Zero;
            }
            if (completedJob != IntPtr.Zero) try { CloseHandle(completedJob); } catch { }
            try { if (File.Exists(session.ScriptPath)) File.Delete(session.ScriptPath); } catch { }
            SweepTerminalSessions();
        });
        watcher.IsBackground = true;
        watcher.Start();
        bool reportFallback = session.CwdFallback;
        session.CwdFallback = false;
        return "{\"id\":" + JsonString(session.Id)
            + ",\"cwd\":" + JsonString(session.Cwd)
            + ",\"cwdFallback\":" + (reportFallback ? "true" : "false") + "}";
    }

    static Thread StartTerminalOutputReader(TerminalSession session)
    {
        Thread thread = new Thread(delegate()
        {
            try
            {
                string prefix = session.Marker + "|";
                ReadTerminalLines(session.Process.StandardOutput.BaseStream, delegate(string line)
                {
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        string[] parts = line.Split(new char[] { '|' }, 4);
                        int sequence;
                        int code;
                        if (parts.Length == 4 && int.TryParse(parts[1], out sequence) && int.TryParse(parts[2], out code))
                        {
                            string nextCwd = "";
                            try { nextCwd = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3])); } catch { }
                            lock (session.Sync)
                            {
                                if (sequence == session.Sequence)
                                {
                                    if (!string.IsNullOrWhiteSpace(nextCwd) && Directory.Exists(nextCwd)) session.Cwd = nextCwd;
                                    session.ExitCode = code;
                                    session.CommandRunning = false;
                                    session.CommandComplete = true;
                                    session.LastUsed = DateTime.UtcNow;
                                }
                            }
                            return;
                        }
                    }
                    lock (session.Sync) session.Stdout.AppendLine(line);
                });
            }
            catch { }
        });
        thread.IsBackground = true;
        thread.Start();
        return thread;
    }

    static Thread StartTerminalErrorReader(TerminalSession session)
    {
        Thread thread = new Thread(delegate()
        {
            try
            {
                ReadTerminalLines(session.Process.StandardError.BaseStream, delegate(string line)
                {
                    lock (session.Sync) session.Stderr.AppendLine(line);
                });
            }
            catch { }
        });
        thread.IsBackground = true;
        thread.Start();
        return thread;
    }

    static readonly Encoding StrictTerminalUtf8 = new UTF8Encoding(false, true);
    static readonly Encoding TerminalOemEncoding = CreateTerminalOemEncoding();

    static Encoding CreateTerminalOemEncoding()
    {
        try { return Encoding.GetEncoding((int)GetOEMCP()); }
        catch { return Encoding.Default; }
    }

    // PowerShell itself writes UTF-8, but legacy Windows commands such as tree.com write
    // the active OEM code page directly to the redirected pipe. Decode each complete line
    // as strict UTF-8 first and fall back to the Windows OEM encoding only when necessary.
    static string DecodeTerminalLine(byte[] bytes, int count)
    {
        try { return StrictTerminalUtf8.GetString(bytes, 0, count); }
        catch (DecoderFallbackException) { return TerminalOemEncoding.GetString(bytes, 0, count); }
    }

    static void ReadTerminalLines(Stream stream, Action<string> onLine)
    {
        byte[] buffer = new byte[4096];
        List<byte> pending = new List<byte>();
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                byte value = buffer[i];
                if (value != (byte)'\n')
                {
                    pending.Add(value);
                    continue;
                }
                int count = pending.Count;
                if (count > 0 && pending[count - 1] == (byte)'\r') count--;
                byte[] line = pending.ToArray();
                pending.Clear();
                onLine(DecodeTerminalLine(line, count));
            }
        }
        if (pending.Count > 0)
        {
            int count = pending.Count;
            if (pending[count - 1] == (byte)'\r') count--;
            onLine(DecodeTerminalLine(pending.ToArray(), count));
        }
    }

    static void RunTerminalCommand(string id, byte[] body)
    {
        if (body == null || body.Length == 0 || body.Length > 1024 * 1024)
            throw new Exception("bad-terminal-command");
        int pos = 0;
        string command = ReadBundleString(body, ref pos);
        if (pos != body.Length || string.IsNullOrWhiteSpace(command) || command.IndexOf('\0') >= 0)
            throw new Exception("bad-terminal-command");
        TerminalSession session;
        lock (TerminalSessionsLock) if (!TerminalSessions.TryGetValue(id ?? "", out session))
            throw new Exception("terminal-session-not-found");
        lock (session.Sync)
        {
            if (session.ShellExited) throw new Exception("terminal-session-stopped");
            if (session.CommandRunning) throw new Exception("terminal-command-busy");
            try { if (session.Process.HasExited) throw new Exception("terminal-session-stopped"); }
            catch (InvalidOperationException) { throw new Exception("terminal-session-stopped"); }
            session.Stdout = new LimitedTextBuffer();
            session.Stderr = new LimitedTextBuffer();
            session.ExitCode = -1;
            session.StopRequested = false;
            session.CommandComplete = false;
            session.CommandRunning = true;
            session.CommandStartedAt = DateTime.UtcNow;
            session.LastUsed = DateTime.UtcNow;
            session.Sequence++;
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
            try { session.Input.WriteLine(session.Sequence.ToString() + "|" + encoded); }
            catch
            {
                session.CommandRunning = false;
                session.CommandComplete = true;
                throw new Exception("terminal-session-stopped");
            }
        }
    }

    static string PollTerminalSession(string id)
    {
        TerminalSession session;
        lock (TerminalSessionsLock) if (!TerminalSessions.TryGetValue(id ?? "", out session))
            return "{\"complete\":true,\"alive\":false,\"code\":-1,\"stdout\":\"\",\"stderr\":\"터미널 세션을 찾지 못했습니다.\",\"cwd\":\"\"}";
        lock (session.Sync)
        {
            return "{\"complete\":" + (session.CommandComplete ? "true" : "false")
                + ",\"alive\":" + (session.ShellExited ? "false" : "true")
                + ",\"stopped\":" + (session.StopRequested ? "true" : "false")
                + ",\"code\":" + session.ExitCode
                + ",\"stdout\":" + JsonString(session.Stdout.GetText())
                + ",\"stderr\":" + JsonString(session.Stderr.GetText())
                + ",\"cwd\":" + JsonString(session.Cwd)
                + ",\"cwdFallback\":" + (session.CwdFallback ? "true" : "false") + "}";
        }
    }

    static void StopTerminalSession(string id)
    {
        TerminalSession session = null;
        lock (TerminalSessionsLock) TerminalSessions.TryGetValue(id ?? "", out session);
        if (session == null) return;
        IntPtr job;
        lock (session.Sync)
        {
            session.StopRequested = true;
            if (session.CommandRunning) session.Stderr.AppendLine("[명령 실행을 중지했습니다.]");
            job = session.JobHandle;
        }
        if (job != IntPtr.Zero) try { TerminateJobObject(job, 130); } catch { }
        KillProcessTree(session.Process);
    }

    static void SweepTerminalSessions()
    {
        List<TerminalSession> remove = new List<TerminalSession>();
        lock (TerminalSessionsLock)
        {
            List<TerminalSession> done = new List<TerminalSession>();
            foreach (TerminalSession session in TerminalSessions.Values)
                if (session.ShellExited) done.Add(session);
            done.Sort(delegate(TerminalSession a, TerminalSession b) { return a.DoneAt.CompareTo(b.DoneAt); });
            DateTime now = DateTime.UtcNow;
            foreach (TerminalSession session in done)
                if ((now - session.DoneAt).TotalMinutes > 15) remove.Add(session);
            for (int i = 0; i < done.Count - 24; i++)
                if (!remove.Contains(done[i])) remove.Add(done[i]);
            foreach (TerminalSession session in remove) TerminalSessions.Remove(session.Id);
        }
        foreach (TerminalSession session in remove)
            try { if (File.Exists(session.ScriptPath)) File.Delete(session.ScriptPath); } catch { }
    }

    static string StartPythonSession(byte[] body, bool bundle)
    {
        string interp = FindPython();
        if (interp == null) throw new PythonMissingException();

        SweepRetainedSessions();   // 새 실행 시작 시 오래된 보존 세션의 작업폴더 정리

        string tempRoot = Path.Combine(Path.GetTempPath(), "moidapy_session_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string scriptPath;
        string workDir;
        try
        {
            if (!bundle)
            {
                byte[] src = body;
                try
                {
                    int pos = 0;
                    int sourceLen = ReadBundleInt(body, ref pos);
                    if (sourceLen >= 0 && pos + sourceLen + 4 <= body.Length)
                    {
                        byte[] parsed = new byte[sourceLen];
                        Buffer.BlockCopy(body, pos, parsed, 0, sourceLen);
                        pos += sourceLen;
                        int stdinLen = ReadBundleInt(body, ref pos);
                        if (stdinLen >= 0 && pos + stdinLen == body.Length) src = parsed;
                    }
                }
                catch { }
                scriptPath = Path.Combine(tempRoot, "script.py");
                File.WriteAllBytes(scriptPath, src);
                workDir = tempRoot;
            }
            else
            {
                int pos = 0;
                string target = ReadBundleString(body, ref pos);
                int count = ReadBundleInt(body, ref pos);
                if (count < 0 || count > 100000) throw new Exception("bad-bundle");
                string targetSafe = SafeRelPath(target);
                if (targetSafe == null) throw new Exception("bad-target");
                for (int i = 0; i < count; i++)
                {
                    string rel = ReadBundleString(body, ref pos);
                    int len = ReadBundleInt(body, ref pos);
                    if (len < 0 || pos + len > body.Length) throw new Exception("bad-bundle");
                    string safe = SafeRelPath(rel);
                    if (safe != null)
                    {
                        string full = Path.Combine(tempRoot, safe);
                        string dir = Path.GetDirectoryName(full);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllBytes(full, SubBytes(body, pos, len));
                    }
                    pos += len;
                }
                if (pos < body.Length) ReadBundleString(body, ref pos); // 표준 입력은 대화형 세션에서 별도 전달
                string requestedCwd = "";
                if (pos < body.Length) requestedCwd = ReadBundleString(body, ref pos);
                if (pos < body.Length)
                {
                    int dirCount = ReadBundleInt(body, ref pos);
                    if (dirCount < 0 || dirCount > 100000) throw new Exception("bad-bundle");
                    for (int i = 0; i < dirCount; i++)
                    {
                        string safeDir = SafeRelPath(ReadBundleString(body, ref pos));
                        if (!string.IsNullOrEmpty(safeDir)) Directory.CreateDirectory(Path.Combine(tempRoot, safeDir));
                    }
                }
                if (pos != body.Length) throw new Exception("bad-bundle");
                scriptPath = Path.Combine(tempRoot, targetSafe);
                if (!File.Exists(scriptPath)) throw new Exception("target-not-found");
                workDir = ResolveBundleWorkDir(tempRoot, requestedCwd, Path.GetDirectoryName(scriptPath));
            }
            return StartPythonSessionProcess(interp, scriptPath, workDir, tempRoot);
        }
        catch
        {
            try { Directory.Delete(tempRoot, true); } catch { }
            throw;
        }
    }

    static byte[] SubBytes(byte[] source, int offset, int count)
    {
        byte[] result = new byte[count];
        Buffer.BlockCopy(source, offset, result, 0, count);
        return result;
    }

    static string StartPythonSessionProcess(string interp, string scriptPath, string workDir, string tempRoot)
    {
        PythonSession session = new PythonSession();
        session.Id = Guid.NewGuid().ToString("N");
        session.TempRoot = tempRoot;
        session.RunnerPath = Path.Combine(Path.GetTempPath(), "moidapy_runner_" + session.Id + ".py");
        session.PlotDir = Path.Combine(Path.GetTempPath(), "moidapy_plots_" + session.Id);
        Directory.CreateDirectory(session.PlotDir);
        SnapshotInputs(session);   // 실행 전 작업폴더 파일 목록 기록(나중에 생성/변경된 출력 파일을 구분)
        File.WriteAllText(session.RunnerPath,
            "import os, runpy, sys\n" +
            "sys.argv[0] = os.environ['CLASSDOCK_SCRIPT']\n" +
            "_ps_script_dir = os.path.dirname(os.environ['CLASSDOCK_SCRIPT'])\n" +
            "_ps_project_root = os.environ.get('CLASSDOCK_PROJECT_ROOT', '')\n" +
            "_ps_paths = []\n" +
            "_ps_cur = _ps_script_dir\n" +
            "while _ps_cur:\n" +
            "    _ps_paths.append(_ps_cur)\n" +
            "    if _ps_project_root and os.path.normcase(os.path.abspath(_ps_cur)) == os.path.normcase(os.path.abspath(_ps_project_root)):\n" +
            "        break\n" +
            "    _ps_next = os.path.dirname(_ps_cur)\n" +
            "    if _ps_next == _ps_cur:\n" +
            "        break\n" +
            "    _ps_cur = _ps_next\n" +
            "for _ps_path in reversed(_ps_paths):\n" +
            "    if _ps_path and _ps_path not in sys.path:\n" +
            "        sys.path.insert(0, _ps_path)\n" +
            "try:\n" +
            "    _ps_vars = runpy.run_path(os.environ['CLASSDOCK_SCRIPT'], run_name='__main__')\n" +
            "finally:\n" +
            "    try:\n" +
            "        import matplotlib.pyplot as _ps_plt\n" +
            "        for _ps_i, _ps_n in enumerate(_ps_plt.get_fignums()[:8]):\n" +
            "            _ps_plt.figure(_ps_n).savefig(os.path.join(os.environ['CLASSDOCK_PLOT_DIR'], 'plot_%02d.png' % _ps_i), bbox_inches='tight')\n" +
            "        _ps_plt.close('all')\n" +
            "    except Exception:\n" +
            "        pass\n" +
            "    try:\n" +
            "        import json as _ps_json, types as _ps_types\n" +
            "        _ps_items = []\n" +
            "        for _ps_name in sorted(_ps_vars):\n" +
            "            if not _ps_name or _ps_name.startswith('_'):\n" +
            "                continue\n" +
            "            _ps_value = _ps_vars[_ps_name]\n" +
            "            if isinstance(_ps_value, (_ps_types.ModuleType, _ps_types.FunctionType, _ps_types.BuiltinFunctionType, type)) or callable(_ps_value):\n" +
            "                continue\n" +
            "            try:\n" +
            "                _ps_text = repr(_ps_value)\n" +
            "            except Exception:\n" +
            "                _ps_text = '<값을 표시할 수 없음>'\n" +
            "            if len(_ps_text) > 600:\n" +
            "                _ps_text = _ps_text[:599] + '…'\n" +
            "            _ps_items.append({'name': _ps_name[:120], 'type': type(_ps_value).__name__[:120], 'value': _ps_text})\n" +
            "            if len(_ps_items) >= 80:\n" +
            "                break\n" +
            "        with open(os.path.join(os.environ['CLASSDOCK_PLOT_DIR'], 'variables.json'), 'w', encoding='utf-8') as _ps_file:\n" +
            "            _ps_json.dump(_ps_items, _ps_file, ensure_ascii=False)\n" +
            "    except Exception:\n" +
            "        pass\n", new UTF8Encoding(false));

        string args = (interp == "py" ? "-3 " : "") + "-u -X utf8 \"" + session.RunnerPath + "\"";
        ProcessStartInfo psi = new ProcessStartInfo(interp, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.WorkingDirectory = workDir;
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        psi.EnvironmentVariables["MPLBACKEND"] = "Agg";
        // 작업폴더의 다른 .py 를 import 하면 파이썬이 __pycache__/*.pyc 를 자동으로 만든다. 학생이 만든 적 없는
        // 파일이 "실행이 만든 파일"에 섞이고 다음 실행 작업폴더로도 이어지므로 자동 생성을 끈다.
        // (py_compile 처럼 코드가 직접 만드는 .pyc 는 생길 수 있지만 결과 목록에서는 아래 안전망으로 제외한다)
        psi.EnvironmentVariables["PYTHONDONTWRITEBYTECODE"] = "1";
        psi.EnvironmentVariables["CLASSDOCK_SCRIPT"] = scriptPath;
        psi.EnvironmentVariables["CLASSDOCK_PROJECT_ROOT"] = tempRoot;
        psi.EnvironmentVariables["CLASSDOCK_PLOT_DIR"] = session.PlotDir;

        session.Process = new Process();
        session.Process.StartInfo = psi;
        session.Process.Start();
        lock (PySessionsLock) PySessions[session.Id] = session;

        Thread outReader = StartLimitedReader(session.Process.StandardOutput, session.Stdout);
        Thread errReader = StartLimitedReader(session.Process.StandardError, session.Stderr);
        Thread watcher = new Thread(delegate()
        {
            bool exited = false;
            bool memoryLimit = false;
            Stopwatch watch = Stopwatch.StartNew();
            while (!exited && watch.ElapsedMilliseconds < 30 * 60 * 1000)
            {
                try { exited = session.Process.WaitForExit(500); } catch { break; }
                if (!exited && ProcessTreeWorkingSetBytes(session.Process.Id) > PythonProcessMemoryLimitBytes)
                {
                    memoryLimit = true;
                    break;
                }
            }
            if (!exited)
            {
                session.Stderr.AppendLine(memoryLimit
                    ? "\n[메모리 제한: 대화형 실행이 4GB를 넘어 종료했습니다.]"
                    : "\n[시간 초과: 대화형 실행을 30분 후 종료했습니다.]");
                KillProcessTree(session.Process);
                try { session.Process.WaitForExit(2000); } catch { }
            }
            try { outReader.Join(2000); errReader.Join(2000); } catch { }
            try { session.ExitCode = session.Process.ExitCode; } catch { session.ExitCode = -1; }
            string outputsJson = ScanOutputs(session);
            session.ImagesJson = ReadPlotImagesJson(session.PlotDir);
            session.VariablesJson = ReadPythonVariablesJson(session.PlotDir);
            lock (session.Sync) { session.OutputsJson = outputsJson; session.DoneAt = DateTime.UtcNow; session.Complete = true; }
            CleanupRunnerAndPlots(session);   // 러너·그림만 정리하고 작업폴더(TempRoot)는 출력 다운로드용으로 보존
            SweepRetainedSessions();
        });
        watcher.IsBackground = true;
        watcher.Start();
        return session.Id;
    }

    static Thread StartLimitedReader(StreamReader reader, LimitedTextBuffer target)
    {
        Thread thread = new Thread(delegate()
        {
            char[] buffer = new char[256];
            try
            {
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    target.Append(buffer, 0, read);
            }
            catch { }
        });
        thread.IsBackground = true;
        thread.Start();
        return thread;
    }

    static string PollPythonSession(string id, string knownOut, string knownErr)
    {
        PythonSession session;
        lock (PySessionsLock) if (!PySessions.TryGetValue(id ?? "", out session))
            return "{\"complete\":true,\"code\":-1,\"stdout\":\"\",\"stderr\":\"세션을 찾지 못했습니다.\",\"images\":[]}";
        lock (session.Sync)
        {
            // 증분 폴링: 클라이언트가 이미 받은 출력 길이(so/se)를 보내면, 그대로면 본문 없이 짧게,
            // 자랐으면 그 이후 "새 내용만" 보낸다. 누적 출력(최대 1MB+)을 매 폴마다 만들고 브라우저가
            // 파싱하면 출력이 커질수록 폴 한 사이클이 느려져 정지 반응까지 함께 밀린다.
            // 버퍼는 덧붙이기 전용이라 길이 오프셋이 그대로 이어붙이기 지점이 된다.
            int so = 0, se = 0;
            bool known = int.TryParse(knownOut ?? "", out so) && int.TryParse(knownErr ?? "", out se)
                && so >= 0 && se >= 0
                && so <= session.Stdout.TextLength && se <= session.Stderr.TextLength;
            if (known && !session.Complete
                && so == session.Stdout.TextLength && se == session.Stderr.TextLength)
                return "{\"complete\":false,\"unchanged\":true}";
            if (known)
                return "{\"complete\":" + (session.Complete ? "true" : "false")
                     + ",\"code\":" + session.ExitCode
                     + ",\"stdoutDelta\":" + JsonString(session.Stdout.GetTextFrom(so))
                     + ",\"stderrDelta\":" + JsonString(session.Stderr.GetTextFrom(se))
                     + ",\"echoes\":" + BuildEchoesJson(session.Echoes)
                     + ",\"images\":" + session.ImagesJson
                     + ",\"variables\":" + session.VariablesJson
                     + ",\"outputs\":" + session.OutputsJson + "}";
            return "{\"complete\":" + (session.Complete ? "true" : "false")
                 + ",\"code\":" + session.ExitCode
                 + ",\"stdout\":" + JsonString(session.Stdout.GetText())
                 + ",\"stderr\":" + JsonString(session.Stderr.GetText())
                 + ",\"echoes\":" + BuildEchoesJson(session.Echoes)
                 + ",\"images\":" + session.ImagesJson
                 + ",\"variables\":" + session.VariablesJson
                 + ",\"outputs\":" + session.OutputsJson + "}";
        }
    }

    // 입력 에코 구간 목록 → JSON [[시작,길이],...] (세션 Sync 잠금 안에서 호출)
    static string BuildEchoesJson(List<int[]> echoes)
    {
        StringBuilder sb = new StringBuilder("[");
        for (int i = 0; i < echoes.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[').Append(echoes[i][0]).Append(',').Append(echoes[i][1]).Append(']');
        }
        return sb.Append(']').ToString();
    }

    static void SendPythonSessionInput(string id, string input)
    {
        PythonSession session;
        lock (PySessionsLock) if (!PySessions.TryGetValue(id ?? "", out session)) throw new Exception("session-not-found");
        lock (session.Sync)
        {
            if (session.Complete) throw new Exception("session-complete");
            byte[] bytes = Encoding.UTF8.GetBytes((input ?? "") + "\n");
            // 파이프 stdin은 에코되지 않으므로 터미널처럼 표시한다. 반드시 stdin 에 쓰기 "전에" 에코를 버퍼에 넣는다.
            // 먼저 쓰면(flush) 파이썬이 즉시 다음 input() 프롬프트를 출력해, 리더 스레드가 그 프롬프트를
            // 에코보다 먼저 버퍼에 담아 "이름 입력 : 나이 입력 : 송화성"처럼 순서가 뒤섞인다.
            int echoStart = session.Stdout.TextLength;
            session.Stdout.AppendLine(input ?? "");
            int echoLen = (input ?? "").Length;
            // 에코가 기대 위치에 정확히 들어갔을 때만 구간을 기록한다. 4MB 상한 절단이나 리더 스레드의
            // 동시 append 로 오프셋이 어긋난 경우엔 기록을 생략 — 그 입력만 색 없이 표시될 뿐 안전하다.
            if (echoLen > 0 && session.Stdout.TextLength == echoStart + echoLen + Environment.NewLine.Length)
                session.Echoes.Add(new int[] { echoStart, echoLen });
            session.Process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
            session.Process.StandardInput.BaseStream.Flush();
        }
    }

    static void StopPythonSession(string id)
    {
        PythonSession session = null;
        lock (PySessionsLock) PySessions.TryGetValue(id ?? "", out session);
        if (session == null) return;
        KillProcessTree(session.Process);
        // 완료 세션은 맵·작업폴더를 유지해 출력 파일을 다운로드할 수 있게 둔다(보존 정리는 SweepRetainedSessions 가 담당).
        // 프로세스를 죽이면 watcher 가 곧 출력 수집·완료 처리한다.
    }

    // 실행 전 작업폴더 파일(입력)을 기록 — 완료 후 새로 생기거나 바뀐 파일만 "출력"으로 잡기 위함
    static void SnapshotInputs(PythonSession session)
    {
        try
        {
            string root = session.TempRoot;
            foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                FileInfo fi = new FileInfo(f);
                session.InitSize[rel] = fi.Length;
                session.InitMtime[rel] = fi.LastWriteTimeUtc.Ticks;
            }
        }
        catch { }
    }

    // 파이썬 바이트코드 찌꺼기(=학생이 만든 결과물이 아님) 판별. rel 은 '/' 로 정규화된 상대경로.
    // 실행 프로세스에 PYTHONDONTWRITEBYTECODE 를 걸어 애초에 안 생기게 했지만,
    // 그 설정 전에 만들어져 작업폴더로 이어진 것까지 걸러내는 안전망이다(셀 노트북 커널도 __pycache__ 를 제외한다).
    static bool IsBytecodeArtifact(string rel)
    {
        if (string.IsNullOrEmpty(rel)) return false;
        if (rel.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.EndsWith(".pyo", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.StartsWith("__pycache__/", StringComparison.OrdinalIgnoreCase)) return true;
        return rel.IndexOf("/__pycache__/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // 작업폴더에서 새로 생기거나 내용이 바뀐 파일을 [{name,size}] JSON 으로
    static string ScanOutputs(PythonSession session)
    {
        try
        {
            string root = session.TempRoot;
            if (!Directory.Exists(root)) return "[]";
            string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            List<string> items = new List<string>();
            foreach (string f in files)
            {
                string rel = f.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                if (IsBytecodeArtifact(rel)) continue;   // 파이썬이 자동으로 만든 __pycache__/*.pyc 는 출력이 아니다
                FileInfo fi;
                try { fi = new FileInfo(f); } catch { continue; }
                long size = fi.Length, mtime = fi.LastWriteTimeUtc.Ticks, initSize, initMtime;
                bool known = session.InitSize.TryGetValue(rel, out initSize);
                session.InitMtime.TryGetValue(rel, out initMtime);
                if (known && size == initSize && mtime == initMtime) continue;   // 변경 없음 = 입력 파일
                items.Add("{\"name\":" + JsonString(rel) + ",\"size\":" + size + "}");
                if (items.Count >= 200) break;
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }
        catch { return "[]"; }
    }

    static void CleanupRunnerAndPlots(PythonSession session)
    {
        try { if (File.Exists(session.RunnerPath)) File.Delete(session.RunnerPath); } catch { }
        try { if (Directory.Exists(session.PlotDir)) Directory.Delete(session.PlotDir, true); } catch { }
    }

    // 보존된(완료) 세션을 정리: 30분 경과분 + 최근 6개 초과분의 작업폴더 삭제·맵에서 제거
    static void SweepRetainedSessions()
    {
        List<PythonSession> toDelete = new List<PythonSession>();
        lock (PySessionsLock)
        {
            List<PythonSession> done = new List<PythonSession>();
            foreach (KeyValuePair<string, PythonSession> kv in PySessions) if (kv.Value.Complete) done.Add(kv.Value);
            DateTime now = DateTime.UtcNow;
            foreach (PythonSession s in done) if ((now - s.DoneAt).TotalMinutes > 30) toDelete.Add(s);
            done.Sort(delegate(PythonSession a, PythonSession b) { return a.DoneAt.CompareTo(b.DoneAt); });
            for (int i = 0; i < done.Count - 6; i++) if (!toDelete.Contains(done[i])) toDelete.Add(done[i]);
            foreach (PythonSession s in toDelete) PySessions.Remove(s.Id);
        }
        foreach (PythonSession s in toDelete) CleanupPythonSessionFiles(s);
    }

    // 보존 세션의 출력 파일 1개를 읽어온다(경로는 작업폴더 안으로 제한 — zip-slip 방지)
    static bool TryGetSessionFile(string id, string name, out byte[] data, out string fileName)
    {
        data = null; fileName = null;
        PythonSession session;
        lock (PySessionsLock) if (!PySessions.TryGetValue(id ?? "", out session)) return false;
        string safe = SafeRelPath(name);
        if (safe == null) return false;
        string full = Path.Combine(session.TempRoot, safe);
        if (!File.Exists(full)) return false;
        try { data = File.ReadAllBytes(full); } catch { return false; }
        fileName = Path.GetFileName(full);
        return true;
    }

    static string ReadPlotImagesJson(string plotDir)
    {
        try
        {
            string[] files = Directory.GetFiles(plotDir, "*.png");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            StringBuilder images = new StringBuilder("[");
            int count = Math.Min(files.Length, 8);
            for (int i = 0; i < count; i++)
            {
                byte[] bytes = File.ReadAllBytes(files[i]);
                if (bytes.Length > 8 * 1024 * 1024) continue;
                if (images.Length > 1) images.Append(',');
                images.Append(JsonString("data:image/png;base64," + Convert.ToBase64String(bytes)));
            }
            return images.Append(']').ToString();
        }
        catch { return "[]"; }
    }

    static string ReadPythonVariablesJson(string plotDir)
    {
        try
        {
            string path = Path.Combine(plotDir, "variables.json");
            if (!File.Exists(path)) return "[]";
            FileInfo info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > 256 * 1024) return "[]";
            string json = File.ReadAllText(path, Encoding.UTF8).Trim();
            return json.StartsWith("[", StringComparison.Ordinal) && json.EndsWith("]", StringComparison.Ordinal) ? json : "[]";
        }
        catch { return "[]"; }
    }

    static void CleanupPythonSessionFiles(PythonSession session)
    {
        try { if (File.Exists(session.RunnerPath)) File.Delete(session.RunnerPath); } catch { }
        try { if (Directory.Exists(session.PlotDir)) Directory.Delete(session.PlotDir, true); } catch { }
        try { if (Directory.Exists(session.TempRoot)) Directory.Delete(session.TempRoot, true); } catch { }
    }

    /* ===== %TEMP% 임시 작업폴더 청소 =====
       파이썬 세션·노트북 커널·터미널은 각자 종료 경로에서 임시물을 지우지만, 브라우저 탭을 닫으면
       하트비트 감시가 곧바로 프로세스를 끝내고(강제 종료·크래시도 마찬가지) 그때 살아 있던 임시물이 고아로 남는다.
       고아 폴더는 접근 통로인 세션 ID 가 메모리에만 있어 재실행 후에는 어떤 기능으로도 쓸 수 없으므로 지워도 잃을 게 없다.
       (설정·자동복원 데이터는 %LOCALAPPDATA%\ClassDock 와 브라우저 IndexedDB 에 있어 이 청소와 무관하다.)

       두 겹으로 막는다.
        1) 종료 직전 CleanupOwnTempEntries — 이번 실행이 만든 것을 그 자리에서 정리.
        2) 다음 기동 때 SweepOrphanTempEntries — 1)까지 못 간 강제 종료·크래시분을 뒤늦게 정리. */
    const int OrphanTempMinAgeHours = 24;
    static readonly string[] OrphanTempPrefixes = new string[] { "moidapy_", "moida_", "classdock_terminal_" };
    // 프로세스 안에서 필요할 때 재사용하는 작은 Python 도우미. 시작 청소와 첫 요청이 겹쳐
    // 실행 직전에 삭제되지 않도록 항상 보존한다(파일명은 고정이라 누적되지 않는다).
    static readonly string[] PersistentTempHelperNames = new string[] {
        "moida_sqlite_preview.py",
        "moida_sqlite_exec.py",
        "moida_python_import_index.py",
        "moida_jedi_complete.py"
    };

    // 지금 이 프로세스가 쓰는 경로는 나이와 무관하게 제외하고, 그 밖에는 24시간 지난 것만 지운다.
    // 별도 실행 인스턴스는 OS 뮤텍스로 막고, 시간 조건은 직전 실행이 막 끝낸 최근 임시물을 지켜 준다.
    static void SweepOrphanTempEntries()
    {
        string temp;
        try { temp = Path.GetTempPath(); } catch { return; }

        Dictionary<string, bool> inUse = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in CurrentTempPaths()) if (path.Length > 0) inUse[path] = true;

        DateTime cutoff = DateTime.UtcNow.AddHours(-OrphanTempMinAgeHours);
        string[] dirs;
        string[] files;
        try { dirs = Directory.GetDirectories(temp); } catch { dirs = new string[0]; }
        try { files = Directory.GetFiles(temp); } catch { files = new string[0]; }

        foreach (string dir in dirs)
        {
            if (!IsOrphanTempCandidate(dir, inUse)) continue;
            try
            {
                DirectoryInfo info = new DirectoryInfo(dir);
                if (info.CreationTimeUtc > cutoff || info.LastWriteTimeUtc > cutoff) continue;
                Directory.Delete(dir, true);
            }
            catch { }   // 다른 프로세스가 쓰는 중이면 잠겨서 실패 — 다음 기동에서 다시 시도한다
        }
        foreach (string file in files)
        {
            if (!IsOrphanTempCandidate(file, inUse)) continue;
            try
            {
                FileInfo info = new FileInfo(file);
                if (info.CreationTimeUtc > cutoff || info.LastWriteTimeUtc > cutoff) continue;
                File.Delete(file);
            }
            catch { }
        }
    }

    static bool IsOrphanTempCandidate(string path, Dictionary<string, bool> inUse)
    {
        string name = Path.GetFileName(path);
        bool ours = false;
        foreach (string prefix in OrphanTempPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { ours = true; break; }
        if (!ours) return false;
        return !inUse.ContainsKey(NormalizeTempPath(path));
    }

    static string NormalizeTempPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); } catch { return path; }
    }

    // 이 프로세스가 붙들고 있는 임시 경로(살아 있거나 보존 중인 세션·커널·터미널)
    static List<string> CurrentTempPaths()
    {
        List<string> paths = new List<string>();
        lock (PySessionsLock)
            foreach (PythonSession session in PySessions.Values)
            {
                paths.Add(NormalizeTempPath(session.TempRoot));
                paths.Add(NormalizeTempPath(session.RunnerPath));
                paths.Add(NormalizeTempPath(session.PlotDir));
            }
        lock (PyKernelsLock)
            foreach (PythonKernel kernel in PyKernels.Values)
            {
                paths.Add(NormalizeTempPath(kernel.TempRoot));
                paths.Add(NormalizeTempPath(kernel.RunnerPath));
            }
        lock (TerminalSessionsLock)
            foreach (TerminalSession session in TerminalSessions.Values)
                paths.Add(NormalizeTempPath(session.ScriptPath));
        try
        {
            string temp = Path.GetTempPath();
            foreach (string name in PersistentTempHelperNames)
                paths.Add(NormalizeTempPath(Path.Combine(temp, name)));
        }
        catch { }
        return paths;
    }

    // 종료 직전 정리. 아직 돌고 있는 것만 프로세스를 먼저 정리하고(끝난 것에 taskkill 을 걸어
    // 종료를 몇 초씩 늦추지 않는다), 임시 파일은 모두 지운다.
    static void CleanupOwnTempEntries()
    {
        try
        {
            List<PythonSession> sessions = new List<PythonSession>();
            lock (PySessionsLock)
            {
                foreach (PythonSession session in PySessions.Values) sessions.Add(session);
                PySessions.Clear();
            }
            foreach (PythonSession session in sessions)
            {
                if (!session.Complete) KillProcessTree(session.Process);
                CleanupPythonSessionFiles(session);
            }

            List<JavaSession> javaSessions = new List<JavaSession>();
            lock (JavaSessionsLock)
            {
                foreach (JavaSession session in JavaSessions.Values) javaSessions.Add(session);
                JavaSessions.Clear();
            }
            foreach (JavaSession session in javaSessions)
            {
                if (!session.Complete) KillProcessTree(session.Process);
                CleanupJavaSessionFiles(session);
            }

            List<string> kernelIds = new List<string>();
            lock (PyKernelsLock) foreach (string id in PyKernels.Keys) kernelIds.Add(id);
            foreach (string id in kernelIds) StopPythonKernel(id);   // 프로세스 종료 + 작업폴더 삭제

            List<string> dbIds = new List<string>();
            lock (DbSessionsLock) foreach (string id in DbSessions.Keys) dbIds.Add(id);
            foreach (string id in dbIds) StopDbSession(id);          // 서버에 유령 커넥션을 남기지 않는다

            List<TerminalSession> terminals = new List<TerminalSession>();
            lock (TerminalSessionsLock)
            {
                foreach (TerminalSession session in TerminalSessions.Values) terminals.Add(session);
                TerminalSessions.Clear();
            }
            foreach (TerminalSession session in terminals)
            {
                if (!session.ShellExited) KillProcessTree(session.Process);
                try { if (File.Exists(session.ScriptPath)) File.Delete(session.ScriptPath); } catch { }
            }

            ClassDockSshTerminal.ShutdownAll();

            List<NpmJob> npmJobs = new List<NpmJob>();
            lock (NpmJobsLock)
            {
                foreach (NpmJob job in NpmJobs.Values) npmJobs.Add(job);
                NpmJobs.Clear();
            }
            foreach (NpmJob job in npmJobs)
                if (!job.Complete) KillProcessTree(job.Process);

            ClearPythonProjectMirror();   // 자동완성용 작업공간 미러
        }
        catch { }   // 정리는 최선 노력 — 실패해도 종료는 진행하고 다음 기동의 청소가 마저 치운다
    }

    // ===== 파이썬(.py) 실행 — 설치된 인터프리터를 찾아 임시 파일로 실행 =====
    static string _pythonCmd = null;     // 캐시: "py" / "python" / "python3"
    static bool _pythonProbed = false;
    static readonly object PyProbeLock = new object();

    /* ===== ffmpeg 영상 변환 (브라우저 미지원 코덱 → MP4) =====
       exe 를 크게 만들지 않으려고 ffmpeg 는 동봉하지 않는다 — exe 옆의 ffmpeg.exe,
       ffmpeg\bin\ffmpeg.exe, 또는 PATH 의 ffmpeg 순서로 찾아 있을 때만 변환을 제공한다.
       (PowerPoint 로 PPTX→PDF 변환하는 것과 같은 '있으면 활용' 방식) */
    static string FindFfmpeg()
    {
        lock (FfmpegProbeLock)
        {
            if (_ffmpegCmd != null) return _ffmpegCmd;   // 성공만 캐시 — 나중에 ffmpeg 를 놓아도 재시작 없이 인식
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] cands = {
                Path.Combine(baseDir, "ffmpeg.exe"),
                Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe"),
                "ffmpeg"
            };
            foreach (string c in cands)
            {
                if (c.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(c)) continue;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(c, "-version");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    Process p = Process.Start(psi);
                    if (p == null) continue;
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } continue; }
                    if (p.ExitCode == 0) { _ffmpegCmd = c; break; }
                }
                catch { /* 해당 후보 없음 → 다음 */ }
            }
            return _ffmpegCmd;
        }
    }

    // ffmpeg 원클릭 설치 작업(백그라운드 스레드): 공식 배포 zip(약 90MB) 다운로드 →
    // 압축에서 bin/ffmpeg.exe 하나만 꺼내 exe 옆에 배치 → 임시파일 정리.
    // 사용자는 버튼만 누르면 되고, 진행률은 /ffmpeg-install-status 폴링으로 보여준다.
    static void InstallFfmpegWorker()
    {
        string tmpZip = Path.Combine(Path.GetTempPath(), "moida_ffmpeg_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            // 항상 최신 안정판을 가리키는 고정 주소(gyan.dev 공식 Windows 빌드)
            string url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }   // TLS 1.2
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 30000;
            req.ReadWriteTimeout = 120000;
            using (WebResponse resp = req.GetResponse())
            using (Stream rs = resp.GetResponseStream())
            using (FileStream fs = new FileStream(tmpZip, FileMode.Create, FileAccess.Write))
            {
                Interlocked.Exchange(ref _ffInstallTotal, resp.ContentLength);
                byte[] buf = new byte[81920];
                int n;
                while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                {
                    fs.Write(buf, 0, n);
                    Interlocked.Add(ref _ffInstallReceived, n);
                }
            }

            _ffInstallState = "extracting";
            string dest = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            string partPath = dest + ".part";
            using (FileStream zipStream = File.OpenRead(tmpZip))
            using (ZipArchive archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                ZipArchiveEntry hit = null;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (name.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) { hit = entry; break; }
                }
                if (hit == null) throw new Exception("zip-no-ffmpeg");
                using (Stream es = hit.Open())
                using (FileStream os = new FileStream(partPath, FileMode.Create, FileAccess.Write))
                {
                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = es.Read(buf, 0, buf.Length)) > 0) os.Write(buf, 0, n);
                }
            }
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
            File.Move(partPath, dest);

            if (FindFfmpeg() == null) throw new Exception("installed-but-not-detected");
            _ffInstallState = "done";
        }
        catch (Exception ex)
        {
            _ffInstallError = FlattenMessage(ex);
            _ffInstallState = "error";
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
        }
    }

    static bool RunFfmpeg(string cmd, string args, int timeoutMs)
    {
        string ignored;
        return ReadFfmpegInfo(cmd, args, timeoutMs, out ignored);
    }

    // 두 파이프를 동시에 비워야 타임아웃과 GPU 초기화 실패가 실제로 처리된다.
    static bool ReadFfmpegInfo(string cmd, string args, int timeoutMs, out string info)
    {
        info = "";
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(cmd, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                if (p == null) return false;
                StringBuilder errors = new StringBuilder();
                Thread stdout = new Thread(delegate() { try { while (p.StandardOutput.ReadLine() != null) { } } catch { } });
                Thread stderr = new Thread(delegate() {
                    try { string line; while ((line = p.StandardError.ReadLine()) != null)
                        lock (errors) { if (errors.Length < 131072) errors.AppendLine(line); }
                    } catch { }
                });
                stdout.IsBackground = stderr.IsBackground = true;
                stdout.Start(); stderr.Start();
                bool exited = p.WaitForExit(timeoutMs);
                if (!exited) { try { p.Kill(); } catch { } }
                stdout.Join(1000); stderr.Join(1000);
                lock (errors) { info = errors.ToString(); }
                return exited && p.ExitCode == 0;
            }
        }
        catch { return false; }
    }

    static byte[] ConvertMediaToMp4(byte[] media, bool forceVideo = false)
    {
        string ffmpeg = FindFfmpeg();
        if (ffmpeg == null) throw new FfmpegMissingException();
        string tmpDir = Path.Combine(Path.GetTempPath(), "moida_av_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        string inPath = Path.Combine(tmpDir, "in.bin");
        string outPath = Path.Combine(tmpDir, "out.mp4");
        try
        {
            File.WriteAllBytes(inPath, media);
            bool ok = ConvertMediaFile(ffmpeg, inPath, outPath, null, forceVideo);
            if (!ok) throw new Exception("ffmpeg-failed");
            return File.ReadAllBytes(outPath);
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
        }
    }

    /* ===== 경로 방식 영상 변환 =====
     * /convert-media 는 원본을 HTTP 본문으로 통째로 받아 byte[] 로 들고 있다가 임시 파일에 쓴다.
     * 그 방식은 .NET 배열 상한(2GB)에 걸리고, 걸리기 전에도 원본 크기만큼 메모리를 먹는다.
     * 여기서는 앱이 파일 경로(원본 폴더 ID + 상대 경로)만 넘기고 ffmpeg 가 디스크에서 직접
     * 읽고 쓴다 — 크기 제한이 사라지고, 진행률도 ffmpeg 가 알려 주는 실제 값으로 보여 준다. */

    static string ResolveSourceFolderFilePath(string id, string relativePath)
    {
        string root, full;
        if (!TryResolveSourceFolderPath(id, relativePath, false, out root, out full))
            throw new UnauthorizedAccessException("bad-source-folder-path");
        if (!File.Exists(full)) throw new FileNotFoundException("source-file-not-found");
        return full;
    }

    static string MediaContentType(string path)
    {
        switch (Path.GetExtension(path ?? "").ToLowerInvariant())
        {
            case ".mp4": case ".m4v": return "video/mp4";
            case ".webm": return "video/webm";
            case ".ogv": return "video/ogg";
            case ".mov": return "video/quicktime";
            case ".mkv": return "video/x-matroska";
            case ".avi": return "video/x-msvideo";
            case ".wmv": return "video/x-ms-wmv";
            case ".flv": return "video/x-flv";
            case ".mp3": return "audio/mpeg";
            case ".m4a": case ".aac": return "audio/mp4";
            case ".wav": return "audio/wav";
            case ".ogg": case ".oga": return "audio/ogg";
            case ".flac": return "audio/flac";
            case ".weba": return "audio/webm";
            default: return "application/octet-stream";
        }
    }

    // <video src> 로 흘려보낼 파일 하나에만 쓰는 표를 발급한다. 미디어 요소의 요청에는
    // X-ClassDock-Token 헤더를 붙일 수 없어, 표 자체가 그 한 파일의 열쇠 역할을 한다.
    static string CreateMediaTicket(string id, string relativePath)
    {
        ResolveSourceFolderFilePath(id, relativePath);   // 표를 주기 전에 실제로 열리는 파일인지 확인
        string ticket = CreateLocalAuthToken();
        DateTime now = DateTime.UtcNow;
        lock (MediaTicketLock)
        {
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, MediaTicket> item in MediaTickets)
                if (item.Value.ExpiresUtc <= now) stale.Add(item.Key);
            foreach (string key in stale) MediaTickets.Remove(key);
            while (MediaTickets.Count >= MediaTicketMax)
            {
                string oldest = null;
                foreach (KeyValuePair<string, MediaTicket> item in MediaTickets)
                    if (oldest == null || item.Value.ExpiresUtc < MediaTickets[oldest].ExpiresUtc) oldest = item.Key;
                if (oldest == null) break;
                MediaTickets.Remove(oldest);
            }
            MediaTicket entry = new MediaTicket();
            entry.RootId = id;
            entry.RelPath = relativePath;
            entry.ExpiresUtc = now + MediaTicketLifetime;
            MediaTickets[ticket] = entry;
        }
        return ticket;
    }

    static string ResolveMediaTicketPath(string ticket)
    {
        MediaTicket found;
        lock (MediaTicketLock)
        {
            if (string.IsNullOrEmpty(ticket) || !MediaTickets.TryGetValue(ticket, out found)) return null;
            if (found.ExpiresUtc <= DateTime.UtcNow) { MediaTickets.Remove(ticket); return null; }
        }
        try { return ResolveSourceFolderFilePath(found.RootId, found.RelPath); }
        catch { return null; }
    }

    // "00:12:34.56" 또는 "00:12:34.56, start: 0.000000" 의 앞부분을 마이크로초로.
    static long ParseTimecodeUs(string text)
    {
        string value = (text ?? "").Trim();
        int cut = value.IndexOfAny(new char[] { ',', ' ' });
        if (cut > 0) value = value.Substring(0, cut);
        string[] parts = value.Split(':');
        if (parts.Length != 3) return 0;
        int hours, minutes;
        double seconds;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours)) return 0;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)) return 0;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) return 0;
        double total = hours * 3600.0 + minutes * 60.0 + seconds;
        return total > 0 ? (long)(total * 1000000.0) : 0;
    }

    internal sealed class MediaInputInfo
    {
        public long DurationUs;
        public bool CopyVideo;
        public bool CopyAudio;
    }

    // ffprobe를 별도로 설치하지 않아도 입력의 첫 영상·소리 정보를 얻는다.
    // MP4에 담을 수 있는 HEVC/MPEG-4나 H.264 10-bit/4:4:4도 브라우저 호환을 위해 변환한다.
    internal static MediaInputInfo ParseMediaInputInfo(string text)
    {
        MediaInputInfo info = new MediaInputInfo();
        bool videoSeen = false, audioSeen = false;
        foreach (string line in (text ?? "").Split('\n'))
        {
            string value = line.Trim();
            if (value.StartsWith("Duration:", StringComparison.Ordinal))
                info.DurationUs = ParseTimecodeUs(value.Substring(9));
            if (!value.StartsWith("Stream #0:", StringComparison.Ordinal)) continue;
            var video = System.Text.RegularExpressions.Regex.Match(value, @"^Stream #0:\d+(?:\[[^\]]*\])?(?:\([^)]*\))?: Video: (.*)$");
            var audio = System.Text.RegularExpressions.Regex.Match(value, @"^Stream #0:\d+(?:\[[^\]]*\])?(?:\([^)]*\))?: Audio: (.*)$");
            if (!videoSeen && video.Success)
            {
                videoSeen = true;
                string format = video.Groups[1].Value;
                info.CopyVideo = System.Text.RegularExpressions.Regex.IsMatch(format,
                    @"^h264 \((?:Constrained Baseline|Baseline|Main|High)\)(?:,| )")
                    && System.Text.RegularExpressions.Regex.IsMatch(format, @", yuv420p(?:\(|,| )")
                    && format.IndexOf("attached pic", StringComparison.Ordinal) < 0;
            }
            if (!audioSeen && audio.Success)
            {
                audioSeen = true;
                info.CopyAudio = audio.Groups[1].Value.StartsWith("aac (LC)", StringComparison.Ordinal);
            }
        }
        return info;
    }

    static MediaInputInfo ProbeMediaInput(string ffmpeg, string inPath)
    {
        string info;
        // 출력이 없어서 exit code는 1이다. 입력 정보만 파싱하고 미확인 코덱은 복사하지 않는다.
        ReadFfmpegInfo(ffmpeg, "-hide_banner -nostdin -i \"" + inPath + "\"", 30000, out info);
        return ParseMediaInputInfo(info);
    }

    internal sealed class MediaConvertAttempt
    {
        public string Encoder;
        public bool CopyAudio;
        public string Stage {
            get { return Encoder == "copy" ? (CopyAudio ? "remux" : "copy")
                : Encoder == "libx264" ? "encode" : "hardware"; }
        }
    }

    internal static List<MediaConvertAttempt> MediaConvertPlan(MediaInputInfo info)
    {
        List<MediaConvertAttempt> plan = new List<MediaConvertAttempt>();
        string[] encoders = info.CopyVideo
            ? new string[] { "copy", "h264_nvenc", "h264_qsv", "h264_amf", "libx264" }
            : new string[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };
        foreach (string encoder in encoders)
        {
            plan.Add(new MediaConvertAttempt { Encoder = encoder, CopyAudio = info.CopyAudio });
            // AAC 복사가 실패해도 영상까지 불필요하게 재인코딩하지 않는다.
            if (info.CopyAudio) plan.Add(new MediaConvertAttempt { Encoder = encoder, CopyAudio = false });
        }
        return plan;
    }

    static string MediaVideoArgs(string encoder)
    {
        if (encoder == "copy") return " -c:v copy";
        string options;
        switch (encoder)
        {
            case "h264_nvenc": options = " -preset p4 -rc vbr -cq 23 -b:v 0"; break;
            case "h264_qsv": options = " -preset veryfast -global_quality 23"; break;
            case "h264_amf": options = " -usage transcoding -quality speed -rc cqp -qp_i 23 -qp_p 23 -qp_b 23"; break;
            default: options = " -preset veryfast -crf 23"; break;
        }
        return " -c:v " + encoder + options
            + " -vf \"scale=ceil(iw/2)*2:ceil(ih/2)*2\" -pix_fmt yuv420p";
    }

    // 임시 출력은 확장자가 .part 라 ffmpeg 가 컨테이너를 짐작할 수 없다 → -f mp4 로 못박는다.
    internal static string MediaConvertArgs(string inPath, string outPath, MediaConvertAttempt attempt)
    {
        string head = "-y -hide_banner -loglevel error -nostdin -progress pipe:1 -nostats"
            + " -i \"" + inPath + "\" -map 0:v:0? -map 0:a:0?";
        string audio = attempt.CopyAudio ? " -c:a copy" : " -c:a aac -b:a 192k";
        return head + MediaVideoArgs(attempt.Encoder) + audio + " -movflags +faststart -f mp4 \"" + outPath + "\"";
    }

    // MediaConvLock 아래에서만 접근. 인코더 목록에 있어도 드라이버가 안 맞을 수 있어 실제로 시험한다.
    static readonly Dictionary<string, bool> MediaHardwareSupport = new Dictionary<string, bool>();
    static bool CanEncodeMediaHardware(string ffmpeg, string encoder)
    {
        string key = ffmpeg + "|" + encoder;
        try { key += "|" + File.GetLastWriteTimeUtc(ffmpeg).Ticks; } catch { }
        bool supported;
        if (MediaHardwareSupport.TryGetValue(key, out supported)) return supported;
        supported = RunFfmpeg(ffmpeg, "-hide_banner -loglevel error -nostdin -f lavfi"
            + " -i color=c=black:s=128x128:r=1 -frames:v 1 -an" + MediaVideoArgs(encoder) + " -f null -", 10000);
        MediaHardwareSupport[key] = supported;
        return supported;
    }

    // 파일 경로 방식과 끌어다 놓기 방식이 같은 선택·재시도 정책을 사용한다.
    internal static bool ExecuteMediaPlan(MediaInputInfo info, Func<string, bool> supportsHardware,
        Func<MediaConvertAttempt, bool> run, Func<bool> cancelled)
    {
        foreach (MediaConvertAttempt attempt in MediaConvertPlan(info))
        {
            if (cancelled()) return false;
            if (attempt.Stage == "hardware" && !supportsHardware(attempt.Encoder)) continue;
            if (cancelled()) return false;
            if (run(attempt)) return !cancelled();
        }
        return false;
    }

    internal static bool ConvertMediaFile(string ffmpeg, string inPath, string outPath, MediaConvertJob job, bool forceVideo = false)
    {
        MediaInputInfo info = ProbeMediaInput(ffmpeg, inPath);
        if (forceVideo) info.CopyVideo = false; // 재생 실패 시 H.264라도 복사하지 않고 다시 인코딩
        if (job != null) Interlocked.Exchange(ref job.DurationUs, info.DurationUs);
        return ExecuteMediaPlan(info,
            delegate(string encoder) { return CanEncodeMediaHardware(ffmpeg, encoder); },
            delegate(MediaConvertAttempt attempt) {
                if (File.Exists(outPath)) File.Delete(outPath);
                if (job != null) { job.Stage = attempt.Stage; MediaJobStageReset(job); }
                string args = MediaConvertArgs(inPath, outPath, attempt);
                bool ok = job == null ? RunFfmpeg(ffmpeg, args, 1800000) : RunFfmpegTracked(ffmpeg, args, job);
                return ok && File.Exists(outPath) && new FileInfo(outPath).Length > 0;
            },
            delegate() { return job != null && job.Cancelled; });
    }

    // RunFfmpeg 과 달리 진행률을 읽으면서 기다리고, 중지 요청이 오면 프로세스를 끊는다.
    static bool RunFfmpegTracked(string cmd, string args, MediaConvertJob job)
    {
        Process p = null;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(cmd, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            p = Process.Start(psi);
            if (p == null) return false;
            lock (MediaJobLock) { job.Proc = p; }
            if (job.Cancelled) { try { p.Kill(); } catch { } }
            Process running = p;
            // stderr 을 비우지 않으면 파이프가 차서 ffmpeg 가 멈춘다(-loglevel error 라도 경고는 나온다).
            Thread drain = new Thread(delegate() { try { running.StandardError.ReadToEnd(); } catch { } });
            drain.IsBackground = true;
            drain.Start();
            string line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                long us = -1;
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
                {
                    long parsed;
                    if (long.TryParse(line.Substring(12).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out parsed)) us = parsed;
                }
                else if (line.StartsWith("out_time=", StringComparison.Ordinal))
                {
                    us = ParseTimecodeUs(line.Substring(9));
                }
                else if (line.StartsWith("speed=", StringComparison.Ordinal))
                {
                    // "speed=12.5x" · 아직 모를 때는 "speed=N/A" 로 온다.
                    double rate;
                    if (double.TryParse(line.Substring(6).Trim().TrimEnd('x', 'X'),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out rate) && rate > 0)
                        Interlocked.Exchange(ref job.SpeedMilli, (long)Math.Round(rate * 1000.0));
                }
                if (us >= 0) Interlocked.Exchange(ref job.DoneUs, us);
            }
            p.WaitForExit();
            try { drain.Join(3000); } catch { }
            return !job.Cancelled && p.ExitCode == 0;
        }
        catch { return false; }
        finally
        {
            lock (MediaJobLock) { job.Proc = null; }
            if (p != null) { try { p.Dispose(); } catch { } }
        }
    }

    // 단계가 바뀌면 진행률·배속·경과 시간을 함께 0 부터 다시 센다. 2차 재인코딩은 1차와
    // 속도가 전혀 달라, 앞 단계의 값을 물려받으면 남은 시간이 엉뚱하게 나온다.
    static void MediaJobStageReset(MediaConvertJob job)
    {
        Interlocked.Exchange(ref job.DoneUs, 0);
        Interlocked.Exchange(ref job.SpeedMilli, 0);
        Interlocked.Exchange(ref job.StageStartedTicks, DateTime.UtcNow.Ticks);
    }

    static void RunMediaConvertJob(object state)
    {
        MediaConvertJob job = (MediaConvertJob)state;
        string outTemp = job.OutPath + ".part";
        try
        {
            string ffmpeg = FindFfmpeg();
            if (ffmpeg == null) throw new FfmpegMissingException();
            // 변환은 CPU 를 다 쓰므로 예전 방식과 마찬가지로 한 번에 하나만 돌린다.
            lock (MediaConvLock)
            {
                if (job.Cancelled) { job.State = "cancelled"; return; }
                job.State = "running";
                bool ok = ConvertMediaFile(ffmpeg, job.InPath, outTemp, job, job.ForceVideoEncode);
                if (job.Cancelled) { job.State = "cancelled"; return; }
                if (!ok) throw new Exception("ffmpeg-failed");
                try { if (File.Exists(job.OutPath)) File.Delete(job.OutPath); } catch { }
                File.Move(outTemp, job.OutPath);
                Interlocked.Exchange(ref job.DoneUs, Interlocked.Read(ref job.DurationUs));
                job.State = "done";
            }
        }
        catch (FfmpegMissingException)
        {
            job.Error = "no-ffmpeg";
            job.State = "error";
        }
        catch (Exception ex)
        {
            job.Error = FlattenMessage(ex);
            job.State = "error";
        }
        finally
        {
            // 끝내 완성하지 못한 조각은 남기지 않는다 — 다음에 열 때 깨진 mp4 로 보이면 안 된다.
            if (job.State != "done") { try { if (File.Exists(outTemp)) File.Delete(outTemp); } catch { } }
        }
    }

    static string StartMediaConvertJob(string id, string inRel, string outRel, bool forceVideo = false)
    {
        if (FindFfmpeg() == null) throw new FfmpegMissingException();
        string root, inFull, outFull;
        if (!TryResolveSourceFolderPath(id, inRel, false, out root, out inFull) || !File.Exists(inFull))
            throw new FileNotFoundException("source-file-not-found");
        if (!TryResolveSourceFolderPath(id, outRel, false, out root, out outFull))
            throw new UnauthorizedAccessException("bad-source-folder-path");
        // 결과는 언제나 MP4 다. 확장자를 못박아 두면 실수로 원본 폴더의 다른 파일을 덮어쓰는 일도 막힌다.
        if (!Path.GetExtension(outFull).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("output-must-be-mp4");
        if (string.Equals(inFull, outFull, StringComparison.OrdinalIgnoreCase))
            throw new IOException("output-same-as-input");
        string outDir = Path.GetDirectoryName(outFull);
        if (string.IsNullOrEmpty(outDir) || !Directory.Exists(outDir))
            throw new DirectoryNotFoundException("output-folder-not-found");

        MediaConvertJob job = new MediaConvertJob();
        job.Id = Guid.NewGuid().ToString("N");
        job.InPath = inFull;
        job.OutPath = outFull;
        job.OutName = Path.GetFileName(outFull);
        job.ForceVideoEncode = forceVideo;
        job.State = "queued";
        job.Stage = "";
        job.Error = "";
        job.StartedUtc = DateTime.UtcNow;
        lock (MediaJobLock)
        {
            // 끝난 지 오래된 작업표는 치운다. 앱이 결과를 확인하기 전에 지워지지 않게 여유를 둔다.
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, MediaConvertJob> item in MediaJobs)
            {
                string finished = item.Value.State;
                if (finished != "queued" && finished != "running"
                    && (DateTime.UtcNow - item.Value.StartedUtc) > TimeSpan.FromHours(6)) stale.Add(item.Key);
            }
            foreach (string key in stale) MediaJobs.Remove(key);
            if (MediaJobs.Count >= MediaJobMax) throw new Exception("too-many-convert-jobs");
            MediaJobs[job.Id] = job;
        }
        Thread worker = new Thread(RunMediaConvertJob);
        worker.IsBackground = true;
        worker.Start(job);
        return job.Id;
    }

    static string MediaConvertJobJson(string jobId)
    {
        MediaConvertJob job;
        lock (MediaJobLock) { if (!MediaJobs.TryGetValue(jobId ?? "", out job)) return null; }
        long duration = Interlocked.Read(ref job.DurationUs);
        long done = Interlocked.Read(ref job.DoneUs);
        // 길이를 못 알아낸 원본에서는 -1 을 보내 화면이 퍼센트 대신 "변환 중"만 보이게 한다.
        int percent = duration > 0 ? (int)Math.Min(100, Math.Max(0, done * 100 / duration)) : -1;
        if (job.State == "done") percent = 100;
        // 남은 시간은 화면에서 계산한다. 여기서는 그 재료(원본 길이·처리한 길이·배속·경과)만 보낸다.
        long stageTicks = Interlocked.Read(ref job.StageStartedTicks);
        long elapsedMs = stageTicks > 0
            ? (long)(DateTime.UtcNow - new DateTime(stageTicks, DateTimeKind.Utc)).TotalMilliseconds : 0;
        return "{\"state\":" + JsonString(job.State)
            + ",\"stage\":" + JsonString(job.Stage ?? "")
            + ",\"percent\":" + percent
            + ",\"durationUs\":" + duration
            + ",\"doneUs\":" + done
            + ",\"speedMilli\":" + Interlocked.Read(ref job.SpeedMilli)
            + ",\"elapsedMs\":" + (elapsedMs > 0 ? elapsedMs : 0)
            + ",\"name\":" + JsonString(job.OutName ?? "")
            + ",\"error\":" + JsonString(job.Error ?? "") + "}";
    }

    static bool CancelMediaConvertJob(string jobId)
    {
        MediaConvertJob job;
        Process running;
        lock (MediaJobLock)
        {
            if (!MediaJobs.TryGetValue(jobId ?? "", out job)) return false;
            job.Cancelled = true;
            running = job.Proc;
        }
        if (running != null) { try { running.Kill(); } catch { } }
        return true;
    }

    static string FindPython()
    {
        lock (PyProbeLock)
        {
            if (_pythonProbed) return _pythonCmd;
            _pythonProbed = true;
            _pythonCmd = ProbePython();
            return _pythonCmd;
        }
    }

    // 파이썬을 새로 설치한 뒤 exe 재시작 없이 다시 찾도록 캐시를 비운다(Py Env 의 '다시 검사').
    static void ResetPythonProbe()
    {
        lock (PyProbeLock) { _pythonProbed = false; _pythonCmd = null; }
        lock (JediLock) { _jediReady = null; }
    }

    /* 설치할 때 'Add python.exe to PATH' 를 체크하지 않으면 PATH 로는 찾을 수 없다.
       그래서 PATH → 레지스트리(PEP 514) → 표준 설치 폴더 순으로 넓혀 가며 찾는다. */
    static string ProbePython()
    {
        // 1) PATH — Windows 런처 'py' 우선(버전 선택 처리), 그다음 python / python3
        string[] cands = { "py", "python", "python3" };
        foreach (string c in cands)
            if (IsUsablePython(c)) return c;
        // 2) PATH 밖 — 설치된 흔적에서 python.exe 를 찾아 최신 버전부터 검사
        foreach (string exe in InstalledPythonCandidates())
            if (IsUsablePython(exe)) return exe;
        return null;
    }

    // --version 이 정상 종료하고 Python 3 이라고 답할 때만 인정한다.
    // 파이썬을 설치하지 않아도 있는 Microsoft Store 안내용 가짜 python.exe 도 여기서 걸러진다.
    static bool IsUsablePython(string cmd)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(cmd, "--version");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process p = Process.Start(psi);
            if (p == null) return false;
            // ReadToEnd를 먼저 호출하면 응답하지 않는 PATH 후보에서 스트림 EOF를 영원히 기다려
            // 아래 5초 제한까지 도달하지 못한다. --version 출력은 매우 작으므로 종료를 먼저 기다린다.
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { }
                try { p.WaitForExit(1000); } catch { }
                return false;
            }
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (p.ExitCode != 0) return false;
            return (stdout + stderr).IndexOf("Python 3", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }   // 해당 후보 없음 → 다음
    }

    // 레지스트리와 표준 설치 폴더에서 python.exe 후보를 모아 최신 버전부터 돌려준다.
    static List<string> InstalledPythonCandidates()
    {
        var found = new List<KeyValuePair<int, string>>();   // (버전 순위, 경로)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        try { paths.AddRange(RegistryPythonPaths()); } catch { }
        try { paths.AddRange(WellKnownPythonPaths()); } catch { }
        foreach (string candidate in paths)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            string exe;
            try { exe = Path.GetFullPath(candidate); } catch { continue; }
            if (!File.Exists(exe) || !seen.Add(exe)) continue;
            found.Add(new KeyValuePair<int, string>(PythonVersionRank(exe), exe));
        }
        found.Sort(delegate(KeyValuePair<int, string> a, KeyValuePair<int, string> b) { return b.Key.CompareTo(a.Key); });
        var list = new List<string>();
        foreach (KeyValuePair<int, string> item in found) list.Add(item.Value);
        return list;
    }

    // 폴더 이름의 'Python313' / 'Python3.13' 에서 숫자를 뽑아 최신 우선 정렬에 쓴다(모르면 0 → 마지막).
    static int PythonVersionRank(string exePath)
    {
        try
        {
            string parent = Path.GetDirectoryName(exePath);
            string dir = parent == null ? "" : Path.GetFileName(parent);
            var digits = new StringBuilder();
            foreach (char ch in dir) if (ch >= '0' && ch <= '9') digits.Append(ch);
            if (digits.Length < 2) return 0;                       // anaconda3 처럼 버전이 없는 경우
            string text = digits.ToString();
            int major = text[0] - '0';
            int minor;
            if (!int.TryParse(text.Substring(1), out minor)) minor = 0;
            return major * 1000 + Math.Min(minor, 999);            // 3.13 → 3013
        }
        catch { return 0; }
    }

    // PEP 514: HKCU/HKLM 의 SOFTWARE\Python\<회사>\<태그>\InstallPath 에 설치 위치가 등록된다.
    static List<string> RegistryPythonPaths()
    {
        var list = new List<string>();
        var views = new KeyValuePair<RegistryHive, RegistryView>[] {
            new KeyValuePair<RegistryHive, RegistryView>(RegistryHive.CurrentUser, RegistryView.Registry64),
            new KeyValuePair<RegistryHive, RegistryView>(RegistryHive.LocalMachine, RegistryView.Registry64),
            new KeyValuePair<RegistryHive, RegistryView>(RegistryHive.LocalMachine, RegistryView.Registry32)
        };
        foreach (KeyValuePair<RegistryHive, RegistryView> view in views)
        {
            RegistryKey baseKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(view.Key, view.Value);
                if (baseKey == null) continue;
                using (RegistryKey root = baseKey.OpenSubKey("SOFTWARE\\Python"))
                {
                    if (root == null) continue;
                    foreach (string company in root.GetSubKeyNames())
                    {
                        using (RegistryKey companyKey = root.OpenSubKey(company))
                        {
                            if (companyKey == null) continue;
                            foreach (string tag in companyKey.GetSubKeyNames())
                            {
                                using (RegistryKey install = companyKey.OpenSubKey(tag + "\\InstallPath"))
                                {
                                    if (install == null) continue;
                                    string exe = install.GetValue("ExecutablePath") as string;
                                    if (string.IsNullOrEmpty(exe))
                                    {
                                        string dir = install.GetValue(null) as string;   // 기본값 = 설치 폴더
                                        if (!string.IsNullOrEmpty(dir)) exe = Path.Combine(dir, "python.exe");
                                    }
                                    if (!string.IsNullOrEmpty(exe)) list.Add(exe);
                                }
                            }
                        }
                    }
                }
            }
            catch { /* 권한 없음·키 없음 → 다음 뷰 */ }
            finally { if (baseKey != null) { try { baseKey.Close(); } catch { } } }
        }
        return list;
    }

    // 레지스트리에 없더라도 대부분 아래 기본 위치에 설치된다.
    static List<string> WellKnownPythonPaths()
    {
        var list = new List<string>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var roots = new List<string>();
        if (!string.IsNullOrEmpty(local)) roots.Add(Path.Combine(local, "Programs\\Python"));   // 기본 '나만 사용' 설치
        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        try { if (!string.IsNullOrEmpty(systemDir)) roots.Add(Path.GetPathRoot(systemDir)); } catch { }   // C:\Python313
        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.GetDirectories(root, "Python3*"))
                    list.Add(Path.Combine(dir, "python.exe"));
            }
            catch { /* 접근 불가 폴더 → 건너뜀 */ }
        }
        string[] condaNames = { "anaconda3", "miniconda3", "miniforge3" };
        foreach (string name in condaNames)
        {
            if (!string.IsNullOrEmpty(profile)) list.Add(Path.Combine(profile, name + "\\python.exe"));
            if (!string.IsNullOrEmpty(programData)) list.Add(Path.Combine(programData, name + "\\python.exe"));
        }
        return list;
    }

    /* ===== 자바(.java) 실행 — 설치된 JDK 찾기 =====
       JDK 11+ 는 `java Foo.java` 한 줄로 컴파일과 실행을 함께 처리한다(JEP 330). 그래서 javac 를 따로
       부르지 않지만, 그 기능은 JDK 에만 있다 — JRE 만 깔린 PC 를 걸러내려고 같은 bin 의 javac.exe 까지 확인한다.
       탐색은 '앱이 설치한 것' 을 가장 앞에 둔다. 학생 PC 에 남아 있는 낡은 자바(8 등)가 PATH 를 잡고 있어도
       앱이 받아 둔 JDK 로 실행되게 하기 위해서다. */
    static string _javaCmd = null;      // 캐시: java.exe 전체 경로(없으면 null)
    static string _javaSource = "";     // 어디서 찾았는지 — 진단에서 "왜 이 PC만 다른가"를 한 번에 알려 준다
    static bool _javaProbed = false;
    static readonly object JavaProbeLock = new object();
    const int JavaMinimumFeatureVersion = 11;   // 단일 파일 소스 실행이 들어온 버전
    // `java -version` 첫 줄: openjdk version "21.0.5" 2024-10-15 / java version "1.8.0_402"
    static readonly System.Text.RegularExpressions.Regex JavaVersionRe =
        new System.Text.RegularExpressions.Regex("version\\s+\"([0-9][0-9._]*)");
    // 따옴표 없이 적는 배포판 대비: openjdk 21 2023-09-19
    static readonly System.Text.RegularExpressions.Regex JavaBareVersionRe =
        new System.Text.RegularExpressions.Regex("\\b(?:openjdk|java)\\s+([0-9]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    // 폴더 이름에서 버전을 뽑아 최신 우선 정렬에 쓴다: jdk-21.0.5+11 / jdk1.8.0_402 / zulu17.50.19
    static readonly System.Text.RegularExpressions.Regex JavaDirVersionRe =
        new System.Text.RegularExpressions.Regex("(\\d+)(?:\\.(\\d+))?");

    static string FindJava()
    {
        lock (JavaProbeLock)
        {
            if (_javaProbed) return _javaCmd;
            _javaProbed = true;
            _javaCmd = ProbeJava(out _javaSource);
            return _javaCmd;
        }
    }

    // JDK 를 새로 설치한 뒤 exe 재시작 없이 다시 찾도록 캐시를 비운다(자동 설치 직후·'다시 검사').
    static void ResetJavaProbe()
    {
        lock (JavaProbeLock) { _javaProbed = false; _javaCmd = null; _javaSource = ""; }
    }

    static string JavaProbeSource()
    {
        lock (JavaProbeLock) { return _javaSource; }
    }

    static string ProbeJava(out string source)
    {
        foreach (KeyValuePair<string, string> candidate in JavaCandidates())
        {
            if (IsUsableJdk(candidate.Key)) { source = candidate.Value; return candidate.Key; }
        }
        source = "";
        return null;
    }

    // 후보를 '어디서 왔는지' 와 함께 우선순위 순서로 모은다. 실제 검증(IsUsableJdk)은 이 순서대로 한 번씩만 한다.
    static List<KeyValuePair<string, string>> JavaCandidates()
    {
        var list = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 1) 앱이 설치한 JDK — exe 옆이 먼저, 거기에 쓸 수 없었던 PC 는 LocalAppData
        foreach (string exe in AppJdkJavaPaths(JdkPortableRoot())) AddJavaCandidate(list, seen, exe, "app-portable");
        foreach (string exe in AppJdkJavaPaths(JdkLocalAppDataRoot())) AddJavaCandidate(list, seen, exe, "app-local");
        // 2) JAVA_HOME — 교실 PC 에 관리자가 지정해 둔 경우
        string javaHome = null;
        try { javaHome = Environment.GetEnvironmentVariable("JAVA_HOME"); } catch { }
        if (!string.IsNullOrEmpty(javaHome))
        {
            try { AddJavaCandidate(list, seen, Path.Combine(javaHome.Trim().Trim('"'), "bin\\java.exe"), "java-home"); }
            catch { }
        }
        // 3) PATH
        foreach (string exe in PathJavaPaths()) AddJavaCandidate(list, seen, exe, "path");
        // 4) 레지스트리와 표준 설치 폴더 — 여러 버전이 함께 나오므로 최신부터 검사한다
        var pool = new List<KeyValuePair<string, string>>();
        foreach (string exe in RegistryJavaPaths()) pool.Add(new KeyValuePair<string, string>(exe, "registry"));
        foreach (string exe in WellKnownJavaPaths()) pool.Add(new KeyValuePair<string, string>(exe, "well-known"));
        pool.Sort(delegate(KeyValuePair<string, string> a, KeyValuePair<string, string> b)
        {
            return JavaVersionRank(b.Key).CompareTo(JavaVersionRank(a.Key));
        });
        foreach (KeyValuePair<string, string> item in pool) AddJavaCandidate(list, seen, item.Key, item.Value);
        return list;
    }

    static void AddJavaCandidate(List<KeyValuePair<string, string>> list, HashSet<string> seen, string exe, string source)
    {
        if (string.IsNullOrEmpty(exe)) return;
        string full;
        try { full = Path.GetFullPath(exe); } catch { return; }   // 잘못된 문자가 섞인 PATH·레지스트리 값
        if (!seen.Add(full)) return;
        list.Add(new KeyValuePair<string, string>(full, source));
    }

    // 자동 설치가 JDK 를 푸는 곳. exe 옆이 1순위 — USB 에 담아 다니면 다른 PC 에서도 그대로 쓴다.
    static string JdkPortableRoot()
    {
        try { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jdk"); }
        catch { return null; }
    }

    // exe 가 Program Files 나 읽기 전용 USB 에 있어 옆에 쓸 수 없을 때의 대체 위치.
    static string JdkLocalAppDataRoot()
    {
        try
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassDock", "jdk");
        }
        catch { return null; }
    }

    /* 자동 설치가 실제로 쓸 위치를 정한다. exe 옆에 임시 파일을 만들어 보고 판단한다 —
       속성만 봐서는 Program Files 가상화나 읽기 전용 USB 를 걸러낼 수 없다. */
    static string JdkInstallRoot()
    {
        string portable = JdkPortableRoot();
        if (!string.IsNullOrEmpty(portable))
        {
            string parent = null;
            try { parent = Path.GetDirectoryName(portable); } catch { }
            if (CanWriteInto(parent)) return portable;
        }
        return JdkLocalAppDataRoot();
    }

    static bool CanWriteInto(string dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            string probe = Path.Combine(dir, ".classdock-write-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (FileStream fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write)) { fs.WriteByte(0); }
            try { File.Delete(probe); } catch { }
            return true;
        }
        catch { return false; }
    }

    /* 앱이 받아 둔 JDK 폴더에서 java.exe 를 찾는다. 배포 zip 은 안에 'jdk-21.0.5+11\' 한 겹을 더 갖고 있어
       루트 바로 아래와 한 단계 아래를 모두 본다(전개 방식이 바뀌거나 사용자가 직접 풀어 넣어도 계속 찾도록). */
    static List<string> AppJdkJavaPaths(string root)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(root)) return list;
        try
        {
            if (!Directory.Exists(root)) return list;
            list.Add(Path.Combine(root, "bin\\java.exe"));
            foreach (string dir in Directory.GetDirectories(root)) list.Add(Path.Combine(dir, "bin\\java.exe"));
        }
        catch { }   // 접근 불가 폴더 → 후보 없음
        return list;
    }

    /* PATH 를 직접 훑는다. 'java' 라는 이름만으로 실행하면 어느 폴더에서 왔는지 알 수 없어
       같은 bin 의 javac.exe(=JDK 인지) 를 확인할 수 없다. */
    static List<string> PathJavaPaths()
    {
        var list = new List<string>();
        string path = null;
        try { path = Environment.GetEnvironmentVariable("PATH"); } catch { }
        if (string.IsNullOrEmpty(path)) return list;
        foreach (string part in path.Split(';'))
        {
            string dir = part.Trim().Trim('"');
            if (dir.Length == 0) continue;
            try { list.Add(Path.Combine(dir, "java.exe")); }
            catch { }   // 잘못된 문자가 섞인 PATH 조각은 건너뜀
        }
        return list;
    }

    /* 레지스트리에 남는 JDK 설치 위치. 벤더마다 키 모양이 달라(JavaSoft 는 <버전>\JavaHome,
       Adoptium 은 <버전>\hotspot\MSI\Path) 각 루트 아래를 제한된 깊이로 훑으며 값을 모은다. */
    static List<string> RegistryJavaPaths()
    {
        var list = new List<string>();
        string[] roots = {
            "SOFTWARE\\JavaSoft\\JDK",
            "SOFTWARE\\JavaSoft\\Java Development Kit",
            "SOFTWARE\\Eclipse Adoptium\\JDK",
            "SOFTWARE\\Eclipse Foundation\\JDK",
            "SOFTWARE\\Microsoft\\JDK",
            "SOFTWARE\\Azul Systems\\Zulu",
            "SOFTWARE\\BellSoft\\Liberica",
            "SOFTWARE\\Amazon Corretto"
        };
        var views = new KeyValuePair<RegistryHive, RegistryView>[] {
            new KeyValuePair<RegistryHive, RegistryView>(RegistryHive.LocalMachine, RegistryView.Registry64),
            new KeyValuePair<RegistryHive, RegistryView>(RegistryHive.LocalMachine, RegistryView.Registry32),
            new KeyValuePair<RegistryHive, RegistryView>(RegistryHive.CurrentUser, RegistryView.Registry64)
        };
        foreach (KeyValuePair<RegistryHive, RegistryView> view in views)
        {
            RegistryKey baseKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(view.Key, view.Value);
                if (baseKey == null) continue;
                foreach (string rootPath in roots)
                {
                    using (RegistryKey root = baseKey.OpenSubKey(rootPath))
                    {
                        if (root != null) CollectJavaHomes(root, 2, list);
                    }
                }
            }
            catch { /* 권한 없음·키 없음 → 다음 뷰 */ }
            finally { if (baseKey != null) { try { baseKey.Close(); } catch { } } }
        }
        return list;
    }

    // 설치 폴더를 가리키는 값을 찾을 때까지 하위 키를 제한된 깊이로 훑는다.
    static void CollectJavaHomes(RegistryKey key, int depth, List<string> into)
    {
        if (key == null) return;
        string[] valueNames = { "JavaHome", "Path", "InstallationPath" };
        foreach (string name in valueNames)
        {
            string home = null;
            try { home = key.GetValue(name) as string; } catch { }
            if (string.IsNullOrEmpty(home)) continue;
            try { into.Add(Path.Combine(home.Trim().Trim('"'), "bin\\java.exe")); }
            catch { }
        }
        if (depth <= 0) return;
        string[] subs;
        try { subs = key.GetSubKeyNames(); } catch { return; }
        foreach (string sub in subs)
        {
            try { using (RegistryKey child = key.OpenSubKey(sub)) CollectJavaHomes(child, depth - 1, into); }
            catch { }
        }
    }

    // 레지스트리에 없더라도 대부분 아래 폴더에 설치된다(Program Files\Java\jdk-21 등).
    static List<string> WellKnownJavaPaths()
    {
        var list = new List<string>();
        var roots = new List<string>();
        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) roots.Add(Path.Combine(local, "Programs"));
        string[] vendors = { "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft", "Zulu",
            "BellSoft", "Amazon Corretto", "Semeru", "AdoptOpenJDK" };
        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            foreach (string vendor in vendors)
            {
                string dir;
                try { dir = Path.Combine(root, vendor); }
                catch { continue; }
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string home in Directory.GetDirectories(dir)) list.Add(Path.Combine(home, "bin\\java.exe"));
                }
                catch { /* 접근 불가 폴더 → 건너뜀 */ }
            }
        }
        return list;
    }

    // 폴더 이름의 'jdk-21.0.5' / 'jdk1.8.0_402' 에서 버전을 뽑아 최신 우선 정렬에 쓴다(모르면 0 → 마지막).
    static int JavaVersionRank(string exePath)
    {
        try
        {
            string bin = Path.GetDirectoryName(exePath);              // ...\jdk-21.0.5+11\bin
            string home = bin == null ? null : Path.GetDirectoryName(bin);
            string dir = home == null ? "" : Path.GetFileName(home);
            System.Text.RegularExpressions.Match m = JavaDirVersionRe.Match(dir);
            if (!m.Success) return 0;
            int major, minor = 0;
            if (!int.TryParse(m.Groups[1].Value, out major)) return 0;
            if (m.Groups[2].Success) int.TryParse(m.Groups[2].Value, out minor);
            if (major == 1) { major = minor; minor = 0; }             // jdk1.8.0 → 8
            return Math.Min(major, 999) * 1000 + Math.Min(minor, 999);
        }
        catch { return 0; }
    }

    /* JDK 로 인정하는 조건 세 가지.
       1) 같은 bin 에 javac.exe 가 있을 것 — JRE 만 있으면 `java Foo.java` 가 실행되지 않는다.
       2) `-version` 이 정상 종료할 것 — 파이썬의 Microsoft Store 안내용 가짜 exe 같은 것을 여기서 거른다.
       3) 주 버전이 11 이상일 것 — 그 아래는 단일 파일 소스 실행을 못 한다. */
    static bool IsUsableJdk(string exePath)
    {
        try
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;
            string bin = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(bin) || !File.Exists(Path.Combine(bin, "javac.exe"))) return false;
            return JavaFeatureVersion(exePath) >= JavaMinimumFeatureVersion;
        }
        catch { return false; }
    }

    // 주 버전만 돌려준다. 21.0.5 → 21, 1.8.0_402 → 8(9 이전은 1.x 로 적는다). 못 읽으면 0.
    static int JavaFeatureVersion(string exePath)
    {
        string text = RunJavaOutput(exePath, "-version", 5000);
        if (text.Length == 0) return 0;
        System.Text.RegularExpressions.Match m = JavaVersionRe.Match(text);
        string raw = m.Success ? m.Groups[1].Value : "";
        if (raw.Length == 0)
        {
            m = JavaBareVersionRe.Match(text);
            raw = m.Success ? m.Groups[1].Value : "";
        }
        if (raw.Length == 0) return 0;
        string[] parts = raw.Split('.');
        int first;
        if (!int.TryParse(parts[0], out first)) return 0;
        if (first != 1) return first;
        int second;
        return (parts.Length > 1 && int.TryParse(parts[1], out second)) ? second : 0;
    }

    /* -version 처럼 출력이 짧은 명령 전용. 파이썬 탐지(IsUsablePython)와 같은 이유로 스트림을 읽기 전에
       종료를 먼저 기다린다 — 응답하지 않는 PATH 후보에서 ReadToEnd 가 EOF 를 영원히 기다리는 것을 막는다.
       자바는 -version 을 표준오류로 내므로 두 스트림을 합쳐 돌려준다. */
    static string RunJavaOutput(string exePath, string args, int timeoutMs)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exePath, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process p = Process.Start(psi);
            if (p == null) return "";
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                try { p.WaitForExit(1000); } catch { }
                return "";
            }
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (p.ExitCode != 0) return "";
            return (stdout + "\n" + stderr).Trim();
        }
        catch { return ""; }
    }

    static string JavaDiagnostics()
    {
        string exe = FindJava();
        string installRoot = JdkInstallRoot();
        if (exe == null)
        {
            return "{\"ok\":false,\"path\":\"\",\"version\":\"\",\"major\":0,\"source\":\"\""
                 + ",\"minimum\":" + JavaMinimumFeatureVersion
                 + ",\"installRoot\":" + JsonString(installRoot ?? "") + "}";
        }
        string version = RunJavaOutput(exe, "-version", 5000);
        int newline = version.IndexOf('\n');
        if (newline >= 0) version = version.Substring(0, newline);
        return "{\"ok\":true"
             + ",\"path\":" + JsonString(exe)
             + ",\"version\":" + JsonString(version.Trim())
             + ",\"major\":" + JavaFeatureVersion(exe)
             + ",\"source\":" + JsonString(JavaProbeSource())
             + ",\"minimum\":" + JavaMinimumFeatureVersion
             + ",\"installRoot\":" + JsonString(installRoot ?? "")
             + "}";
    }

    /* JDK 9+의 src.zip은 "java.base/java/lang/String.java" 같이 모듈 폴더를 한 겹 두고,
       일부 배포판은 "java/lang/String.java"로 넣는다. 둘 다 받아들이되 요청값은
       Java 완전 이름만 허용해 zip의 다른 파일을 임의로 읽을 수 없게 한다. */
    static string JavaDefinitionSource(byte[] body)
    {
        if (body == null || body.Length == 0 || body.Length > 16 * 1024)
            return "{\"ok\":false,\"reason\":\"bad-request\"}";
        string json = Encoding.UTF8.GetString(body);
        string qualified = JsonStringField(json, "qualified") ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(qualified,
            "^[A-Za-z_$][A-Za-z0-9_$]*(?:\\.[A-Za-z_$][A-Za-z0-9_$]*)+$"))
            return "{\"ok\":false,\"reason\":\"bad-request\"}";

        // 내부 클래스 Foo.Bar가 Foo$Bar로 오는 경우에는 바깥 Foo.java를 연다.
        int dollar = qualified.IndexOf('$');
        if (dollar >= 0) qualified = qualified.Substring(0, dollar);
        string java = FindJava();
        if (java == null) return "{\"ok\":false,\"reason\":\"no-jdk\"}";
        string bin = Path.GetDirectoryName(java);
        string home = string.IsNullOrEmpty(bin) ? null : Path.GetDirectoryName(bin);
        string zipPath = null;
        if (!string.IsNullOrEmpty(home))
        {
            string inLib = Path.Combine(home, "lib", "src.zip");
            string inHome = Path.Combine(home, "src.zip");
            if (File.Exists(inLib)) zipPath = inLib;
            else if (File.Exists(inHome)) zipPath = inHome;
        }
        if (zipPath == null) return "{\"ok\":false,\"reason\":\"no-source\"}";

        string relative = qualified.Replace('.', '/') + ".java";
        ZipArchiveEntry selected = null;
        string source;
        using (FileStream fs = File.OpenRead(zipPath))
        using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Read, false))
        {
            selected = archive.GetEntry("java.base/" + relative) ?? archive.GetEntry(relative);
            if (selected == null)
            {
                string suffix = "/" + relative;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith(suffix, StringComparison.Ordinal)) { selected = entry; break; }
                }
            }
            if (selected == null) return "{\"ok\":false,\"reason\":\"not-found\"}";
            if (selected.Length <= 0 || selected.Length > 5 * 1024 * 1024)
                return "{\"ok\":false,\"reason\":\"source-too-large\"}";
            using (Stream entryStream = selected.Open())
            using (StreamReader reader = new StreamReader(entryStream, Encoding.UTF8, true))
                source = reader.ReadToEnd().Replace("\r\n", "\n").Replace("\r", "\n");
        }

        string name = qualified.Substring(qualified.LastIndexOf('.') + 1);
        System.Text.RegularExpressions.Match declaration = System.Text.RegularExpressions.Regex.Match(source,
            "(?m)^\\s*(?:(?:public|protected|private|static|final|abstract|sealed|non-sealed|strictfp)\\s+)*(?:class|interface|enum|record)\\s+"
            + System.Text.RegularExpressions.Regex.Escape(name) + "\\b");
        int nameAt = declaration.Success
            ? declaration.Index + declaration.Value.LastIndexOf(name, StringComparison.Ordinal)
            : 0;
        int line = 1, column = 0;
        for (int i = 0; i < nameAt && i < source.Length; i++)
        {
            if (source[i] == '\n') { line++; column = 0; }
            else column++;
        }
        return "{\"ok\":true,\"qualified\":" + JsonString(qualified)
             + ",\"name\":" + JsonString(name)
             + ",\"fileName\":" + JsonString(name + ".java")
             + ",\"entry\":" + JsonString(selected.FullName)
             + ",\"line\":" + line + ",\"column\":" + column
             + ",\"source\":" + JsonString(source) + "}";
    }

    /* ===== JDK 원클릭 설치 (Eclipse Adoptium Temurin, LTS 고정) =====
       ffmpeg 원클릭 설치와 같은 구조다 — 백그라운드 스레드가 내려받아 풀고, 프런트는 상태만 폴링한다.
       다른 점 세 가지:
         (1) 파일 하나가 아니라 폴더 전체를 푼다 → 압축 경로가 대상 폴더 밖을 가리키는지(zip-slip) 검사한다.
         (2) 200MB 를 실행 파일로 쓰는 것이라 배포처가 함께 주는 SHA-256 을 대조한다.
         (3) 받기 전에 디스크 여유 공간을 확인한다 — USB 에서 다 받고 실패하면 시간만 버린다. */
    static readonly object JdkInstallLock = new object();
    static volatile string _jdkInstallState = "idle";   // idle | metadata | downloading | verifying | extracting | done | error
    static long _jdkInstallReceived = 0;                // 내려받은 바이트(진행률 표시용)
    static long _jdkInstallTotal = 0;
    static long _jdkExtractDone = 0;                    // 푼 항목 수 — 전개도 수십 초 걸려 진행이 보여야 한다
    static long _jdkExtractTotal = 0;
    static volatile string _jdkInstallError = "";
    static volatile string _jdkInstallVersion = "";
    // 교실에서 학생마다 버전이 갈리지 않도록 한 판으로 고정한다(문법·교재 차이를 만들지 않기 위해).
    const int JdkFeatureVersion = 21;
    const long JdkMinimumFreeBytes = 700L * 1024 * 1024;   // zip(약 200MB) + 전개본(약 340MB) + 여유
    static readonly System.Text.RegularExpressions.Regex Sha256HexRe =
        new System.Text.RegularExpressions.Regex("^[0-9a-fA-F]{64}$");

    static bool JdkInstallRunning()
    {
        string state = _jdkInstallState;
        return state == "metadata" || state == "downloading" || state == "verifying" || state == "extracting";
    }

    static void InstallJdkWorker()
    {
        string tmpZip = Path.Combine(Path.GetTempPath(), "classdock_jdk_" + Guid.NewGuid().ToString("N") + ".zip");
        string staging = null;
        try
        {
            string root = JdkInstallRoot();
            if (string.IsNullOrEmpty(root)) throw new Exception("설치할 위치를 찾지 못했습니다.");
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }   // TLS 1.2

            _jdkInstallState = "metadata";
            string link, checksum, releaseName;
            long declaredSize;
            FetchJdkAsset(out link, out checksum, out releaseName, out declaredSize);
            _jdkInstallVersion = releaseName ?? "";
            EnsureJdkFreeSpace(root, tmpZip, Math.Max(JdkMinimumFreeBytes, declaredSize * 3));

            _jdkInstallState = "downloading";
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(link);
            req.Timeout = 30000;
            req.ReadWriteTimeout = 120000;
            req.UserAgent = "ClassDock";
            using (WebResponse resp = req.GetResponse())
            using (Stream rs = resp.GetResponseStream())
            using (FileStream fs = new FileStream(tmpZip, FileMode.Create, FileAccess.Write))
            {
                Interlocked.Exchange(ref _jdkInstallTotal, resp.ContentLength > 0 ? resp.ContentLength : declaredSize);
                byte[] buf = new byte[81920];
                int n;
                while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                {
                    fs.Write(buf, 0, n);
                    Interlocked.Add(ref _jdkInstallReceived, n);
                }
            }

            _jdkInstallState = "verifying";
            if (!string.Equals(Sha256File(tmpZip), checksum, StringComparison.OrdinalIgnoreCase))
                throw new Exception("내려받은 파일이 배포처가 알려준 것과 다릅니다. 네트워크 문제일 수 있으니 다시 시도해 주세요.");

            _jdkInstallState = "extracting";
            // 옆의 임시 폴더에 먼저 풀고 다 되면 제자리로 옮긴다 — 중간에 실패해도 반쯤 풀린 jdk 폴더가 남지 않는다.
            staging = root + ".part-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            ExtractZipToDirectory(tmpZip, staging);
            ReplaceDirectory(staging, root);
            staging = null;

            ResetJavaProbe();
            if (FindJava() == null) throw new Exception("설치는 끝났지만 자바를 찾지 못했습니다.");
            _jdkInstallState = "done";
        }
        catch (Exception ex)
        {
            _jdkInstallError = FlattenMessage(ex);
            _jdkInstallState = "error";
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            try { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    /* 배포처 메타데이터에서 내려받을 주소와 SHA-256 을 가져온다. 고정 리다이렉트 주소로 바로 받을 수도 있지만
       그러면 무엇을 받았는지 확인할 방법이 없다 — 체크섬을 얻으려고 이 한 번을 더 거친다. */
    static void FetchJdkAsset(out string link, out string checksum, out string releaseName, out long size)
    {
        string url = "https://api.adoptium.net/v3/assets/latest/" + JdkFeatureVersion
            + "/hotspot?os=windows&architecture=x64&image_type=jdk&vendor=eclipse";
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = 30000;
        req.ReadWriteTimeout = 60000;
        req.UserAgent = "ClassDock";
        string json;
        using (WebResponse resp = req.GetResponse())
        using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            json = reader.ReadToEnd();

        // 응답에는 설치 프로그램 등 다른 블록도 올 수 있어, "package" 뒤에서부터 찾아 그 안의 값만 읽는다.
        int at = json.IndexOf("\"package\"", StringComparison.Ordinal);
        if (at < 0) throw new Exception("배포처 응답을 이해하지 못했습니다.");
        string tail = json.Substring(at);
        link = JsonStringField(tail, "link");
        checksum = JsonStringField(tail, "checksum");
        releaseName = JsonStringField(json, "release_name");
        size = 0;
        long.TryParse(JsonNumberField(tail, "size"), out size);
        if (string.IsNullOrEmpty(link) || !link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || !link.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new Exception("배포처가 알려준 주소가 올바르지 않습니다.");
        if (string.IsNullOrEmpty(checksum) || !Sha256HexRe.IsMatch(checksum))
            throw new Exception("배포처가 알려준 검증값이 올바르지 않습니다.");
    }

    // 아주 작은 JSON 값 추출기 — 이 응답에서 필요한 몇 개만 읽는다(런처에 JSON 파서를 들이지 않기 위해).
    static string JsonStringField(string json, string name)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
            json, "\"" + System.Text.RegularExpressions.Regex.Escape(name) + "\"\\s*:\\s*\"([^\"\\\\]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    static string JsonNumberField(string json, string name)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
            json, "\"" + System.Text.RegularExpressions.Regex.Escape(name) + "\"\\s*:\\s*(\\d+)");
        return m.Success ? m.Groups[1].Value : "";
    }

    static string Sha256File(string path)
    {
        using (FileStream fs = File.OpenRead(path))
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
    }

    // 설치 위치와 임시 파일이 서로 다른 드라이브일 수 있어 둘 다 본다.
    static void EnsureJdkFreeSpace(string installRoot, string tempFile, long needed)
    {
        CheckDriveFreeSpace(installRoot, needed);
        CheckDriveFreeSpace(tempFile, needed);
    }

    static void CheckDriveFreeSpace(string anyPathOnDrive, long needed)
    {
        long available;
        try
        {
            string rootPath = Path.GetPathRoot(Path.GetFullPath(anyPathOnDrive));
            if (string.IsNullOrEmpty(rootPath)) return;
            DriveInfo drive = new DriveInfo(rootPath);
            // 드라이브 정보를 못 읽는 경우(네트워크 경로 등)는 막지 않는다 — 실제 쓰기에서 판정된다.
            if (!drive.IsReady) return;
            available = drive.AvailableFreeSpace;
        }
        catch { return; }
        if (available < needed)
            throw new Exception("디스크 여유 공간이 부족합니다(약 "
                + (needed / (1024 * 1024)) + "MB 필요, 현재 " + (available / (1024 * 1024)) + "MB).");
    }

    /* zip 전체를 폴더로 푼다. 압축 안의 상대경로가 대상 폴더 밖을 가리키면(zip-slip) 건너뛴다 —
       ffmpeg 설치는 파일 하나만 꺼내 이 검사가 필요 없었지만, 폴더째 푸는 여기서는 필수다. */
    static void ExtractZipToDirectory(string zipPath, string destRoot)
    {
        Directory.CreateDirectory(destRoot);
        string rootFull = Path.GetFullPath(destRoot);
        if (!rootFull.EndsWith("\\", StringComparison.Ordinal)) rootFull += "\\";
        using (FileStream zipStream = File.OpenRead(zipPath))
        using (ZipArchive archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            Interlocked.Exchange(ref _jdkExtractTotal, archive.Entries.Count);
            Interlocked.Exchange(ref _jdkExtractDone, 0);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                Interlocked.Increment(ref _jdkExtractDone);
                string rel = (entry.FullName ?? "").Replace('/', '\\');
                if (rel.Length == 0) continue;
                string full;
                try { full = Path.GetFullPath(Path.Combine(rootFull, rel)); }
                catch { continue; }
                if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;   // zip-slip → 버린다
                if (rel.EndsWith("\\", StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(full);
                    continue;
                }
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using (Stream es = entry.Open())
                using (FileStream os = new FileStream(full, FileMode.Create, FileAccess.Write))
                {
                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = es.Read(buf, 0, buf.Length)) > 0) os.Write(buf, 0, n);
                }
            }
        }
    }

    // 다 푼 폴더를 제자리로 옮긴다. 이미 있으면 옆으로 밀어 두었다가 성공한 뒤에 지운다.
    static void ReplaceDirectory(string staging, string dest)
    {
        string parent = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        string old = null;
        if (Directory.Exists(dest))
        {
            old = dest + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Directory.Move(dest, old);
        }
        try { Directory.Move(staging, dest); }
        catch
        {
            if (old != null && !Directory.Exists(dest)) { try { Directory.Move(old, dest); } catch { } }
            throw;
        }
        if (old != null) { try { Directory.Delete(old, true); } catch { } }
    }

    /* ===== 자바 실습용 라이브러리(jar) =====
       표준 라이브러리만으로는 JSON·CSV·단위테스트 예제를 만들 수 없다. 마븐 같은 빌드 도구를 들이는 대신
       검증한 jar 를 캐시에 두고 실행 클래스패스에 얹는다(js 쪽 npm 패키지 캐시와 같은 발상).
       빌드 도구를 어설프게 흉내 내지 않으려고 두 가지를 스스로 지킨다.
         (1) id → 좌표 표는 서버가 가진다. 프런트가 보내는 것은 id(또는 좌표) 뿐이고 경로·URL 은 받지 않는다.
         (2) 전이 의존성은 해결하지 않는다 → 단일 jar 로 끝나는 라이브러리만 카탈로그에 올린다.
       여기는 '이미 있는 jar 를 찾아 -cp 에 얹는' 데까지다. 내려받기·검증(SHA-256)은 설치 API 에서 붙인다. */
    class JavaLibrary
    {
        public string Id;
        public string Label;
        public string Group;
        public string Artifact;
        public string Version;
        // 배포처에서 받은 파일과 대조할 값. 비워 두면 설치할 때 배포처의 .sha1 로만 확인하고 실제 SHA-256 을
        // 설치 로그에 남긴다 — 그 값을 여기 옮겨 적으면 다음부터는 변조까지 걸러진다.
        public string Sha256;
        public string Words;      // 편집기 자동완성에 얹을 클래스 이름
        public string Sample;     // 라이브러리 목록에 보여줄 예시 import 한 줄
    }

    /* 교실에서 쓸 만하면서 '한 파일로 끝나는' 것만 골랐다 — 의존성이 딸린 라이브러리를 하나라도 넣는 순간
       버전 충돌·스코프까지 다뤄야 해서 마븐 없이는 감당할 수 없다. JUnit 은 그래서 필요한 것을 모두 담은
       console-standalone 한 개를 쓴다. 학생마다 문법이 갈리지 않도록 버전은 JDK 와 같이 한 판으로 고정한다. */
    static readonly JavaLibrary[] JavaLibraryCatalog = new JavaLibrary[]
    {
        new JavaLibrary {
            Id = "gson", Label = "Gson", Group = "com.google.code.gson", Artifact = "gson", Version = "2.11.0",
            Sha256 = "57928d6e5a6edeb2abd3770a8f95ba44dce45f3b23b7a9dc2b309c581552a78b",
            Words = "Gson GsonBuilder JsonObject JsonArray JsonElement JsonParser",
            Sample = "import com.google.gson.Gson;" },
        new JavaLibrary {
            Id = "commons-lang3", Label = "Apache Commons Lang", Group = "org.apache.commons",
            Artifact = "commons-lang3", Version = "3.17.0",
            Sha256 = "6ee731df5c8e5a2976a1ca023b6bb320ea8d3539fbe64c8a1d5cb765127c33b4",
            Words = "StringUtils NumberUtils ArrayUtils RandomStringUtils",
            Sample = "import org.apache.commons.lang3.StringUtils;" },
        new JavaLibrary {
            Id = "commons-csv", Label = "Apache Commons CSV", Group = "org.apache.commons",
            Artifact = "commons-csv", Version = "1.10.0",
            Sha256 = "2d06e6a07a636baf777ad8e659256f2119109dde23551c9b80c5422d424b808c",
            Words = "CSVFormat CSVParser CSVPrinter CSVRecord",
            Sample = "import org.apache.commons.csv.CSVFormat;" },
        new JavaLibrary {
            Id = "jsoup", Label = "jsoup", Group = "org.jsoup", Artifact = "jsoup", Version = "1.18.3",
            Sha256 = "5be1ccd3228ae5fd6eed1bd6d827bac2bc65b91c20e9957d16ea65f739f15302",
            Words = "Jsoup Document Element Elements",
            Sample = "import org.jsoup.Jsoup;" },
        new JavaLibrary {
            Id = "junit", Label = "JUnit 5 (console standalone)", Group = "org.junit.platform",
            Artifact = "junit-platform-console-standalone", Version = "1.11.4",
            Sha256 = "b016ef6b1c3454d6d7c2c88ce081dabf289699686af6622d6e4e2e1b54b4a2fc",
            Words = "Test Assertions assertEquals assertTrue assertThrows BeforeEach DisplayName",
            Sample = "import org.junit.jupiter.api.Test;" },
        new JavaLibrary {
            Id = "lombok", Label = "Lombok", Group = "org.projectlombok", Artifact = "lombok", Version = "1.18.48",
            Sha256 = "85477a4655ebb2c074a9099cfb749be454449fee564d4282610df1b85f7c508b",
            Words = "Data Getter Setter Builder Value NonNull ToString EqualsAndHashCode NoArgsConstructor AllArgsConstructor RequiredArgsConstructor",
            Sample = "import lombok.Data;" }
    };

    static JavaLibrary FindJavaLibraryCatalogItem(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (JavaLibrary item in JavaLibraryCatalog)
            if (string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) return item;
        return null;
    }

    static JavaLibrary FindJavaLibraryCatalogCoordinate(string group, string artifact, string version)
    {
        foreach (JavaLibrary item in JavaLibraryCatalog)
            if (string.Equals(item.Group, group, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Artifact, artifact, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Version, version, StringComparison.OrdinalIgnoreCase)) return item;
        return null;
    }

    // 캐시 위치는 JDK 와 같은 규칙 — exe 옆이 1순위다(USB 에 담아 다니면 라이브러리도 함께 따라간다).
    static string JavaLibraryPortableRoot()
    {
        try { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "java-libs"); }
        catch { return null; }
    }

    // exe 옆에 쓸 수 없을 때(Program Files·읽기 전용 USB)의 대체 위치.
    static string JavaLibraryLocalAppDataRoot()
    {
        try
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassDock", "java-libs");
        }
        catch { return null; }
    }

    // 배포본에 미리 담아 보내는 jar. 인터넷이 막힌 교실에서도 내려받기 없이 쓰게 한다(vendor\wheels 와 같은 역할).
    static string JavaLibraryVendorRoot()
    {
        try { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vendor", "java-libs"); }
        catch { return null; }
    }

    /* 찾을 때는 세 곳을 모두 본다 — 쓰기는 한 곳에 하더라도, PC 를 옮겨 다니면 예전에 받아 둔 캐시가
       다른 쪽에 남아 있을 수 있다. 클래스패스는 ; 로 항목을 나누므로 ; 나 " 가 든 경로는 아예 뺀다. */
    static List<string> JavaLibraryLookupRoots()
    {
        List<string> roots = new List<string>();
        string[] candidates = new string[] {
            JavaLibraryPortableRoot(), JavaLibraryLocalAppDataRoot(), JavaLibraryVendorRoot() };
        foreach (string root in candidates)
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (root.IndexOf(';') >= 0 || root.IndexOf('"') >= 0) continue;
            if (!roots.Contains(root)) roots.Add(root);
        }
        return roots;
    }

    /* 프런트가 보내는 목록의 각 항목은 카탈로그 id 이거나 group:artifact:version 좌표다.
       두 가지 모두 그대로 폴더·파일 이름이 되므로 여기서 좁게 검사한다 — 특히 .. 를 막아
       캐시 폴더 밖의 파일이 클래스패스에 얹히는 일이 없게 한다. */
    static readonly System.Text.RegularExpressions.Regex JavaLibraryIdRe =
        new System.Text.RegularExpressions.Regex("^[a-z0-9][a-z0-9-]{0,31}$");
    static readonly System.Text.RegularExpressions.Regex JavaLibrarySegmentRe =
        new System.Text.RegularExpressions.Regex("^[A-Za-z0-9][A-Za-z0-9_.+-]{0,99}$");
    const int JavaLibraryMaxPerRun = 20;

    static bool JavaLibrarySafeSegment(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
        return JavaLibrarySegmentRe.IsMatch(value);
    }

    // "com.google.code.gson:gson:2.11.0" → "com\google\code\gson\gson\2.11.0\gson-2.11.0.jar" (마븐 저장소와 같은 모양).
    static string JavaLibraryRelativePath(string group, string artifact, string version)
    {
        if (string.IsNullOrEmpty(group)) return null;
        if (!JavaLibrarySafeSegment(artifact) || !JavaLibrarySafeSegment(version)) return null;
        StringBuilder sb = new StringBuilder();
        foreach (string part in group.Split('.'))
        {
            if (!JavaLibrarySafeSegment(part)) return null;
            sb.Append(part).Append('\\');
        }
        sb.Append(artifact).Append('\\').Append(version).Append('\\')
          .Append(artifact).Append('-').Append(version).Append(".jar");
        return sb.ToString();
    }

    /* 루트 하나 아래의 절대 경로. 조립 결과가 그 루트 밖을 가리키면 null 이다(zip 전개와 같은 검사) —
       읽기·쓰기·삭제가 모두 이 함수를 거치므로 캐시 폴더 밖으로 나갈 길이 한 곳으로 모인다. */
    static string JavaLibraryFileUnder(string root, string relative)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(relative)) return null;
        string rootFull, full;
        try
        {
            rootFull = Path.GetFullPath(root);
            if (!rootFull.EndsWith("\\", StringComparison.Ordinal)) rootFull += "\\";
            full = Path.GetFullPath(Path.Combine(rootFull, relative));
        }
        catch { return null; }
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;
        return full;
    }

    // 상대 경로를 조회 루트들에서 찾는다. 먼저 찾은 것이 이긴다(exe 옆 → LocalAppData → 배포 동봉).
    static string FindJavaLibraryFile(string relative)
    {
        foreach (string root in JavaLibraryLookupRoots())
        {
            string full = JavaLibraryFileUnder(root, relative);
            if (full == null) continue;
            try { if (File.Exists(full)) return full; }
            catch { }
        }
        return null;
    }

    /* 항목 하나(카탈로그 id 또는 좌표)를 '무엇을 어디에 두는가' 로 풀어 놓은 것. 실행·설치·삭제·목록이
       모두 여기를 거치게 해서 좌표 검사와 경로 조립 규칙이 갈라지지 않게 한다. */
    class JavaLibraryTarget
    {
        public string Spec;        // 사용자가 적은 그대로(안내 문구에 쓴다)
        public string Id;          // 카탈로그에 있으면 그 id, 아니면 빈 문자열
        public string Label;
        public string Group;
        public string Artifact;
        public string Version;
        public string Sha256;      // 카탈로그에 박아 둔 검증값(없으면 빈 문자열)
        public string Relative;    // 캐시 안의 경로
    }

    static JavaLibraryTarget ParseJavaLibraryTarget(string spec)
    {
        string value = (spec ?? "").Trim();
        if (value.Length == 0) return null;
        JavaLibrary item = null;
        string group, artifact, version;
        if (JavaLibraryIdRe.IsMatch(value))
        {
            item = FindJavaLibraryCatalogItem(value);
            if (item == null) return null;
            group = item.Group; artifact = item.Artifact; version = item.Version;
        }
        else
        {
            string[] parts = value.Split(':');
            if (parts.Length != 3) return null;
            group = parts[0]; artifact = parts[1]; version = parts[2];
            // 좌표로 적었어도 카탈로그와 같은 것이면 카탈로그가 아는 이름·검증값을 쓴다.
            item = FindJavaLibraryCatalogCoordinate(group, artifact, version);
        }
        string relative = JavaLibraryRelativePath(group, artifact, version);
        if (relative == null) return null;
        JavaLibraryTarget target = new JavaLibraryTarget();
        target.Spec = value;
        target.Id = item != null ? item.Id : "";
        target.Label = item != null ? item.Label : artifact + " " + version;
        target.Group = group;
        target.Artifact = artifact;
        target.Version = version;
        target.Sha256 = item != null ? (item.Sha256 ?? "") : "";
        target.Relative = relative;
        return target;
    }

    // 항목 하나를 실제 jar 경로로. 캐시에 없으면 null.
    static string ResolveJavaLibrarySpec(string spec)
    {
        JavaLibraryTarget target = ParseJavaLibraryTarget(spec);
        return target == null ? null : FindJavaLibraryFile(target.Relative);
    }

    /* 실행 요청의 libs= 목록(쉼표·공백 구분) → jar 경로들. 못 찾은 이름은 missing 으로 돌려준다.
       조용히 빼고 실행하면 학생은 원인을 알 수 없는 NoClassDefFoundError 만 보게 된다. */
    static List<string> ResolveJavaLibraryJars(string libs, out List<string> missing)
    {
        List<string> jars = new List<string>();
        missing = new List<string>();
        string[] raw = (libs ?? "").Split(new char[] { ',', ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (string spec in raw)
        {
            if (jars.Count + missing.Count >= JavaLibraryMaxPerRun) break;
            string jar = ResolveJavaLibrarySpec(spec);
            if (jar == null)
            {
                if (!missing.Contains(spec)) missing.Add(spec);
                continue;
            }
            if (!jars.Contains(jar)) jars.Add(jar);
        }
        return jars;
    }

    /* -cp 에 넘길 한 줄. 첫 항목은 컴파일 결과가 놓이는 실행 폴더이고 그 뒤에 선택한 jar 가 붙는다.
       라이브러리를 하나도 고르지 않았을 때도 같은 경로를 쓴다(따로 분기하면 한쪽만 고치는 실수가 난다).
       jar 이름은 위에서 걸러 ; 나 " 가 있을 수 없지만, 임시 폴더 경로는 우리 손 밖이라 여기서 확인한다. */
    static string JavaClassPath(string tempRoot, List<string> jars)
    {
        if (tempRoot == null || tempRoot.IndexOf(';') >= 0 || tempRoot.IndexOf('"') >= 0)
            throw new Exception("실행 임시 폴더 경로에 ; 또는 \" 가 있어 클래스패스를 만들 수 없습니다.");
        StringBuilder sb = new StringBuilder(tempRoot);
        if (jars != null) foreach (string jar in jars) sb.Append(';').Append(jar);
        return sb.ToString();
    }

    // JDK 24+는 processor 관련 옵션이 없으면 annotation processor를 자동 실행하지 않는다.
    // 선택한 jar 부분만 processor path로 명시하면 Lombok 같은 라이브러리는 계속 동작하면서,
    // 학생 소스 출력 폴더를 processor 검색 대상으로 삼지 않는다.
    static string JavaAnnotationProcessorArgs(string classPath)
    {
        string value = classPath ?? "";
        int separator = value.IndexOf(';');
        if (separator < 0 || separator + 1 >= value.Length) return "";
        string jars = value.Substring(separator + 1);
        return string.IsNullOrEmpty(jars) ? "" : " -processorpath " + QuoteProcessArgument(jars);
    }

    /* ===== 라이브러리 원클릭 설치 (Maven Central 고정) =====
       마븐을 설치하지 않고 jar 파일 하나만 받아 캐시에 둔다. 지키는 것 세 가지.
         (1) 주소는 서버가 검증한 좌표로만 조립한다 — 프런트가 준 URL 은 쓰지 않는다.
         (2) 받은 파일은 반드시 대조한다. 카탈로그에 박아 둔 SHA-256 이 있으면 그것과 맞춰 변조까지 걸러내고,
             없으면 배포처가 함께 두는 .sha1 과 맞춘다(같은 곳에서 온 값이라 '깨진 파일' 검출까지가 한계다).
         (3) 검증을 통과하기 전에는 .part 이름으로만 존재한다 — 반쯤 받은 jar 가 클래스패스에 얹히지 않게.
       진행 로그·폴링·취소 규약은 pip·npm 설치와 같다(프런트가 같은 모양으로 그린다). */
    class JavaLibJob
    {
        public string Id;
        public readonly object Sync = new object();
        public readonly LimitedTextBuffer Log = new LimitedTextBuffer();
        public bool Complete;
        public bool CancelRequested;
        public int ExitCode = -1;
        public DateTime DoneAt = DateTime.MaxValue;
    }

    static readonly object JavaLibJobsLock = new object();
    static readonly Dictionary<string, JavaLibJob> JavaLibJobs = new Dictionary<string, JavaLibJob>();
    const string JavaLibraryRepository = "https://repo1.maven.org/maven2/";
    const long JavaLibraryMaxJarBytes = 30L * 1024 * 1024;    // 수업용 라이브러리는 이 안에 다 들어온다
    const int JavaLibraryMaxInstalled = 20;
    static readonly System.Text.RegularExpressions.Regex Sha1HexRe =
        new System.Text.RegularExpressions.Regex("[0-9a-fA-F]{40}");

    // 내려받은 jar 를 실제로 두는 곳. JdkInstallRoot 와 같은 판단 — exe 옆에 쓸 수 있으면 옆에 둔다.
    static string JavaLibraryInstallRoot()
    {
        string portable = JavaLibraryPortableRoot();
        if (!string.IsNullOrEmpty(portable) && portable.IndexOf(';') < 0 && portable.IndexOf('"') < 0)
        {
            string parent = null;
            try { parent = Path.GetDirectoryName(portable); } catch { }
            if (CanWriteInto(parent)) return portable;
        }
        string local = JavaLibraryLocalAppDataRoot();
        if (string.IsNullOrEmpty(local) || local.IndexOf(';') >= 0 || local.IndexOf('"') >= 0) return null;
        return local;
    }

    // 배포본에 담겨 온 파일인지 — 이런 항목은 지우지 않는다(지워도 다음 실행에 다시 나타나 혼란만 준다).
    static bool IsBundledJavaLibrary(string relative)
    {
        string full = JavaLibraryFileUnder(JavaLibraryVendorRoot(), relative);
        try { return full != null && File.Exists(full); }
        catch { return false; }
    }

    /* 캐시 폴더를 훑어 좌표를 되살린다. 경로 모양이 <group…>\<artifact>\<version>\<artifact>-<version>.jar 일 때만
       목록에 넣는다 — 사용자가 아무 jar 나 던져 둔 경우를 그대로 라이브러리로 보여 주지 않기 위해서다. */
    static List<JavaLibraryTarget> EnumerateJavaLibraryJars(string root)
    {
        List<JavaLibraryTarget> found = new List<JavaLibraryTarget>();
        if (string.IsNullOrEmpty(root)) return found;
        string rootFull;
        string[] files;
        try
        {
            if (!Directory.Exists(root)) return found;
            rootFull = Path.GetFullPath(root);
            if (!rootFull.EndsWith("\\", StringComparison.Ordinal)) rootFull += "\\";
            files = Directory.GetFiles(root, "*.jar", SearchOption.AllDirectories);
        }
        catch { return found; }
        foreach (string file in files)
        {
            string full;
            try { full = Path.GetFullPath(file); }
            catch { continue; }
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
            string[] parts = full.Substring(rootFull.Length).Split('\\');
            if (parts.Length < 4) continue;
            string version = parts[parts.Length - 2];
            string artifact = parts[parts.Length - 3];
            if (!string.Equals(parts[parts.Length - 1], artifact + "-" + version + ".jar",
                StringComparison.OrdinalIgnoreCase)) continue;
            StringBuilder group = new StringBuilder();
            for (int i = 0; i < parts.Length - 3; i++)
            {
                if (group.Length > 0) group.Append('.');
                group.Append(parts[i]);
            }
            JavaLibraryTarget target = ParseJavaLibraryTarget(group + ":" + artifact + ":" + version);
            if (target != null) found.Add(target);
        }
        return found;
    }

    // 캐시에 있는 jar 수. 배포 동봉본은 세지 않는다 — 사용자가 지울 수 없어 한도만 잡아먹는다.
    static int InstalledJavaLibraryCount()
    {
        List<string> seen = new List<string>();
        string[] roots = new string[] { JavaLibraryPortableRoot(), JavaLibraryLocalAppDataRoot() };
        foreach (string root in roots)
            foreach (JavaLibraryTarget target in EnumerateJavaLibraryJars(root))
                if (!seen.Contains(target.Relative)) seen.Add(target.Relative);
        return seen.Count;
    }

    /* 카탈로그에 없는 jar(직접 좌표로 받은 것)는 알려 줄 클래스 이름이 없어 자동완성이 통째로 비었다.
       jar 안의 최상위 클래스 이름만 읽어 채운다 — 압축을 풀지 않고 엔트리 이름만 훑으므로
       0.6MB jar 기준 40ms 남짓이고, 같은 파일은 아래 캐시가 한 번만 읽게 한다.
       메서드까지는 뽑지 않는다(javap 를 여러 번 돌려야 하고 큰 jar 는 표가 수백 KB 가 된다). */
    const int JavaJarWordLimit = 400;        // 이보다 많으면 자동완성 목록에서 키워드가 묻힌다
    static readonly Dictionary<string, string> JavaJarWordCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> JavaJarClassCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static readonly object JavaJarWordLock = new object();

    static string JavaLibraryJarWords(string jarPath)
    {
        if (string.IsNullOrEmpty(jarPath)) return "";
        string key;
        try
        {
            FileInfo info = new FileInfo(jarPath);
            if (!info.Exists) return "";
            // 같은 자리에 다른 jar 가 오면(재설치) 다시 읽도록 크기·수정시각을 열쇠에 넣는다.
            key = jarPath + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        }
        catch { return ""; }
        lock (JavaJarWordLock)
        {
            string hit;
            if (JavaJarWordCache.TryGetValue(key, out hit)) return hit;
        }
        string words = ScanJavaJarWords(jarPath);
        lock (JavaJarWordLock)
        {
            if (JavaJarWordCache.Count > 64) JavaJarWordCache.Clear();   // 오래 켜 둬도 무한정 쌓이지 않게
            JavaJarWordCache[key] = words;
        }
        return words;
    }

    // 자동 import 에는 단순 이름뿐 아니라 패키지 전체 이름도 필요하다. ZIP 목록에서 읽고 같은 방식으로 캐시한다.
    static string JavaLibraryJarClasses(string jarPath)
    {
        if (string.IsNullOrEmpty(jarPath)) return "";
        string key;
        try
        {
            FileInfo info = new FileInfo(jarPath);
            if (!info.Exists) return "";
            key = jarPath + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        }
        catch { return ""; }
        lock (JavaJarWordLock)
        {
            string hit;
            if (JavaJarClassCache.TryGetValue(key, out hit)) return hit;
        }
        List<string> classes = EnumerateJavaJarClasses(jarPath, JavaJarWordLimit);
        classes.Sort(StringComparer.Ordinal);
        string names = string.Join(" ", classes.ToArray());
        lock (JavaJarWordLock)
        {
            if (JavaJarClassCache.Count > 64) JavaJarClassCache.Clear();
            JavaJarClassCache[key] = names;
        }
        return names;
    }

    static bool IsHiddenJavaPackagePath(string path)
    {
        string lowered = "/" + (path ?? "").Replace('\\', '/').ToLowerInvariant();
        // 라이브러리가 "쓰라고 낸" 것이 아닌 자리 — 제안하면 컴파일은 되어도 다음 판올림에 깨진다.
        return lowered.Contains("/internal/") || lowered.Contains("/impl/") || lowered.Contains("/shaded/");
    }

    /* jar 안의 최상위 클래스(패키지까지 붙은 이름). 이름 목록과 javap 멤버 추출이 같은 규칙을 쓴다. */
    static List<string> EnumerateJavaJarClasses(string jarPath, int limit)
    {
        List<string> classes = new List<string>();
        try
        {
            using (FileStream stream = File.OpenRead(jarPath))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string full = entry.FullName ?? "";
                    if (!full.EndsWith(".class", StringComparison.OrdinalIgnoreCase)) continue;
                    if (full.IndexOf('$') >= 0) continue;                                          // 내부·익명 클래스
                    if (full.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue; // 멀티릴리스 사본
                    if (IsHiddenJavaPackagePath(full)) continue;
                    string name = entry.Name ?? "";
                    if (name.Length <= 6) continue;
                    name = name.Substring(0, name.Length - 6);                                      // ".class" 를 뗀다
                    // 자바 클래스는 대문자로 시작한다 — package-info 처럼 관례 밖 이름은 제안하지 않는다.
                    if (name.Length == 0 || !char.IsUpper(name[0])) continue;
                    classes.Add(full.Substring(0, full.Length - 6).Replace('/', '.'));
                    if (classes.Count >= limit) break;
                }
            }
        }
        catch { return new List<string>(); }   // 깨진 jar 라도 목록 그리기는 계속되어야 한다
        return classes;
    }

    static string JavaSimpleName(string qualified)
    {
        string value = qualified ?? "";
        int at = value.LastIndexOf('.');
        return at < 0 ? value : value.Substring(at + 1);
    }

    static string ScanJavaJarWords(string jarPath)
    {
        List<string> names = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string qualified in EnumerateJavaJarClasses(jarPath, JavaJarWordLimit))
        {
            string simple = JavaSimpleName(qualified);
            if (simple.Length == 0 || !seen.Add(simple)) continue;
            names.Add(simple);
        }
        names.Sort(StringComparer.Ordinal);
        return string.Join(" ", names.ToArray());
    }

    /* 이름 다음은 멤버다. 직접 좌표로 받은 jar 는 무엇이 들어 있는지 적어 둔 표가 없으므로 javap 로 뽑는다.
       javap 는 클래스를 읽기만 하고 실행하지 않으므로 남의 jar 에 돌려도 안전하다.
       주의할 점 셋:
         · javap 는 javac 와 달리 @argfile 을 못 읽는다(파일 이름을 클래스 이름으로 오해한다) —
           그래서 한 번에 다 넘기면 Windows 명령줄 32KB 한도에 걸린다. 400개씩 끊어 부른다.
         · 인자 '이름' 은 클래스 파일에 남지 않는다(-parameters 로 컴파일한 jar 만 예외).
           그래서 이름만 모으고 서명은 만들지 않는다 — 손으로 적어 둔 기본 목록보다 안내가 얕은 이유다.
         · 큰 jar 는 표가 수백 KB 가 된다. 클래스·용량 상한을 두고, 넘으면 거기서 끊는다.
       뽑은 표는 jar 옆에 <파일>.members.json 으로 남겨 다음부터는 곧바로 돌려준다. */
    const int JavaJarMemberClassLimit = 800;      // javap 로 훑을 최상위 클래스 수
    const int JavaJarMemberBatch = 400;           // 한 번의 javap 호출에 넘길 클래스 수(명령줄 길이 한도)
    const int JavaJarMemberJsonLimit = 200 * 1024;
    const int JavaJarMemberTimeoutMs = 30000;     // 한 번의 javap 호출이 이보다 걸리면 포기한다
    static readonly object JavaJarMemberLock = new object();

    // 좌표 하나의 멤버 표(JSON). 기본 목록에 있는 것은 프런트가 손으로 적어 둔 표를 쓰므로 여기서 뽑지 않는다.
    static string JavaLibraryMembersJson(string spec)
    {
        JavaLibraryTarget target = ParseJavaLibraryTarget(spec);
        if (target == null || !string.IsNullOrEmpty(target.Id)) return "{}";
        string file = FindJavaLibraryFile(target.Relative);
        if (file == null) return "{}";
        lock (JavaJarMemberLock)
        {
            string cached = ReadJavaJarMemberCache(file);
            if (cached != null) return cached;
            string json = ExtractJavaJarMembersJson(file);
            if (json == null) return "{}";
            WriteJavaJarMemberCache(file, json);
            return json;
        }
    }

    static string JavaJarMemberCachePath(string jarPath) { return jarPath + ".members.json"; }

    // jar 보다 새 캐시만 쓴다 — 같은 좌표를 지웠다 다시 받으면 표도 다시 뽑아야 한다.
    static string ReadJavaJarMemberCache(string jarPath)
    {
        try
        {
            string cache = JavaJarMemberCachePath(jarPath);
            if (!File.Exists(cache)) return null;
            if (File.GetLastWriteTimeUtc(cache) < File.GetLastWriteTimeUtc(jarPath)) return null;
            if (new FileInfo(cache).Length > JavaJarMemberJsonLimit * 2) return null;
            string json = File.ReadAllText(cache, Encoding.UTF8);
            // v1 은 단순 클래스 이름과 멤버 이름만 저장해 동명 클래스·static 구분을 잃었다.
            // 새 형식 표식이 없는 캐시는 다시 뽑아 잘못된 자동완성이 계속 남지 않게 한다.
            return json.IndexOf("\"$schema\":2", StringComparison.Ordinal) >= 0 ? json : null;
        }
        catch { return null; }
    }

    // 쓰지 못해도(읽기 전용 폴더 등) 이번 답은 그대로 나간다 — 다음에 다시 뽑을 뿐이다.
    static void WriteJavaJarMemberCache(string jarPath, string json)
    {
        try { File.WriteAllText(JavaJarMemberCachePath(jarPath), json ?? "{}", new UTF8Encoding(false)); }
        catch { }
    }

    static string ExtractJavaJarMembersJson(string jarPath)
    {
        string java = FindJava();
        if (java == null) return null;
        string bin = Path.GetDirectoryName(java);
        string javap = string.IsNullOrEmpty(bin) ? null : Path.Combine(bin, "javap.exe");
        if (javap == null || !File.Exists(javap)) return null;
        List<string> classes = EnumerateJavaJarClasses(jarPath, JavaJarMemberClassLimit);
        if (classes.Count == 0) return "{\"$schema\":2}";
        Dictionary<string, List<string>> table = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string> order = new List<string>();
        for (int i = 0; i < classes.Count; i += JavaJarMemberBatch)
        {
            int take = Math.Min(JavaJarMemberBatch, classes.Count - i);
            string text = RunJavap(javap, jarPath, classes.GetRange(i, take));
            // 시간 초과·실패한 결과를 빈/부분 캐시로 굳히지 않는다. 다음 요청에서 다시 시도할 수 있어야 한다.
            if (text == null) return null;
            ParseJavapOutput(text, table, order);
        }
        return JavaMemberTableJson(table, order);
    }

    static string RunJavap(string javap, string jarPath, List<string> classes)
    {
        StringBuilder args = new StringBuilder();
        args.Append("-public -classpath \"").Append(jarPath).Append('"');
        foreach (string name in classes) args.Append(' ').Append(name);
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(javap, args.ToString());
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            using (Process proc = Process.Start(psi))
            {
                /* 두 스트림을 먼저 동시에 비운다. 한쪽을 ReadToEnd 로 다 읽은 뒤 다른 쪽을 읽으면
                   자식이 반대쪽 파이프 버퍼에서 막혀 WaitForExit(제한 시간)까지 도달하지 못할 수 있다. */
                string output = "";
                Thread outReader = new Thread(delegate()
                {
                    try { output = proc.StandardOutput.ReadToEnd(); } catch { }
                });
                Thread errReader = new Thread(delegate()
                {
                    try { proc.StandardError.ReadToEnd(); } catch { }
                });
                outReader.IsBackground = true; errReader.IsBackground = true;
                outReader.Start(); errReader.Start();
                bool exited = proc.WaitForExit(JavaJarMemberTimeoutMs);
                if (!exited)
                {
                    try { proc.Kill(); } catch { }
                    try { proc.WaitForExit(2000); } catch { }
                    try { outReader.Join(2000); errReader.Join(2000); } catch { }
                    return null;
                }
                try { outReader.Join(2000); errReader.Join(2000); } catch { }
                return output;
            }
        }
        catch { return null; }
    }

    /* javap 출력은 "클래스 한 줄 + 들여쓴 멤버 여러 줄" 이 되풀이된다.
         public final class org.apache.commons.lang3.StringUtils {
           public static boolean isBlank(java.lang.CharSequence);
           public static final java.lang.String EMPTY;
         }
       멤버 앞에는 S:/I:로 static/instance를, 메서드 뒤에는 ()를 붙여 프런트가 안전하게 가른다. */
    static void ParseJavapOutput(string text, Dictionary<string, List<string>> table, List<string> order)
    {
        string current = null;
        foreach (string raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line[0] != ' ' && line[0] != '\t')
            {
                current = JavapTypeName(line);
                if (current != null && !table.ContainsKey(current)) { table[current] = new List<string>(); order.Add(current); }
                continue;
            }
            if (current == null) continue;
            string member = JavapMemberName(line);
            if (member == null) continue;
            List<string> list = table[current];
            if (!list.Contains(member)) list.Add(member);
        }
    }

    // "public final class a.b.C<T> {" → "a.b.C". 패키지까지 보존해야 서로 다른 C의 멤버가 섞이지 않는다.
    static string JavapTypeName(string line)
    {
        string[] keywords = new string[] { " class ", " interface ", " enum ", " record " };
        int at = -1, skip = 0;
        foreach (string keyword in keywords)
        {
            int found = line.IndexOf(keyword, StringComparison.Ordinal);
            if (found >= 0 && (at < 0 || found < at)) { at = found; skip = keyword.Length; }
        }
        if (at < 0) return null;
        string rest = line.Substring(at + skip).TrimStart();
        int end = rest.Length;
        for (int i = 0; i < rest.Length; i++)
        {
            char c = rest[i];
            if (c == '<' || c == ' ' || c == '{' || c == '\t') { end = i; break; }
        }
        string qualified = rest.Substring(0, end);
        string simple = JavaSimpleName(qualified);
        if (simple.Length == 0 || !char.IsUpper(simple[0])) return null;
        foreach (string part in qualified.Split('.'))
        {
            if (part.Length == 0 || (!char.IsLetter(part[0]) && part[0] != '_' && part[0] != '$')) return null;
            foreach (char c in part) if (!IsJavaIdentifierPart(c)) return null;
        }
        return qualified;
    }

    /* 들여쓴 한 줄에서 멤버 이름. S:/I: 뒤에 메서드는 "이름()", 필드는 "이름".
       생성자(반환형이 없고 이름이 클래스와 같다)는 자동완성에 쓸모가 없지만, 여기서는 이름만 보고
       가릴 수 없으므로 부르는 쪽(JavaMemberTableJson)에서 클래스 이름과 같은 것을 뺀다. */
    static string JavapMemberName(string line)
    {
        string text = line.Trim();
        if (!text.StartsWith("public", StringComparison.Ordinal)) return null;
        if (text.EndsWith("{", StringComparison.Ordinal)) return null;
        int paren = text.IndexOf('(');
        string head = paren >= 0 ? text.Substring(0, paren) : text.TrimEnd(';');
        int end = head.Length;
        while (end > 0 && !IsJavaIdentifierPart(head[end - 1])) end--;
        int start = end;
        while (start > 0 && IsJavaIdentifierPart(head[start - 1])) start--;
        if (end <= start) return null;
        string name = head.Substring(start, end - start);
        if (name.Length == 0 || char.IsDigit(name[0])) return null;
        // 클래스 수신자와 인스턴스 수신자에서 잘못된 항목을 섞지 않도록 static 여부도 함께 싣는다.
        bool isStatic = text.IndexOf(" static ", StringComparison.Ordinal) >= 0;
        return (isStatic ? "S:" : "I:") + name + (paren >= 0 ? "()" : "");
    }

    static bool IsJavaIdentifierPart(char c) { return char.IsLetterOrDigit(c) || c == '_' || c == '$'; }

    static string JavaMemberTableJson(Dictionary<string, List<string>> table, List<string> order)
    {
        StringBuilder sb = new StringBuilder("{\"$schema\":2");
        foreach (string cls in order)
        {
            List<string> list = table[cls];
            List<string> kept = new List<string>();
            foreach (string member in list)
            {
                // 생성자는 제안해도 쓸 데가 없다(점 뒤에 나오지 않는다).
                string bare = member.StartsWith("S:", StringComparison.Ordinal) || member.StartsWith("I:", StringComparison.Ordinal)
                    ? member.Substring(2) : member;
                string simple = JavaSimpleName(cls);
                if (bare == simple || bare == simple + "()") continue;
                kept.Add(member);
            }
            if (kept.Count == 0) continue;
            StringBuilder entry = new StringBuilder();
            if (sb.Length > 1) entry.Append(',');
            entry.Append(JsonString(cls)).Append(':').Append(JsonString(string.Join(" ", kept.ToArray())));
            if (sb.Length + entry.Length > JavaJarMemberJsonLimit) break;   // 상한을 넘으면 거기까지만
            sb.Append(entry);
        }
        return sb.Append('}').ToString();
    }

    static string JavaLibraryRowJson(JavaLibraryTarget target, JavaLibrary catalogItem)
    {
        string file = FindJavaLibraryFile(target.Relative);
        long size = 0;
        try { if (file != null) size = new FileInfo(file).Length; }
        catch { }
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"spec\":").Append(JsonString(target.Spec))
          .Append(",\"id\":").Append(JsonString(target.Id ?? ""))
          .Append(",\"label\":").Append(JsonString(target.Label ?? ""))
          .Append(",\"coordinate\":").Append(JsonString(target.Group + ":" + target.Artifact + ":" + target.Version))
          .Append(",\"installed\":").Append(file != null ? "true" : "false")
          .Append(",\"bundled\":").Append(IsBundledJavaLibrary(target.Relative) ? "true" : "false")
          .Append(",\"size\":").Append(size);
        if (catalogItem != null)
            sb.Append(",\"words\":").Append(JsonString(catalogItem.Words ?? ""))
              .Append(",\"sample\":").Append(JsonString(catalogItem.Sample ?? ""));
        else if (file != null)
        {
            // 카탈로그에 없는 jar 는 손으로 적어 둔 목록이 없다 — jar 안에서 클래스 이름을 읽어 채운다.
            sb.Append(",\"words\":").Append(JsonString(JavaLibraryJarWords(file)))
              .Append(",\"classes\":").Append(JsonString(JavaLibraryJarClasses(file)));
        }
        return sb.Append('}').ToString();
    }

    // 고를 수 있는 목록 = 카탈로그. 설치 여부·크기를 함께 실어 프런트가 한 번의 요청으로 화면을 그리게 한다.
    static string JavaLibraryCatalogJson()
    {
        StringBuilder sb = new StringBuilder("[");
        foreach (JavaLibrary item in JavaLibraryCatalog)
        {
            JavaLibraryTarget target = ParseJavaLibraryTarget(item.Id);
            if (target == null) continue;
            if (sb.Length > 1) sb.Append(',');
            sb.Append(JavaLibraryRowJson(target, item));
        }
        return sb.Append(']').ToString();
    }

    // 캐시에 실제로 있는 것 전부(카탈로그에 없는 직접 좌표 포함). 세 곳을 합쳐 좌표 기준으로 한 번씩만 싣는다.
    static string JavaLibraryListJson()
    {
        List<string> seen = new List<string>();
        StringBuilder sb = new StringBuilder("[");
        string[] roots = new string[] {
            JavaLibraryPortableRoot(), JavaLibraryLocalAppDataRoot(), JavaLibraryVendorRoot() };
        foreach (string root in roots)
            foreach (JavaLibraryTarget target in EnumerateJavaLibraryJars(root))
            {
                if (seen.Contains(target.Relative)) continue;
                seen.Add(target.Relative);
                if (sb.Length > 1) sb.Append(',');
                sb.Append(JavaLibraryRowJson(target, FindJavaLibraryCatalogItem(target.Id)));
            }
        return sb.Append(']').ToString();
    }

    /* Maven Central 이름 검색. 프런트에는 외부 URL을 주지 않고, 런처가 고정된 HTTPS 검색 API만 조회한다.
       검색 인덱스의 latestVersion 은 늦을 수 있으므로 후보를 고른 뒤 JavaLibraryResolveJson 이
       저장소의 maven-metadata.xml 을 다시 읽어 실제 설치 버전을 확정한다. */
    class JavaLibrarySearchItem
    {
        public string Group;
        public string Artifact;
        public string Version;
        public string Packaging;
        public string Label;
        public int VersionCount;
        public int Score;
        public int Order;
        public bool Exact;
        public bool Curated;
    }

    class JavaLibrarySearchCacheEntry
    {
        public string Json;
        public DateTime ExpiresAt;
    }

    static readonly object JavaLibrarySearchCacheLock = new object();
    static readonly Dictionary<string, JavaLibrarySearchCacheEntry> JavaLibrarySearchCache =
        new Dictionary<string, JavaLibrarySearchCacheEntry>(StringComparer.OrdinalIgnoreCase);

    static bool JavaLibrarySafeGroup(string group)
    {
        string value = (group ?? "").Trim();
        if (value.Length == 0 || value.Length > 300 || value.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
        foreach (string part in value.Split('.')) if (!JavaLibrarySafeSegment(part)) return false;
        return true;
    }

    static bool JavaLibrarySafeSearchQuery(string query)
    {
        string value = (query ?? "").Trim();
        if (value.Length < 2 || value.Length > 80) return false;
        bool previousSpace = false;
        foreach (char c in value)
        {
            bool space = c == ' ';
            if (!char.IsLetterOrDigit(c) && !space && c != '_' && c != '-' && c != '.' && c != '+') return false;
            if (space && previousSpace) return false;
            previousSpace = space;
        }
        return true;
    }

    static JavaLibrary FindJavaLibraryCatalogArtifact(string group, string artifact)
    {
        foreach (JavaLibrary item in JavaLibraryCatalog)
            if (string.Equals(item.Group, group, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Artifact, artifact, StringComparison.OrdinalIgnoreCase)) return item;
        return null;
    }

    static string FetchJavaLibraryText(string url, int maxChars)
    {
        return FetchJavaLibraryText(url, maxChars, 15000);
    }

    static string FetchJavaLibraryText(string url, int maxChars, int timeoutMs)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("java-lib-search-address");
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = Math.Max(1000, timeoutMs);
        req.ReadWriteTimeout = Math.Max(5000, timeoutMs);
        req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        req.UserAgent = "ClassDock/1.0 (Java library search)";
        using (WebResponse resp = req.GetResponse())
        using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
        {
            if (resp.ContentLength > maxChars * 4L) throw new InvalidOperationException("java-lib-search-response-too-large");
            StringBuilder sb = new StringBuilder(Math.Min(maxChars, 32768));
            char[] buffer = new char[8192];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (sb.Length + read > maxChars) throw new InvalidOperationException("java-lib-search-response-too-large");
                sb.Append(buffer, 0, read);
            }
            return sb.ToString();
        }
    }

    static XmlDocument ParseJavaLibraryXml(string text)
    {
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.DtdProcessing = DtdProcessing.Prohibit;
        settings.XmlResolver = null;
        XmlDocument doc = new XmlDocument();
        doc.XmlResolver = null;
        using (StringReader input = new StringReader(text ?? ""))
        using (XmlReader reader = XmlReader.Create(input, settings)) doc.Load(reader);
        return doc;
    }

    static string JavaLibraryXmlField(XmlNode node, string tag, string name)
    {
        if (node == null) return "";
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.LocalName != tag || child.Attributes == null) continue;
            XmlAttribute attr = child.Attributes["name"];
            if (attr != null && attr.Value == name) return child.InnerText ?? "";
        }
        return "";
    }

    static string JavaLibrarySearchJson(string rawQuery)
    {
        string query = (rawQuery ?? "").Trim();
        if (!JavaLibrarySafeSearchQuery(query)) throw new InvalidOperationException("invalid-library-search");
        string cacheKey = query.ToLowerInvariant();
        lock (JavaLibrarySearchCacheLock)
        {
            JavaLibrarySearchCacheEntry cached;
            if (JavaLibrarySearchCache.TryGetValue(cacheKey, out cached) && cached.ExpiresAt > DateTime.UtcNow)
                return cached.Json;
            if (cached != null) JavaLibrarySearchCache.Remove(cacheKey);
        }
        string url = "https://search.maven.org/solrsearch/select?q=" + Uri.EscapeDataString(query) + "&rows=12&wt=xml";
        // 추천 항목은 화면에서 먼저 보이므로 추가 후보 조회가 교실 네트워크를 오래 붙잡지 않게 제한한다.
        XmlDocument xml = ParseJavaLibraryXml(FetchJavaLibraryText(url, 1024 * 1024, 8000));
        XmlNodeList docs = xml.SelectNodes("/response/result[@name='response']/doc");
        List<JavaLibrarySearchItem> found = new List<JavaLibrarySearchItem>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int order = 0;
        foreach (XmlNode doc in docs)
        {
            string group = JavaLibraryXmlField(doc, "str", "g");
            string artifact = JavaLibraryXmlField(doc, "str", "a");
            string version = JavaLibraryXmlField(doc, "str", "latestVersion");
            string packaging = JavaLibraryXmlField(doc, "str", "p");
            if (!string.Equals(packaging, "jar", StringComparison.OrdinalIgnoreCase)) continue;
            if (!JavaLibrarySafeGroup(group) || !JavaLibrarySafeSegment(artifact) || !JavaLibrarySafeSegment(version)) continue;
            string key = group + ":" + artifact;
            if (!seen.Add(key)) continue;
            JavaLibrary catalog = FindJavaLibraryCatalogArtifact(group, artifact);
            JavaLibrarySearchItem item = new JavaLibrarySearchItem();
            item.Group = group; item.Artifact = artifact; item.Version = version; item.Packaging = "jar";
            item.Label = catalog != null ? catalog.Label : artifact;
            item.Exact = string.Equals(artifact, query, StringComparison.OrdinalIgnoreCase);
            item.Curated = catalog != null;
            item.Order = order++;
            int.TryParse(JavaLibraryXmlField(doc, "int", "versionCount"), out item.VersionCount);
            item.Score = (item.Curated ? 10000 : 0) + (item.Exact ? 1000 : 0)
                + (artifact.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 250 : 0);
            found.Add(item);
        }
        found.Sort(delegate(JavaLibrarySearchItem a, JavaLibrarySearchItem b)
        {
            int score = b.Score.CompareTo(a.Score);
            return score != 0 ? score : a.Order.CompareTo(b.Order);
        });
        StringBuilder json = new StringBuilder("[");
        foreach (JavaLibrarySearchItem item in found)
        {
            if (json.Length > 1) json.Append(',');
            json.Append("{\"group\":").Append(JsonString(item.Group))
                .Append(",\"artifact\":").Append(JsonString(item.Artifact))
                .Append(",\"version\":").Append(JsonString(item.Version))
                .Append(",\"packaging\":\"jar\",\"label\":").Append(JsonString(item.Label))
                .Append(",\"versionCount\":").Append(item.VersionCount)
                .Append(",\"exact\":").Append(item.Exact ? "true" : "false")
                .Append(",\"curated\":").Append(item.Curated ? "true" : "false").Append('}');
        }
        string result = json.Append(']').ToString();
        lock (JavaLibrarySearchCacheLock)
        {
            if (JavaLibrarySearchCache.Count >= 40) JavaLibrarySearchCache.Clear();
            JavaLibrarySearchCache[cacheKey] = new JavaLibrarySearchCacheEntry {
                Json = result, ExpiresAt = DateTime.UtcNow.AddMinutes(15) };
        }
        return result;
    }

    static string JavaLibraryMetadataVersion(string group, string artifact)
    {
        string baseUrl = JavaLibraryRepository + group.Replace('.', '/') + "/" + artifact + "/";
        XmlDocument xml = ParseJavaLibraryXml(FetchJavaLibraryText(baseUrl + "maven-metadata.xml", 512 * 1024));
        string[] preferred = new string[] {
            xml.SelectSingleNode("/metadata/versioning/release") == null ? "" : xml.SelectSingleNode("/metadata/versioning/release").InnerText,
            xml.SelectSingleNode("/metadata/versioning/latest") == null ? "" : xml.SelectSingleNode("/metadata/versioning/latest").InnerText };
        foreach (string raw in preferred)
        {
            string version = (raw ?? "").Trim();
            if (JavaLibrarySafeSegment(version) && !version.EndsWith("-SNAPSHOT", StringComparison.OrdinalIgnoreCase)) return version;
        }
        XmlNodeList versions = xml.SelectNodes("/metadata/versioning/versions/version");
        for (int i = versions.Count - 1; i >= 0; i--)
        {
            string version = (versions[i].InnerText ?? "").Trim();
            if (JavaLibrarySafeSegment(version) && !version.EndsWith("-SNAPSHOT", StringComparison.OrdinalIgnoreCase)) return version;
        }
        return "";
    }

    static string JavaLibraryPomChild(XmlNode node, string name)
    {
        if (node == null) return "";
        foreach (XmlNode child in node.ChildNodes) if (child.LocalName == name) return (child.InnerText ?? "").Trim();
        return "";
    }

    static void JavaLibraryDependencyInfo(string group, string artifact, string version, out bool known, out int count)
    {
        known = false; count = 0;
        try
        {
            string url = JavaLibraryRepository + group.Replace('.', '/') + "/" + artifact + "/" + version + "/" + artifact + "-" + version + ".pom";
            XmlDocument xml = ParseJavaLibraryXml(FetchJavaLibraryText(url, 1024 * 1024));
            foreach (XmlNode dependency in xml.GetElementsByTagName("dependency"))
            {
                bool managed = false;
                for (XmlNode parent = dependency.ParentNode; parent != null; parent = parent.ParentNode)
                    if (parent.LocalName == "dependencyManagement" || parent.LocalName == "plugin") { managed = true; break; }
                if (managed) continue;
                string scope = JavaLibraryPomChild(dependency, "scope").ToLowerInvariant();
                string optional = JavaLibraryPomChild(dependency, "optional").ToLowerInvariant();
                if (optional == "true" || scope == "test" || scope == "provided" || scope == "system") continue;
                count++;
            }
            known = true;
        }
        catch { known = false; count = 0; }
    }

    static string JavaLibraryResolveJson(string rawGroup, string rawArtifact)
    {
        string group = (rawGroup ?? "").Trim(), artifact = (rawArtifact ?? "").Trim();
        if (!JavaLibrarySafeGroup(group) || !JavaLibrarySafeSegment(artifact))
            throw new InvalidOperationException("invalid-library-spec");
        string version = JavaLibraryMetadataVersion(group, artifact);
        if (!JavaLibrarySafeSegment(version)) throw new InvalidOperationException("java-lib-version-not-found");
        bool dependencyKnown; int dependencyCount;
        JavaLibraryDependencyInfo(group, artifact, version, out dependencyKnown, out dependencyCount);
        return "{\"coordinate\":" + JsonString(group + ":" + artifact + ":" + version)
            + ",\"version\":" + JsonString(version)
            + ",\"dependencyKnown\":" + (dependencyKnown ? "true" : "false")
            + ",\"dependencyCount\":" + dependencyCount + "}";
    }

    /* 설치 시작. 실제 작업은 배경 스레드가 하고 여기서는 작업 번호만 돌려준다 —
       200MB 를 받는 JDK 설치와 달리 몇 초로 끝나는 일이지만, 교실 인터넷에서는 그 몇 초가 몇 분이 된다. */
    static string StartJavaLibraryInstall(byte[] body)
    {
        string spec = Encoding.UTF8.GetString(body ?? new byte[0]).Trim();
        JavaLibraryTarget target = ParseJavaLibraryTarget(spec);
        if (target == null) throw new InvalidOperationException("invalid-library-spec");
        lock (JavaLibJobsLock)
            foreach (JavaLibJob active in JavaLibJobs.Values)
                if (!active.Complete) throw new InvalidOperationException("java-lib-busy");
        SweepJavaLibJobs();
        if (FindJavaLibraryFile(target.Relative) != null) throw new InvalidOperationException("java-lib-exists");
        if (InstalledJavaLibraryCount() >= JavaLibraryMaxInstalled) throw new InvalidOperationException("java-lib-limit");
        string root = JavaLibraryInstallRoot();
        if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("no-install-root");

        JavaLibJob job = new JavaLibJob();
        job.Id = Guid.NewGuid().ToString("N");
        job.Log.AppendLine(target.Label + " · " + target.Group + ":" + target.Artifact + ":" + target.Version);
        lock (JavaLibJobsLock) JavaLibJobs[job.Id] = job;

        JavaLibJob captured = job;
        JavaLibraryTarget capturedTarget = target;
        string capturedRoot = root;
        Thread worker = new Thread(delegate() { RunJavaLibraryInstall(captured, capturedTarget, capturedRoot); });
        worker.IsBackground = true;
        worker.Start();
        return "{\"id\":" + JsonString(job.Id) + "}";
    }

    static void RunJavaLibraryInstall(JavaLibJob job, JavaLibraryTarget target, string root)
    {
        string dest = JavaLibraryFileUnder(root, target.Relative);
        string temp = null;
        try
        {
            if (dest == null) throw new Exception("설치 경로를 만들지 못했습니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            temp = dest + ".part-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            /* 배포본(vendor\java-libs)에 담아 보낸 jar 는 여기로 오지 않는다 — 조회 루트라 이미 쓸 수 있고,
               StartJavaLibraryInstall 이 '이미 있음'으로 돌려보낸다. 인터넷 없는 교실은 그것으로 끝난다. */
            DownloadJavaLibrary(job, target, temp);
            VerifyJavaLibrary(job, target, temp);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(temp, dest);
            temp = null;
            job.Log.AppendLine("설치를 마쳤습니다: " + dest);
            FinishJavaLibJob(job, 0);
        }
        catch (Exception ex)
        {
            bool cancelled;
            lock (job.Sync) cancelled = job.CancelRequested;
            job.Log.AppendLine(cancelled
                ? "[설치를 취소했습니다. 받던 파일은 지웠습니다.]"
                : "[설치 실패: " + FlattenMessage(ex) + "]");
            FinishJavaLibJob(job, -1);
        }
        finally
        {
            try { if (temp != null && File.Exists(temp)) File.Delete(temp); }
            catch { }
            SweepJavaLibJobs();
        }
    }

    static void DownloadJavaLibrary(JavaLibJob job, JavaLibraryTarget target, string temp)
    {
        string url = JavaLibraryRepository + target.Relative.Replace('\\', '/');
        job.Log.AppendLine("내려받는 중: " + url);
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }   // TLS 1.2
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = 30000;
        req.ReadWriteTimeout = 120000;
        req.UserAgent = "ClassDock";
        long received = 0, total = 0, nextMark = 0;
        using (WebResponse resp = req.GetResponse())
        using (Stream rs = resp.GetResponseStream())
        using (FileStream fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
        {
            total = resp.ContentLength;
            if (total > JavaLibraryMaxJarBytes)
                throw new Exception("파일이 " + (JavaLibraryMaxJarBytes / (1024 * 1024)) + "MB 제한보다 큽니다.");
            byte[] buf = new byte[81920];
            int n;
            while ((n = rs.Read(buf, 0, buf.Length)) > 0)
            {
                lock (job.Sync) if (job.CancelRequested) throw new Exception("취소했습니다.");
                received += n;
                // 길이를 알려 주지 않는 응답도 있어 받는 도중에도 상한을 본다.
                if (received > JavaLibraryMaxJarBytes)
                    throw new Exception("파일이 " + (JavaLibraryMaxJarBytes / (1024 * 1024)) + "MB 제한을 넘었습니다.");
                fs.Write(buf, 0, n);
                if (total > 0 && received >= nextMark)
                {
                    job.Log.AppendLine("내려받는 중… " + (received * 100 / total) + "%");
                    nextMark = received + Math.Max(total / 5, 1);
                }
            }
        }
        job.Log.AppendLine("내려받기 완료 (" + (received / 1024) + "KB).");
    }

    /* 받은 파일 대조. 카탈로그에 박아 둔 SHA-256 이 있으면 그것이 기준이고, 없으면 배포처의 .sha1 을 받아 맞춘다.
       기준을 하나도 얻지 못하면 설치를 접는다 — 검증하지 못한 파일을 클래스패스에 올리지 않는다.
       어느 쪽이든 실제 SHA-256 을 로그에 남긴다 — 카탈로그에 박아 둘 값을 여기서 얻는다. */
    static void VerifyJavaLibrary(JavaLibJob job, JavaLibraryTarget target, string file)
    {
        string actual = Sha256File(file);
        if (!string.IsNullOrEmpty(target.Sha256))
        {
            if (!string.Equals(actual, target.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new Exception("받은 파일이 카탈로그에 적힌 검증값과 다릅니다.");
            job.Log.AppendLine("검증 완료 — 카탈로그 SHA-256 과 일치합니다.");
            return;
        }
        string expected = FetchJavaLibraryChecksum(target);
        if (string.IsNullOrEmpty(expected)) throw new Exception("배포처에서 검증값을 얻지 못했습니다.");
        if (!string.Equals(Sha1File(file), expected, StringComparison.OrdinalIgnoreCase))
            throw new Exception("받은 파일이 배포처가 알려준 검증값과 다릅니다.");
        job.Log.AppendLine("검증 완료 — 배포처 SHA-1 과 일치합니다.");
        job.Log.AppendLine("SHA-256: " + actual);
        job.Log.AppendLine("(이 값을 카탈로그에 적어 두면 다음부터는 변조까지 걸러집니다.)");
    }

    static string FetchJavaLibraryChecksum(JavaLibraryTarget target)
    {
        string url = JavaLibraryRepository + target.Relative.Replace('\\', '/') + ".sha1";
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.UserAgent = "ClassDock";
            string text;
            using (WebResponse resp = req.GetResponse())
            using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.ASCII))
                text = reader.ReadToEnd();
            System.Text.RegularExpressions.Match m = Sha1HexRe.Match(text ?? "");
            return m.Success ? m.Value : "";
        }
        catch { return ""; }
    }

    static string Sha1File(string path)
    {
        using (FileStream fs = File.OpenRead(path))
        using (SHA1 sha = SHA1.Create())
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
    }

    static void FinishJavaLibJob(JavaLibJob job, int code)
    {
        lock (job.Sync)
        {
            if (job.Complete) return;
            job.ExitCode = job.CancelRequested ? -1 : code;
            job.DoneAt = DateTime.UtcNow;
            job.Complete = true;
        }
    }

    // 증분 폴링 — pip·npm 설치와 같은 규약(from 이 현재 길이와 같고 진행 중이면 본문 없이 짧게 답한다).
    static string PollJavaLibraryInstall(string id, string knownLen)
    {
        JavaLibJob job;
        lock (JavaLibJobsLock) if (!JavaLibJobs.TryGetValue(id ?? "", out job))
            return "{\"complete\":true,\"code\":-1,\"cancelled\":false,\"log\":"
                 + JsonString("설치 작업을 찾지 못했습니다.") + "}";
        lock (job.Sync)
        {
            int from = 0;
            bool known = int.TryParse(knownLen ?? "", out from) && from >= 0 && from <= job.Log.TextLength;
            if (known && !job.Complete && from == job.Log.TextLength) return "{\"complete\":false,\"unchanged\":true}";
            string head = "{\"complete\":" + (job.Complete ? "true" : "false")
                + ",\"code\":" + job.ExitCode + ",\"cancelled\":" + (job.CancelRequested ? "true" : "false");
            if (known) return head + ",\"logDelta\":" + JsonString(job.Log.GetTextFrom(from)) + "}";
            return head + ",\"log\":" + JsonString(job.Log.GetText()) + "}";
        }
    }

    // 취소는 표시만 한다 — 내려받기 반복문이 다음 조각에서 이 값을 보고 스스로 멈추고 .part 를 지운다.
    static void CancelJavaLibraryInstall(string id)
    {
        JavaLibJob job;
        lock (JavaLibJobsLock) if (!JavaLibJobs.TryGetValue(id ?? "", out job)) return;
        lock (job.Sync)
        {
            if (job.Complete || job.CancelRequested) return;
            job.CancelRequested = true;
        }
    }

    static void SweepJavaLibJobs()
    {
        lock (JavaLibJobsLock)
        {
            List<string> remove = new List<string>();
            DateTime now = DateTime.UtcNow;
            foreach (KeyValuePair<string, JavaLibJob> kv in JavaLibJobs)
                if (kv.Value.Complete && (now - kv.Value.DoneAt).TotalMinutes > 10) remove.Add(kv.Key);
            foreach (string id in remove) JavaLibJobs.Remove(id);
        }
    }

    /* 캐시에서 하나 지운다. 배포본에 담겨 온 것은 지우지 않는다(지워도 다음 실행에 다시 나타난다).
       빈 폴더는 캐시 루트 아래에서만 위로 훑어 정리한다 — com\google\… 껍데기만 남지 않게. */
    static string DeleteJavaLibrary(string spec)
    {
        JavaLibraryTarget target = ParseJavaLibraryTarget(spec);
        if (target == null) return "invalid-library-spec";
        bool removed = false;
        string[] roots = new string[] { JavaLibraryPortableRoot(), JavaLibraryLocalAppDataRoot() };
        foreach (string root in roots)
        {
            string file = JavaLibraryFileUnder(root, target.Relative);
            if (file == null) continue;
            try
            {
                if (!File.Exists(file)) continue;
                File.Delete(file);
                removed = true;
                PruneEmptyJavaLibraryDirectories(Path.GetDirectoryName(file), root);
            }
            catch { }
        }
        if (removed) return "ok";
        return IsBundledJavaLibrary(target.Relative) ? "java-lib-bundled" : "java-lib-not-found";
    }

    static void PruneEmptyJavaLibraryDirectories(string dir, string root)
    {
        try
        {
            string boundary = Path.GetFullPath(root);
            if (!boundary.EndsWith("\\", StringComparison.Ordinal)) boundary += "\\";
            while (!string.IsNullOrEmpty(dir))
            {
                string full = Path.GetFullPath(dir);
                if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) return;
                if (Directory.GetFileSystemEntries(full).Length > 0) return;
                Directory.Delete(full);
                dir = Path.GetDirectoryName(full);
            }
        }
        catch { }
    }

    // ===== 자바(.java) 실행 세션 — 단일 파일 소스 실행 + 대화형 표준입력 =====
    // 자바 실행도 파이썬과 같은 상한을 쓴다. 수업용 코드는 이 안에서 충분하고, 폭주한 코드가 PC 를 멈추는 것만 막는다.
    const long JavaProcessMemoryLimitBytes = PythonProcessMemoryLimitBytes;
    const int JavaSessionTimeoutMs = 30 * 60 * 1000;
    const int JavaCompileTimeoutMs = 30 * 1000;
    // 파일 이름은 public 최상위 타입에 맞추고, 실행 대상은 실제 main 메서드를 가진 최상위 타입으로 고른다.
    // source-file mode(`java Foo.java`)는 무조건 첫 타입을 실행하므로 보조 타입을 앞에 둔 정상 코드가 실패한다.
    const string JavaIdStart = "[\\p{L}\\p{Nl}\\p{Sc}\\p{Pc}]";
    const string JavaIdPart = "[\\p{L}\\p{Nl}\\p{Sc}\\p{Pc}\\p{Mn}\\p{Mc}\\p{Nd}\\p{Cf}]";
    static readonly System.Text.RegularExpressions.Regex JavaTypeRe =
        new System.Text.RegularExpressions.Regex(
            "(?:^|[;}{\\s])(?:(public)\\s+)?(?:(?:final|abstract|sealed|non-sealed|strictfp)\\s+)*(?:class|interface|enum|record)\\s+("
            + JavaIdStart + JavaIdPart + "*)");
    static readonly System.Text.RegularExpressions.Regex JavaMainMethodRe =
        new System.Text.RegularExpressions.Regex(
            "(?=[^;{}]*\\bpublic\\b)(?=[^;{}]*\\bstatic\\b)[^;{}]*\\bvoid\\s+main\\s*\\(\\s*(?:final\\s+)?(?:java\\s*\\.\\s*lang\\s*\\.\\s*)?String\\s*(?:(?:\\[\\s*\\]|\\.\\.\\.)|"
            + JavaIdStart + JavaIdPart + "*\\s*\\[\\s*\\])");
    // 형제 소스를 둘 폴더·파일 이름이 진짜 자바 식별자인지 본다 — .. 나 경로 구분자가 섞여 들어오지 못하게.
    static readonly System.Text.RegularExpressions.Regex JavaIdentifierRe =
        new System.Text.RegularExpressions.Regex("^" + JavaIdStart + JavaIdPart + "*$");
    static readonly System.Text.RegularExpressions.Regex JavaPackageRe =
        new System.Text.RegularExpressions.Regex(
            "(?:^|[;\\s])package\\s+(" + JavaIdStart + JavaIdPart + "*(?:\\s*\\.\\s*" + JavaIdStart + JavaIdPart + "*)*)\\s*;");

    class JavaTypeCandidate
    {
        public string Name;
        public bool IsPublic;
        public bool HasMain;
    }

    /* 실행을 시작하기도 전에 끝난 세션. 컴파일 실패와 같은 모양(프로세스 없는 완료 세션)으로 만들어
       프런트가 평소의 출력 칸에서 붉은 글씨로 보여 주게 한다 — '실행 실패' 화면보다 원인이 눈에 잘 띈다. */
    static string StartJavaMessageSession(string message)
    {
        JavaSession session = new JavaSession();
        session.Id = Guid.NewGuid().ToString("N");
        session.Stderr.AppendLine(message);
        session.ExitCode = -1;
        session.DoneAt = DateTime.UtcNow;
        session.Complete = true;
        lock (JavaSessionsLock) JavaSessions[session.Id] = session;
        return session.Id;
    }


    /* 저장할 때 도는 문법 검사(/java-check). javac 만 돌리고 결과를 그 자리에서 돌려준다 —
       실행 세션과 달리 남는 프로세스가 없으므로 폴링·중지·세션 보관이 필요 없고, 임시 폴더도 바로 지운다.
       컴파일은 실행 경로와 같은 CompileJavaSource 를 쓴다. 여기서 통과한 코드는 실행에서도 컴파일을 지난다. */
    static string RunJavaCheck(byte[] body, string libs, bool lint)
    {
        string java = FindJava();
        if (java == null) throw new JavaMissingException();
        string bin = Path.GetDirectoryName(java);
        string javac = string.IsNullOrEmpty(bin) ? null : Path.Combine(bin, "javac.exe");
        if (string.IsNullOrEmpty(javac) || !File.Exists(javac)) throw new JavaMissingException();

        // 라이브러리가 없으면 검사 자체를 건너뛴다. 없는 jar 때문에 나는 import 오류를
        // 학생 코드의 잘못으로 표시하면 고칠 수 없는 빨간 줄이 남는다.
        List<string> missingLibraries;
        List<string> libraryJars = ResolveJavaLibraryJars(libs, out missingLibraries);
        if (missingLibraries.Count > 0)
            return "{\"ok\":true,\"skipped\":\"libs\",\"output\":\"\",\"mainClass\":\"\"}";

        string source, stdinText;
        List<string> extraSources;
        DecodeRunPayload(body, out source, out stdinText, out extraSources);
        string fileClassName = JavaMainClassName(source);
        string tempRoot = Path.Combine(Path.GetTempPath(), "moidajava_session_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string scriptPath = Path.Combine(tempRoot, fileClassName + ".java");
            File.WriteAllText(scriptPath, source, new UTF8Encoding(false));
            WriteJavaExtraSources(tempRoot, extraSources);   // 같은 폴더의 형제 .java 도 함께 (실행과 같은 묶음)
            // 출력을 담기만 하는 임시 세션. JavaSessions 에 넣지 않으므로 폴링 대상이 되지 않는다.
            JavaSession probe = new JavaSession();
            bool ok = CompileJavaSource(javac, scriptPath, tempRoot, probe, JavaClassPath(tempRoot, libraryJars), lint);
            string output = probe.Stderr.GetText();
            if (output.Length == 0) output = probe.Stdout.GetText();
            return "{\"ok\":" + (ok ? "true" : "false") + ",\"output\":" + JsonString(output)
                + ",\"mainClass\":" + JsonString(fileClassName) + "}";
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    /* piped=false: 대화형 실행. 표준입력을 열어 두고 /java-session-input 으로 한 줄씩 받는다.
       piped=true : 채점처럼 입력을 미리 다 아는 실행. 페이로드의 표준입력을 한 번에 흘려보내고 닫는다.
       두 길을 나누는 이유는 /java-session-input 이 터미널처럼 보이려고 stdout 에 에코를 남기기 때문이다 —
       그 에코가 섞이면 채점의 출력 비교가 어긋난다. */
    static string StartJavaSession(byte[] body, bool piped, string libs, bool lint, string requestedMain, bool junit)
    {
        string java = FindJava();
        if (java == null) throw new JavaMissingException();

        SweepJavaSessions();   // 새 실행 시작 시 오래된 보존 세션의 작업폴더 정리

        // 고른 라이브러리를 먼저 찾는다. 하나라도 없으면 실행하지 않고 그 사실을 출력 칸에 보여 준다.
        List<string> missingLibraries;
        List<string> libraryJars = ResolveJavaLibraryJars(libs, out missingLibraries);
        if (missingLibraries.Count > 0)
            return StartJavaMessageSession("[라이브러리를 찾지 못했습니다: "
                + string.Join(", ", missingLibraries.ToArray())
                + "]\n라이브러리 목록에서 다시 설치하거나, 선택을 해제한 뒤 실행해 주세요.");

        string source, stdinText;
        List<string> extraSources;
        DecodeRunPayload(body, out source, out stdinText, out extraSources);
        string fileClassName = JavaMainClassName(source);
        string launchClassName = JavaLaunchClassName(source, requestedMain);
        string packageName = JavaPackageName(source);
        string qualifiedClassName = string.IsNullOrEmpty(packageName) ? launchClassName : packageName + "." + launchClassName;
        string junitJar = null;
        if (junit)
        {
            foreach (string jar in libraryJars)
            {
                if (Path.GetFileName(jar).StartsWith("junit-platform-console-standalone-", StringComparison.OrdinalIgnoreCase))
                { junitJar = jar; break; }
            }
            if (junitJar == null)
                return StartJavaMessageSession("[JUnit 5 라이브러리가 선택되지 않았습니다.]\n라이브러리에서 JUnit 5를 선택한 뒤 다시 실행해 주세요.");
        }
        string tempRoot = Path.Combine(Path.GetTempPath(), "moidajava_session_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string scriptPath = Path.Combine(tempRoot, fileClassName + ".java");
            File.WriteAllText(scriptPath, source, new UTF8Encoding(false));
            // 검사와 실행이 같은 파일 묶음을 봐야 한다 — 저장 검사만 형제를 알면 "검사는 통과했는데 실행이 안 되는" 짝이 생긴다.
            WriteJavaExtraSources(tempRoot, extraSources);
            return StartJavaSessionProcess(java, scriptPath, fileClassName, qualifiedClassName, tempRoot,
                piped ? (stdinText ?? "") : null, JavaClassPath(tempRoot, libraryJars), lint, junitJar);
        }
        catch
        {
            try { Directory.Delete(tempRoot, true); } catch { }
            throw;
        }
    }

    /* 프런트는 파이썬 실행과 같은 페이로드([길이][소스][길이][표준입력])를 보낸다.
       그 뒤에 [개수]([길이][소스])* 가 더 붙으면 같은 폴더의 형제 .java 본문이다(없으면 옛 모양 그대로).
       경로는 받지 않는다 — 어느 폴더에 둘지는 소스가 스스로 적은 package 와 public 클래스로 정한다.
       프런트가 준 경로를 그대로 쓰면 임시 폴더 밖으로 파일을 쓰는 길이 열린다.
       봉투 모양이 아니면 본문 전체를 소스로 보고 나머지는 비운다. */
    const int JavaExtraSourceMax = 60;              // 함께 받을 형제 파일 개수 상한
    const int JavaExtraSourceBytes = 512 * 1024;    // 형제 파일 하나의 크기 상한

    static void DecodeRunPayload(byte[] body, out string source, out string stdinText, out List<string> extras)
    {
        byte[] raw = body ?? new byte[0];
        var utf8 = new UTF8Encoding(false);
        source = utf8.GetString(raw);
        stdinText = "";
        extras = new List<string>();
        try
        {
            int pos = 0;
            int sourceLen = ReadBundleInt(raw, ref pos);
            if (sourceLen < 0 || pos + sourceLen + 4 > raw.Length) return;
            int sourceAt = pos;
            pos += sourceLen;
            int stdinLen = ReadBundleInt(raw, ref pos);
            if (stdinLen < 0 || pos + stdinLen > raw.Length) return;
            int stdinAt = pos;
            pos += stdinLen;
            List<string> found = new List<string>();
            if (pos != raw.Length)
            {
                int count = ReadBundleInt(raw, ref pos);
                if (count < 0 || count > JavaExtraSourceMax) return;
                for (int i = 0; i < count; i++)
                {
                    int len = ReadBundleInt(raw, ref pos);
                    if (len < 0 || len > JavaExtraSourceBytes || pos + len > raw.Length) return;
                    found.Add(utf8.GetString(raw, pos, len));
                    pos += len;
                }
                if (pos != raw.Length) return;
            }
            source = utf8.GetString(raw, sourceAt, sourceLen);
            stdinText = utf8.GetString(raw, stdinAt, stdinLen);
            extras = found;
        }
        catch { }
    }

    /* 형제 .java 를 실행 임시 폴더에 함께 풀어 둔다. javac 는 -sourcepath 에서 '클래스 이름 = 파일 이름'
       규칙으로 찾으므로, package 를 선언한 파일은 그 package 경로에 놓여야 찾아진다(zoo.Dog → <root>/zoo/Dog.java).
       주 파일을 먼저 쓴 뒤에 부르므로 이미 있는 자리는 건너뛴다 — 형제가 주 파일을 덮어쓸 수 없다.
       참조되지 않는 파일은 javac 가 열어 보지도 않는다. 그래서 한 폴더를 통째로 줘도 값이 싸다. */
    static void WriteJavaExtraSources(string tempRoot, List<string> extras)
    {
        if (extras == null || extras.Count == 0) return;
        var utf8 = new UTF8Encoding(false);
        foreach (string extra in extras)
        {
            if (string.IsNullOrEmpty(extra)) continue;
            string className = JavaDeclaredFileClassName(extra);
            if (className == null || !JavaIdentifierRe.IsMatch(className)) continue;
            string dir = tempRoot;
            string packageName = JavaPackageName(extra);
            if (!string.IsNullOrEmpty(packageName))
            {
                bool bad = false;
                foreach (string part in packageName.Split('.'))
                {
                    if (!JavaIdentifierRe.IsMatch(part)) { bad = true; break; }
                    dir = Path.Combine(dir, part);
                }
                if (bad) continue;
            }
            string path = Path.Combine(dir, className + ".java");
            if (File.Exists(path)) continue;
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, extra, utf8);
            }
            catch { }
        }
    }

    /* 형제 파일의 이름은 그 파일이 실제로 선언한 타입에서만 얻는다.
       JavaMainClassName 은 타입을 못 찾으면 "Main" 으로 떨어지는데(실행할 파일에는 맞는 기본값),
       그대로 쓰면 문법이 깨진 형제가 엉뚱한 이름을 차지한다. */
    static string JavaDeclaredFileClassName(string source)
    {
        List<JavaTypeCandidate> types = JavaTopLevelTypes(source);
        foreach (JavaTypeCandidate type in types) if (type.IsPublic) return type.Name;
        return types.Count > 0 ? types[0].Name : null;
    }

    /* javac 의 파일 이름 규칙을 맞추기 위해 public 최상위 타입을 파일 이름으로 쓴다.
       public 타입이 없으면 main 보유 타입, 첫 최상위 타입 순으로 고른다. */
    static string JavaMainClassName(string source)
    {
        List<JavaTypeCandidate> types = JavaTopLevelTypes(source);
        foreach (JavaTypeCandidate type in types) if (type.IsPublic) return type.Name;
        foreach (JavaTypeCandidate type in types) if (type.HasMain) return type.Name;
        return types.Count > 0 ? types[0].Name : "Main";
    }

    // 실제 실행할 클래스. 보조 클래스를 파일 앞에 선언해도 main 이 든 타입을 명시적으로 실행한다.
    static string JavaLaunchClassName(string source, string requestedMain)
    {
        List<JavaTypeCandidate> types = JavaTopLevelTypes(source);
        if (!string.IsNullOrEmpty(requestedMain))
            foreach (JavaTypeCandidate type in types)
                if (type.HasMain && string.Equals(type.Name, requestedMain, StringComparison.Ordinal)) return type.Name;
        foreach (JavaTypeCandidate type in types) if (type.HasMain) return type.Name;
        foreach (JavaTypeCandidate type in types) if (type.IsPublic) return type.Name;
        return types.Count > 0 ? types[0].Name : "Main";
    }

    static string JavaPackageName(string source)
    {
        string scrubbed = StripJavaCommentsAndStrings(source ?? "");
        System.Text.RegularExpressions.Match m = JavaPackageRe.Match(scrubbed);
        return m.Success ? System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, "\\s+", "") : "";
    }

    static List<JavaTypeCandidate> JavaTopLevelTypes(string source)
    {
        string scrubbed = StripJavaCommentsAndStrings(source ?? "");
        var found = new List<JavaTypeCandidate>();
        int scan = 0, depth = 0;
        foreach (System.Text.RegularExpressions.Match m in JavaTypeRe.Matches(scrubbed))
        {
            while (scan < m.Index)
            {
                if (scrubbed[scan] == '{') depth++;
                else if (scrubbed[scan] == '}') depth = Math.Max(0, depth - 1);
                scan++;
            }
            if (depth != 0) continue;
            int open = scrubbed.IndexOf('{', m.Index + m.Length);
            if (open < 0) continue;
            int close = JavaMatchingBrace(scrubbed, open);
            if (close < 0) close = scrubbed.Length;
            found.Add(new JavaTypeCandidate {
                Name = m.Groups[2].Value,
                IsPublic = m.Groups[1].Success,
                HasMain = JavaTypeHasMain(scrubbed, open, close)
            });
        }
        return found;
    }

    static int JavaMatchingBrace(string source, int open)
    {
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    static bool JavaTypeHasMain(string source, int open, int close)
    {
        foreach (System.Text.RegularExpressions.Match m in JavaMainMethodRe.Matches(source, open + 1))
        {
            if (m.Index >= close) break;
            int depth = 1;
            for (int i = open + 1; i < m.Index; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}') depth--;
            }
            if (depth == 1) return true;
        }
        return false;
    }

    // 주석과 문자열 리터럴을 지운다 — 안내문에 적힌 "class Foo" 같은 글자에 속아 엉뚱한 이름을 고르지 않도록.
    static string StripJavaCommentsAndStrings(string source)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0, n = source.Length;
        while (i < n)
        {
            char c = source[i];
            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                while (i < n && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i = Math.Min(n, i + 2);
                sb.Append(' ');
                continue;
            }
            if (c == '"' && i + 2 < n && source[i + 1] == '"' && source[i + 2] == '"')
            {
                i += 3;                                            // 텍스트 블록 """…"""
                while (i + 2 < n && !(source[i] == '"' && source[i + 1] == '"' && source[i + 2] == '"')) i++;
                i = Math.Min(n, i + 3);
                sb.Append(' ');
                continue;
            }
            if (c == '"' || c == '\'')
            {
                char quote = c;
                i++;
                while (i < n && source[i] != quote)
                {
                    if (source[i] == '\\') i++;
                    i++;
                }
                i = Math.Min(n, i + 1);
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    // 먼저 javac 로 컴파일한 뒤 main 보유 클래스를 명시 실행한다. 보조 타입의 선언 순서에 영향을 받지 않는다.
    static string StartJavaSessionProcess(string java, string scriptPath, string sourceFileClassName,
        string qualifiedClassName, string tempRoot, string pipedStdin, string classPath, bool lint, string junitJar)
    {
        JavaSession session = new JavaSession();
        session.Id = Guid.NewGuid().ToString("N");
        session.TempRoot = tempRoot;
        session.MainClass = sourceFileClassName;

        string bin = Path.GetDirectoryName(java);
        string javac = string.IsNullOrEmpty(bin) ? null : Path.Combine(bin, "javac.exe");
        if (string.IsNullOrEmpty(javac) || !File.Exists(javac)) throw new JavaMissingException();
        if (!CompileJavaSource(javac, scriptPath, tempRoot, session, classPath, lint))
        {
            lock (JavaSessionsLock) JavaSessions[session.Id] = session;
            return session.Id;
        }

        /* 컴파일 결과 디렉터리와 고른 라이브러리 jar 를 classpath 로 주고 탐지한 main 타입을 직접 실행한다.
           file/stdout/stderr 인코딩을 모두 UTF-8로 맞춰 한글 입력·출력이 Windows 코드페이지에 좌우되지 않게 한다. */
        /* 인자는 손으로 따옴표를 붙이지 않고 QuoteProcessArgument 로 감싼다.
           지금까지 새지 않은 이유는 Windows 경로에 따옴표를 못 쓰고, 클래스·패키지 이름은 정규식으로
           뽑은 자바 식별자라 공백이 못 들어가기 때문이다 — 코드 어디에도 적혀 있지 않은 약속이었다.
           (qualifiedClassName 은 아예 따옴표가 없었다.) 파서를 조금만 느슨하게 고치면 그 약속이
           깨지므로, 값이 무엇이든 한 인자로 넘어가도록 인용을 한 곳으로 모은다. */
        string args = "-Dfile.encoding=UTF-8 -Dstdout.encoding=UTF-8 -Dstderr.encoding=UTF-8 ";
        if (string.IsNullOrEmpty(junitJar))
            args += "-cp " + QuoteProcessArgument(classPath) + " " + QuoteProcessArgument(qualifiedClassName);
        else
            args += "-jar " + QuoteProcessArgument(junitJar) + " execute --class-path " + QuoteProcessArgument(classPath)
                + " --scan-class-path --disable-banner --disable-ansi-colors --details=tree";
        ProcessStartInfo psi = new ProcessStartInfo(java, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.WorkingDirectory = tempRoot;

        session.Process = new Process();
        session.Process.StartInfo = psi;
        session.Process.Start();
        Thread outReader = StartLimitedReader(session.Process.StandardOutput, session.Stdout);
        Thread errReader = StartLimitedReader(session.Process.StandardError, session.Stderr);
        lock (JavaSessionsLock) JavaSessions[session.Id] = session;
        Thread watcher = new Thread(delegate()
        {
            bool exited = false;
            bool memoryLimit = false;
            Stopwatch watch = Stopwatch.StartNew();
            while (!exited && watch.ElapsedMilliseconds < JavaSessionTimeoutMs)
            {
                try { exited = session.Process.WaitForExit(500); } catch { break; }
                if (!exited && ProcessTreeWorkingSetBytes(session.Process.Id) > JavaProcessMemoryLimitBytes)
                {
                    memoryLimit = true;
                    break;
                }
            }
            if (!exited)
            {
                session.Stderr.AppendLine(memoryLimit
                    ? "\n[메모리 제한: 실행이 4GB를 넘어 종료했습니다.]"
                    : "\n[시간 초과: 실행을 30분 후 종료했습니다.]");
                KillProcessTree(session.Process);
                try { session.Process.WaitForExit(2000); } catch { }
            }
            try { outReader.Join(2000); errReader.Join(2000); } catch { }
            try { session.ExitCode = session.Process.ExitCode; } catch { session.ExitCode = -1; }
            lock (session.Sync) { session.DoneAt = DateTime.UtcNow; session.Complete = true; }
            SweepJavaSessions();
        });
        watcher.IsBackground = true;
        watcher.Start();

        if (pipedStdin != null)
        {
            // 리더·제한 감시를 먼저 시작하고 별도 스레드에서 입력한다. 큰 입력과 큰 출력이 서로의
            // 파이프를 기다리는 교착이 생겨도 프런트의 중지 요청과 서버 제한 시간이 계속 작동한다.
            Thread inputWriter = new Thread(delegate()
            {
                try
                {
                    byte[] inputBytes = Encoding.UTF8.GetBytes(pipedStdin);
                    if (inputBytes.Length > 0) session.Process.StandardInput.BaseStream.Write(inputBytes, 0, inputBytes.Length);
                    session.Process.StandardInput.BaseStream.Flush();
                }
                catch { }
                try { session.Process.StandardInput.Close(); } catch { }
            });
            inputWriter.IsBackground = true;
            inputWriter.Start();
        }
        return session.Id;
    }

    // 컴파일 출력도 같은 세션 버퍼에 담는다. 실패한 세션은 곧바로 poll 가능한 완료 상태로 돌려준다.
    static bool CompileJavaSource(string javac, string scriptPath, string tempRoot, JavaSession session,
        string classPath, bool lint)
    {
        // 컴파일에도 같은 classpath 를 준다 — 실행에만 주면 라이브러리를 쓰는 import 부터 컴파일이 실패한다.
        // -sourcepath 는 임시 폴더 자신이다. 함께 풀어 둔 형제 .java 를 '참조된 것만' 알아서 같이 컴파일한다
        // (파일 목록으로 다 넘기면 쓰지도 않는 파일의 오류까지 학생 화면에 올라온다).
        string args = "-J-Dfile.encoding=UTF-8 -encoding UTF-8 -cp " + QuoteProcessArgument(classPath) + JavaAnnotationProcessorArgs(classPath)
            + " -sourcepath " + QuoteProcessArgument(tempRoot)
            + " -d " + QuoteProcessArgument(tempRoot) + (lint ? " -Xlint:all" : "") + " " + QuoteProcessArgument(scriptPath);
        ProcessStartInfo psi = new ProcessStartInfo(javac, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.WorkingDirectory = tempRoot;

        Process compiler = new Process();
        compiler.StartInfo = psi;
        compiler.Start();
        Thread outReader = StartLimitedReader(compiler.StandardOutput, session.Stdout);
        Thread errReader = StartLimitedReader(compiler.StandardError, session.Stderr);
        bool exited = false, memoryLimit = false;
        Stopwatch watch = Stopwatch.StartNew();
        while (!exited && watch.ElapsedMilliseconds < JavaCompileTimeoutMs)
        {
            try { exited = compiler.WaitForExit(250); } catch { break; }
            if (!exited && ProcessTreeWorkingSetBytes(compiler.Id) > JavaProcessMemoryLimitBytes)
            {
                memoryLimit = true;
                break;
            }
        }
        if (!exited)
        {
            session.Stderr.AppendLine(memoryLimit
                ? "\n[메모리 제한: 컴파일이 4GB를 넘어 종료했습니다.]"
                : "\n[시간 초과: 컴파일을 30초 후 종료했습니다.]");
            KillProcessTree(compiler);
            try { compiler.WaitForExit(2000); } catch { }
        }
        try { outReader.Join(2000); errReader.Join(2000); } catch { }
        int code;
        try { code = compiler.ExitCode; } catch { code = -1; }
        if (exited && code == 0) return true;
        lock (session.Sync)
        {
            session.ExitCode = code;
            session.DoneAt = DateTime.UtcNow;
            session.Complete = true;
        }
        return false;
    }

    // 증분 폴링 — 파이썬 세션과 같은 계약(so/se 오프셋을 보내면 변화 없을 때 짧게, 자랐으면 새 내용만).
    static string PollJavaSession(string id, string knownOut, string knownErr)
    {
        JavaSession session;
        lock (JavaSessionsLock) if (!JavaSessions.TryGetValue(id ?? "", out session))
            return "{\"complete\":true,\"code\":-1,\"stdout\":\"\",\"stderr\":\"세션을 찾지 못했습니다.\",\"echoes\":[]}";
        lock (session.Sync)
        {
            int so = 0, se = 0;
            bool known = int.TryParse(knownOut ?? "", out so) && int.TryParse(knownErr ?? "", out se)
                && so >= 0 && se >= 0
                && so <= session.Stdout.TextLength && se <= session.Stderr.TextLength;
            if (known && !session.Complete
                && so == session.Stdout.TextLength && se == session.Stderr.TextLength)
                return "{\"complete\":false,\"unchanged\":true}";
            if (known)
                return "{\"complete\":" + (session.Complete ? "true" : "false")
                     + ",\"code\":" + session.ExitCode
                     + ",\"stdoutDelta\":" + JsonString(session.Stdout.GetTextFrom(so))
                     + ",\"stderrDelta\":" + JsonString(session.Stderr.GetTextFrom(se))
                     + ",\"echoes\":" + BuildEchoesJson(session.Echoes)
                     + ",\"mainClass\":" + JsonString(session.MainClass) + "}";
            return "{\"complete\":" + (session.Complete ? "true" : "false")
                 + ",\"code\":" + session.ExitCode
                 + ",\"stdout\":" + JsonString(session.Stdout.GetText())
                 + ",\"stderr\":" + JsonString(session.Stderr.GetText())
                 + ",\"echoes\":" + BuildEchoesJson(session.Echoes)
                 + ",\"mainClass\":" + JsonString(session.MainClass) + "}";
        }
    }

    static void SendJavaSessionInput(string id, string input)
    {
        JavaSession session;
        lock (JavaSessionsLock) if (!JavaSessions.TryGetValue(id ?? "", out session)) throw new Exception("session-not-found");
        lock (session.Sync)
        {
            if (session.Complete) throw new Exception("session-complete");
            byte[] bytes = Encoding.UTF8.GetBytes((input ?? "") + "\n");
            // 파이프 stdin 은 에코되지 않으므로 터미널처럼 직접 표시한다. 파이썬 쪽과 같은 이유로
            // 반드시 stdin 에 쓰기 "전에" 에코를 넣는다 — 먼저 쓰면 자바가 곧바로 다음 출력을 내보내
            // 리더 스레드가 그것을 에코보다 먼저 담아 순서가 뒤섞인다.
            int echoStart = session.Stdout.TextLength;
            session.Stdout.AppendLine(input ?? "");
            int echoLen = (input ?? "").Length;
            if (echoLen > 0 && session.Stdout.TextLength == echoStart + echoLen + Environment.NewLine.Length)
                session.Echoes.Add(new int[] { echoStart, echoLen });
            session.Process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
            session.Process.StandardInput.BaseStream.Flush();
        }
    }

    // 표준입력을 닫아 Scanner 의 hasNext() 루프를 정상 종료시킨다(중지와 달리 코드가 스스로 끝날 기회를 준다).
    static void CloseJavaSessionInput(string id)
    {
        JavaSession session;
        lock (JavaSessionsLock) if (!JavaSessions.TryGetValue(id ?? "", out session)) return;
        lock (session.Sync)
        {
            if (session.Complete) return;
            try { session.Process.StandardInput.Close(); } catch { }
        }
    }

    static void StopJavaSession(string id)
    {
        JavaSession session = null;
        lock (JavaSessionsLock) JavaSessions.TryGetValue(id ?? "", out session);
        if (session == null) return;
        KillProcessTree(session.Process);
        // 프로세스를 죽이면 watcher 가 곧 완료 처리한다. 맵·작업폴더 정리는 SweepJavaSessions 가 맡는다.
    }

    // 끝난 세션은 잠깐 남겨 두고(마지막 폴이 결과를 받아갈 수 있게) 오래된 것부터 지운다.
    static void SweepJavaSessions()
    {
        List<JavaSession> toDelete = new List<JavaSession>();
        lock (JavaSessionsLock)
        {
            List<JavaSession> done = new List<JavaSession>();
            foreach (KeyValuePair<string, JavaSession> kv in JavaSessions) if (kv.Value.Complete) done.Add(kv.Value);
            DateTime now = DateTime.UtcNow;
            foreach (JavaSession s in done) if ((now - s.DoneAt).TotalMinutes > 30) toDelete.Add(s);
            done.Sort(delegate(JavaSession a, JavaSession b) { return a.DoneAt.CompareTo(b.DoneAt); });
            for (int i = 0; i < done.Count - 6; i++) if (!toDelete.Contains(done[i])) toDelete.Add(done[i]);
            foreach (JavaSession s in toDelete) JavaSessions.Remove(s.Id);
        }
        foreach (JavaSession s in toDelete) CleanupJavaSessionFiles(s);
    }

    static void CleanupJavaSessionFiles(JavaSession session)
    {
        if (session == null) return;
        try { if (!string.IsNullOrEmpty(session.TempRoot) && Directory.Exists(session.TempRoot)) Directory.Delete(session.TempRoot, true); }
        catch { }
    }

    // pip 패키지 이름 검증(명령 주입 방지): 이름 + 선택적 버전 지정자만 허용
    static string RunPyOutput(string interp, string args, int timeoutMs)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(interp, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            Process p = Process.Start(psi);
            if (p == null) return "";
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return ""; }
            return (stdout + (stdout.Length > 0 && stderr.Length > 0 ? "\n" : "") + stderr).Trim();
        }
        catch { return ""; }
    }

    // 이 앱(호스트)과 그 자식 프로세스(파이썬 커널·chromedriver 등) 물리 메모리(WorkingSet) 합계를 JSON 으로.
    static string MemoryStatsJson()
    {
        try
        {
            int selfId = Process.GetCurrentProcess().Id;
            var parent = new Dictionary<int, int>();
            var pname = new Dictionary<int, string>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap != IntPtr.Zero && snap.ToInt64() != -1)
            {
                try
                {
                    PROCESSENTRY32 pe = new PROCESSENTRY32();
                    pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                    if (Process32First(snap, ref pe))
                    {
                        do
                        {
                            int pid = (int)pe.th32ProcessID;
                            parent[pid] = (int)pe.th32ParentProcessID;
                            string nm = pe.szExeFile ?? "";
                            if (nm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) nm = nm.Substring(0, nm.Length - 4);
                            pname[pid] = nm;
                        } while (Process32Next(snap, ref pe));
                    }
                }
                finally { CloseHandle(snap); }
            }
            // 자기 자신 + 모든 후손 PID 수집
            var tree = new HashSet<int>();
            tree.Add(selfId);
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var kv in parent)
                    if (tree.Contains(kv.Value) && !tree.Contains(kv.Key)) { tree.Add(kv.Key); changed = true; }
            }
            var rows = new List<KeyValuePair<string, long>>();
            long total = 0;
            foreach (int pid in tree)
            {
                long ws;
                string nm = pname.ContainsKey(pid) ? pname[pid] : "?";
                try { using (var p = Process.GetProcessById(pid)) { ws = p.WorkingSet64; } }
                catch { continue; }   // 이미 종료된 프로세스는 건너뜀
                total += ws;
                rows.Add(new KeyValuePair<string, long>(nm, ws));
            }
            rows.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"totalMB\":").Append(total / (1024 * 1024)).Append(",\"processes\":[");
            for (int i = 0; i < rows.Count && i < 12; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"name\":").Append(JsonString(rows[i].Key)).Append(",\"mb\":").Append(rows[i].Value / (1024 * 1024)).Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }
        catch (Exception ex) { return "{\"ok\":false,\"reason\":" + JsonString(FlattenMessage(ex)) + "}"; }
    }

    static string PythonDiagnostics()
    {
        string interp = FindPython();
        if (interp == null)
        {
            return "{\"ok\":false,\"command\":\"\",\"version\":\"\",\"pip\":false,\"jedi\":false,\"saveRoot\":" + JsonString(CurrentSaveRoot()) + "}";
        }
        string prefix = interp == "py" ? "-3 " : "";
        string version = RunPyOutput(interp, prefix + "--version", 5000);
        bool pip = RunPyCheck(interp, "import pip");
        bool jedi = RunPyCheck(interp, "import jedi");
        return "{\"ok\":true"
             + ",\"command\":" + JsonString(interp)
             + ",\"version\":" + JsonString(version)
             + ",\"pip\":" + (pip ? "true" : "false")
             + ",\"jedi\":" + (jedi ? "true" : "false")
             + ",\"saveRoot\":" + JsonString(CurrentSaveRoot())
             + "}";
    }

    static readonly object SqlitePreviewLock = new object();
    static string _sqlitePreviewRunnerPath = null;

    static string SqlitePreviewRunner()
    {
        lock (SqlitePreviewLock)
        {
            if (_sqlitePreviewRunnerPath != null && File.Exists(_sqlitePreviewRunnerPath)) return _sqlitePreviewRunnerPath;
            string path = Path.Combine(Path.GetTempPath(), "moida_sqlite_preview.py");
            File.WriteAllText(path,
                "import sys, json, sqlite3\n" +
                "db = sys.argv[1]\n" +
                "def qid(value): return '\"' + str(value).replace('\"', '\"\"') + '\"'\n" +
                "def cell(value):\n" +
                "    if value is None: return None\n" +
                "    if isinstance(value, (bytes, bytearray, memoryview)): return '<BLOB %d bytes>' % len(value)\n" +
                "    text = str(value)\n" +
                "    return text if len(text) <= 500 else text[:500] + '…'\n" +
                "result = {'ok': True, 'limit': 200, 'tables': []}\n" +
                "try:\n" +
                "    uri = 'file:' + db.replace('\\\\', '/') + '?mode=ro'\n" +
                "    con = sqlite3.connect(uri, uri=True)\n" +
                "    con.execute('PRAGMA query_only=ON')\n" +
                "    masters = con.execute(\"SELECT name, type, sql FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY type, name\").fetchall()\n" +
                "    result['totalTables'] = len(masters)\n" +
                "    remaining_cells = 12000\n" +
                "    for name, kind, sql in masters[:60]:\n" +
                "        item = {'name': name, 'type': kind, 'sql': (sql or '')[:4000], 'columns': [], 'rows': [], 'rowCount': None}\n" +
                "        try:\n" +
                "            info = con.execute('PRAGMA table_info(' + qid(name) + ')').fetchall()\n" +
                "            item['columns'] = [{'name': r[1], 'type': r[2] or '', 'notnull': bool(r[3]), 'default': r[4], 'pk': int(r[5] or 0)} for r in info[:80]]\n" +
                "            item['rowCount'] = int(con.execute('SELECT COUNT(*) FROM ' + qid(name)).fetchone()[0])\n" +
                "            width = max(1, min(len(info), 80))\n" +
                "            row_limit = min(200, remaining_cells // width)\n" +
                "            cur = con.execute('SELECT * FROM ' + qid(name) + ' LIMIT ' + str(row_limit))\n" +
                "            item['displayColumns'] = [d[0] for d in (cur.description or [])[:80]]\n" +
                "            item['rows'] = [[cell(v) for v in row[:80]] for row in cur.fetchall()]\n" +
                "            remaining_cells = max(0, remaining_cells - len(item['rows']) * max(1, len(item['displayColumns'])))\n" +
                "        except Exception as exc:\n" +
                "            item['error'] = str(exc)\n" +
                "        result['tables'].append(item)\n" +
                "    con.close()\n" +
                "except Exception as exc:\n" +
                "    result = {'ok': False, 'error': str(exc), 'tables': []}\n" +
                "print(json.dumps(result, ensure_ascii=False))\n",
                new UTF8Encoding(false));
            _sqlitePreviewRunnerPath = path;
            return path;
        }
    }

    static string SqlitePreview(byte[] body)
    {
        byte[] signature = Encoding.ASCII.GetBytes("SQLite format 3\0");
        if (body == null || body.Length < signature.Length) throw new InvalidDataException("not-sqlite3");
        for (int i = 0; i < signature.Length; i++) if (body[i] != signature[i]) throw new InvalidDataException("not-sqlite3");

        string interp = FindPython();
        if (interp == null) throw new PythonMissingException();
        string tempDir = Path.Combine(Path.GetTempPath(), "moida_sqlite_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "preview.sqlite3");
        File.WriteAllBytes(dbPath, body);
        try
        {
            string runner = SqlitePreviewRunner();
            string args = (interp == "py" ? "-3 " : "") + "\"" + runner + "\" \"" + dbPath + "\"";
            return RunSqliteRunner(interp, args, null);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    static string _sqliteExecRunnerPath = null;
    static readonly object SqliteExecLock = new object();

    class SqliteProcessCapture
    {
        public readonly StringBuilder Text = new StringBuilder();
        public bool TooLarge;
        public Exception Error;
    }

    static string SqliteExecRunner()
    {
        lock (SqlitePreviewLock)
        {
            if (_sqliteExecRunnerPath != null && File.Exists(_sqliteExecRunnerPath)) return _sqliteExecRunnerPath;
            string path = Path.Combine(Path.GetTempPath(), "moida_sqlite_exec.py");
            File.WriteAllText(path,
                "import sys, json, sqlite3, hashlib, os\n" +
                "db = sys.argv[1]\n" +
                "mode = sys.argv[2] if len(sys.argv) > 2 else 'exec'\n" +
                "backup = sys.argv[3] if len(sys.argv) > 3 else ''\n" +
                "sql = sys.stdin.read() if mode == 'exec' else ''\n" +
                "def qid(value): return '\"' + str(value).replace('\"', '\"\"') + '\"'\n" +
                "def fingerprint(path, include_wal=True):\n" +
                "    digest = hashlib.sha256()\n" +
                "    paths = [(path, b'')]\n" +
                "    if include_wal and os.path.exists(path + '-wal'): paths.append((path + '-wal', b'\\0wal\\0'))\n" +
                "    for current, marker in paths:\n" +
                "        if marker: digest.update(marker)\n" +
                "        with open(current, 'rb') as source:\n" +
                "            while True:\n" +
                "                chunk = source.read(1024 * 1024)\n" +
                "                if not chunk: break\n" +
                "                digest.update(chunk)\n" +
                "    return digest.hexdigest()\n" +
                "def cell(value):\n" +
                "    if value is None: return None\n" +
                "    if isinstance(value, (bytes, bytearray, memoryview)): return '<BLOB %d bytes>' % len(value)\n" +
                "    text = str(value)\n" +
                "    return text if len(text) <= 500 else text[:500] + '…'\n" +
                "def snapshot(con):\n" +
                "    masters = con.execute(\"SELECT name, type, sql FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY type, name\").fetchall()\n" +
                "    tables, remaining_cells = [], 12000\n" +
                "    for name, kind, tsql in masters[:60]:\n" +
                "        item = {'name': name, 'type': kind, 'sql': (tsql or '')[:4000], 'columns': [], 'rows': [], 'rowCount': None}\n" +
                "        try:\n" +
                "            info = con.execute('PRAGMA table_info(' + qid(name) + ')').fetchall()\n" +
                "            item['columns'] = [{'name': r[1], 'type': r[2] or '', 'notnull': bool(r[3]), 'default': r[4], 'pk': int(r[5] or 0)} for r in info[:80]]\n" +
                "            item['rowCount'] = int(con.execute('SELECT COUNT(*) FROM ' + qid(name)).fetchone()[0])\n" +
                "            width = max(1, min(len(info), 80))\n" +
                "            row_limit = min(200, remaining_cells // width)\n" +
                "            cur = con.execute('SELECT * FROM ' + qid(name) + ' LIMIT ' + str(row_limit))\n" +
                "            item['displayColumns'] = [d[0] for d in (cur.description or [])[:80]]\n" +
                "            item['rows'] = [[cell(v) for v in row[:80]] for row in cur.fetchall()]\n" +
                "            remaining_cells = max(0, remaining_cells - len(item['rows']) * max(1, len(item['displayColumns'])))\n" +
                "        except Exception as exc:\n" +
                "            item['error'] = str(exc)\n" +
                "        tables.append(item)\n" +
                "    return tables, len(masters)\n" +
                "def statements(script):\n" +
                "    parts, buf = [], ''\n" +
                "    for ch in script:\n" +
                "        buf += ch\n" +
                "        if ch == ';' and sqlite3.complete_statement(buf):\n" +
                "            if buf.strip(): parts.append(buf)\n" +
                "            buf = ''\n" +
                "    if buf.strip(): parts.append(buf)\n" +
                "    return parts\n" +
                "def first_keyword(statement):\n" +
                "    text = statement.lstrip()\n" +
                "    while True:\n" +
                "        if text.startswith('--'):\n" +
                "            pos = text.find('\\n')\n" +
                "            text = '' if pos < 0 else text[pos + 1:].lstrip()\n" +
                "            continue\n" +
                "        if text.startswith('/*'):\n" +
                "            pos = text.find('*/', 2)\n" +
                "            text = '' if pos < 0 else text[pos + 2:].lstrip()\n" +
                "            continue\n" +
                "        break\n" +
                "    return ''.join(ch for ch in text.split(None, 1)[0] if ch.isalpha()).upper() if text else ''\n" +
                "result, con, committed = {'ok': True}, None, False\n" +
                "try:\n" +
                "    con = sqlite3.connect(db)\n" +
                "    con.execute('PRAGMA busy_timeout=4000')\n" +
                "    if mode == 'preview':\n" +
                "        con.execute('PRAGMA query_only=ON')\n" +
                "        result = {'ok': True, 'limit': 200, 'tables': []}\n" +
                "        result['tables'], result['totalTables'] = snapshot(con)\n" +
                "    else:\n" +
                "        parts = statements(sql)\n" +
                "        if not parts: raise ValueError('empty-sql')\n" +
                "        if not backup: raise ValueError('missing-backup-path')\n" +
                "        backup_con = sqlite3.connect(backup)\n" +
                "        try: con.backup(backup_con)\n" +
                "        finally: backup_con.close()\n" +
                "        changes_before = con.total_changes\n" +
                "        con.execute('BEGIN IMMEDIATE')\n" +
                "        denied = (sqlite3.SQLITE_ATTACH, sqlite3.SQLITE_DETACH, sqlite3.SQLITE_TRANSACTION)\n" +
                "        con.set_authorizer(lambda action, p1, p2, dbname, source: sqlite3.SQLITE_DENY if action in denied else sqlite3.SQLITE_OK)\n" +
                "        cur = con.cursor()\n" +
                "        try:\n" +
                "            for statement in parts: cur.execute(statement)\n" +
                "            if len(parts) == 1 and cur.description:\n" +
                "                cols = [d[0] for d in cur.description[:40]]\n" +
                "                data = cur.fetchmany(501)\n" +
                "                exec_info = {'kind': 'select', 'columns': cols, 'rows': [[cell(v) for v in row[:40]] for row in data[:500]], 'truncated': len(data) > 500, 'rowCount': min(len(data), 500)}\n" +
                "            elif len(parts) == 1:\n" +
                "                exec_info = {'kind': 'write', 'rowcount': cur.rowcount, 'lastrowid': cur.lastrowid}\n" +
                "            else:\n" +
                "                exec_info = {'kind': 'script', 'changes': con.total_changes - changes_before}\n" +
                "            con.set_authorizer(lambda action, p1, p2, dbname, source: sqlite3.SQLITE_OK)\n" +
                "            con.commit()\n" +
                "            committed = True\n" +
                "        except Exception:\n" +
                "            con.set_authorizer(lambda action, p1, p2, dbname, source: sqlite3.SQLITE_OK)\n" +
                "            con.rollback()\n" +
                "            try: os.remove(backup)\n" +
                "            except OSError: pass\n" +
                "            raise\n" +
                "        read_only = len(parts) == 1 and first_keyword(parts[0]) in ('SELECT', 'EXPLAIN', 'VALUES')\n" +
                "        if read_only:\n" +
                "            try: os.remove(backup)\n" +
                "            except OSError: pass\n" +
                "        else:\n" +
                "            exec_info['backup'] = os.path.basename(backup)\n" +
                "        result = {'ok': True, 'exec': exec_info}\n" +
                "except Exception as exc:\n" +
                "    if mode == 'exec' and backup and not committed:\n" +
                "        try:\n" +
                "            if con is not None and con.in_transaction: con.rollback()\n" +
                "        except Exception: pass\n" +
                "        try: os.remove(backup)\n" +
                "        except OSError: pass\n" +
                "    result = {'ok': False, 'error': str(exc)}\n" +
                "finally:\n" +
                "    if con is not None:\n" +
                "        try: con.close()\n" +
                "        except Exception: pass\n" +
                "if result.get('ok'):\n" +
                "    try: result['fingerprint'] = fingerprint(db)\n" +
                "    except Exception: result['fingerprint'] = ''\n" +
                "print(json.dumps(result, ensure_ascii=False))\n",
                new UTF8Encoding(false));
            _sqliteExecRunnerPath = path;
            return path;
        }
    }

    // SQL 쓰기는 저장 루트 아래의 명시적 상대경로만 허용한다. 절대경로는 로컬 PC의 임의 DB를
    // 수정할 수 있으므로 읽기용 TryReadLocalFile 과 달리 허용하지 않는다.
    static bool TryResolveDbPath(string path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return false;
        try
        {
            if (!TryResolveSaveRootPath(path, out full)) return false;
        }
        catch { return false; }
        if (!File.Exists(full)) return false;
        string ext = Path.GetExtension(full).ToLowerInvariant();
        return ext == ".db" || ext == ".sqlite" || ext == ".sqlite3";
    }

    static void ValidateSqliteHeader(string full)
    {
        byte[] signature = Encoding.ASCII.GetBytes("SQLite format 3\0");
        using (FileStream fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            byte[] headBytes = new byte[signature.Length];
            int read = fs.Read(headBytes, 0, headBytes.Length);
            if (read < signature.Length) throw new InvalidDataException("not-sqlite3");
            for (int i = 0; i < signature.Length; i++)
                if (headBytes[i] != signature[i]) throw new InvalidDataException("not-sqlite3");
        }
    }

    static void AppendHashFile(HashAlgorithm hash, string path)
    {
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                hash.TransformBlock(buffer, 0, read, buffer, 0);
        }
    }

    static string DbFingerprint(string full, bool includeWal)
    {
        using (SHA256 sha = SHA256.Create())
        {
            AppendHashFile(sha, full);
            string wal = full + "-wal";
            if (includeWal && File.Exists(wal))
            {
                byte[] marker = Encoding.ASCII.GetBytes("\0wal\0");
                sha.TransformBlock(marker, 0, marker.Length, marker, 0);
                AppendHashFile(sha, wal);
            }
            sha.TransformFinalBlock(new byte[0], 0, 0);
            StringBuilder text = new StringBuilder(sha.Hash.Length * 2);
            foreach (byte value in sha.Hash) text.Append(value.ToString("x2"));
            return text.ToString();
        }
    }

    static void ValidateDbFingerprint(Dictionary<string, string> headers, string full, bool required, bool includeWal)
    {
        string expected = headers != null && headers.ContainsKey("X-Db-Fingerprint")
            ? headers["X-Db-Fingerprint"].Trim().ToLowerInvariant() : "";
        if (required && expected.Length != 64) throw new DbMismatchException();
        if (expected.Length > 0 && !string.Equals(expected, DbFingerprint(full, includeWal), StringComparison.Ordinal))
            throw new DbMismatchException();
    }

    static string NextDbBackupPath(string full)
    {
        string prefix = full + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string candidate = prefix;
        int suffix = 1;
        while (File.Exists(candidate)) candidate = prefix + "-" + (suffix++);
        return candidate;
    }

    static void CaptureProcessText(StreamReader reader, int limit, SqliteProcessCapture capture)
    {
        try
        {
            char[] buffer = new char[4096];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                int remaining = limit - capture.Text.Length;
                if (remaining > 0) capture.Text.Append(buffer, 0, Math.Min(read, remaining));
                if (read > remaining) capture.TooLarge = true;
            }
        }
        catch (Exception ex) { capture.Error = ex; }
    }

    static string RunSqliteRunner(string interp, string args, string stdin)
    {
        ProcessStartInfo psi = new ProcessStartInfo(interp, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        using (Process proc = Process.Start(psi))
        {
            if (proc == null) throw new Exception("sqlite-process-spawn-failed");
            SqliteProcessCapture stdout = new SqliteProcessCapture();
            SqliteProcessCapture stderr = new SqliteProcessCapture();
            Thread stdoutThread = new Thread(delegate() { CaptureProcessText(proc.StandardOutput, 12 * 1024 * 1024, stdout); });
            Thread stderrThread = new Thread(delegate() { CaptureProcessText(proc.StandardError, 1024 * 1024, stderr); });
            stdoutThread.IsBackground = true;
            stderrThread.IsBackground = true;
            stdoutThread.Start();
            stderrThread.Start();
            try
            {
                using (StreamWriter input = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)))
                {
                    if (!string.IsNullOrEmpty(stdin)) input.Write(stdin);
                }
                if (!proc.WaitForExit(30000))
                {
                    try { proc.Kill(); } catch { }
                    try { proc.WaitForExit(5000); } catch { }
                    throw new Exception("sqlite-process-timeout");
                }
            }
            finally
            {
                stdoutThread.Join(5000);
                stderrThread.Join(5000);
            }
            if (stdout.Error != null) throw stdout.Error;
            if (stderr.Error != null) throw stderr.Error;
            if (stdout.TooLarge) throw new Exception("sqlite-result-too-large");
            if (proc.ExitCode != 0)
                throw new Exception(string.IsNullOrWhiteSpace(stderr.Text.ToString()) ? "sqlite-process-failed" : stderr.Text.ToString().Trim());
            return stdout.Text.ToString().Trim();
        }
    }

    static string SqliteDiskPreview(Dictionary<string, string> headers)
    {
        string rawPath = headers != null && headers.ContainsKey("X-Db-Path") ? Uri.UnescapeDataString(headers["X-Db-Path"]) : "";
        string full;
        if (!TryResolveDbPath(rawPath, out full)) throw new FileNotFoundException("db-not-found");
        ValidateSqliteHeader(full);
        ValidateDbFingerprint(headers, full, false, false);
        string interp = FindPython();
        if (interp == null) throw new PythonMissingException();
        string runner = SqliteExecRunner();
        string args = (interp == "py" ? "-3 " : "") + "\"" + runner + "\" \"" + full + "\" preview";
        return RunSqliteRunner(interp, args, null);
    }

    static string SqliteExec(Dictionary<string, string> headers, byte[] body)
    {
        string rawPath = headers != null && headers.ContainsKey("X-Db-Path") ? Uri.UnescapeDataString(headers["X-Db-Path"]) : "";
        string full;
        if (!TryResolveDbPath(rawPath, out full)) throw new FileNotFoundException("db-not-found");
        ValidateSqliteHeader(full);
        string sql = Encoding.UTF8.GetString(body ?? new byte[0]);
        if (string.IsNullOrWhiteSpace(sql)) throw new Exception("empty-sql");
        string interp = FindPython();
        if (interp == null) throw new PythonMissingException();
        lock (SqliteExecLock)
        {
            // 화면을 연 뒤 같은 경로가 다른 파일로 교체됐으면 실행하지 않는다.
            ValidateDbFingerprint(headers, full, true, true);
            string backup = NextDbBackupPath(full);
            string runner = SqliteExecRunner();
            string args = (interp == "py" ? "-3 " : "") + "\"" + runner + "\" \"" + full + "\" exec \"" + backup + "\"";
            return RunSqliteRunner(interp, args, sql);
        }
    }

    static readonly System.Text.RegularExpressions.Regex NpmPackageNameRe =
        new System.Text.RegularExpressions.Regex(@"^(?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$");
    static readonly System.Text.RegularExpressions.Regex NpmVersionRe =
        new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9][A-Za-z0-9._+~-]*$");
    static readonly System.Text.RegularExpressions.Regex JsGlobalNameRe =
        new System.Text.RegularExpressions.Regex(@"^[A-Za-z_$][A-Za-z0-9_$]*$");

    static bool IsSafeNpmId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 32) return false;
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }

    static string NpmPackageId(string spec, string globalName)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(spec + "\n" + globalName);
        byte[] hash;
        using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(bytes);
        StringBuilder result = new StringBuilder(32);
        for (int i = 0; i < 16; i++) result.Append(hash[i].ToString("x2"));
        return result.ToString();
    }

    static string[] ParseNpmInstallRequest(byte[] body)
    {
        string text = Encoding.UTF8.GetString(body ?? new byte[0]).Replace("\r", "");
        string[] lines = text.Split('\n');
        string spec = lines.Length > 0 ? lines[0].Trim() : "";
        string globalName = lines.Length > 1 ? lines[1].Trim() : "";
        if (spec.Length == 0 || spec.Length > 160) throw new InvalidDataException("invalid-package-spec");
        if (!JsGlobalNameRe.IsMatch(globalName) || globalName.Length > 80) throw new InvalidDataException("invalid-global-name");

        string packageName = spec;
        string version = "";
        if (spec.StartsWith("@", StringComparison.Ordinal))
        {
            int slash = spec.IndexOf('/');
            if (slash < 2) throw new InvalidDataException("invalid-package-spec");
            int versionAt = spec.IndexOf('@', slash + 1);
            if (versionAt >= 0)
            {
                packageName = spec.Substring(0, versionAt);
                version = spec.Substring(versionAt + 1);
            }
        }
        else
        {
            int versionAt = spec.IndexOf('@');
            if (versionAt >= 0)
            {
                packageName = spec.Substring(0, versionAt);
                version = spec.Substring(versionAt + 1);
            }
        }
        if (!NpmPackageNameRe.IsMatch(packageName) || packageName.Length > 120)
            throw new InvalidDataException("invalid-package-spec");
        if (version.Length > 0 && (!NpmVersionRe.IsMatch(version) || version.Length > 80))
            throw new InvalidDataException("invalid-package-version");
        if (spec.EndsWith("@", StringComparison.Ordinal)) throw new InvalidDataException("invalid-package-version");
        return new string[] { spec, packageName, globalName };
    }

    static string FindPathExecutable(string fileName)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string raw in path.Split(';'))
        {
            string dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;
            try
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch { }
        }
        return null;
    }

    static string FindNodeExecutable()
    {
        string found = FindPathExecutable("node.exe");
        if (!string.IsNullOrEmpty(found)) return found;
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string candidate = Path.Combine(programFiles, "nodejs", "node.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    static string FindNpmCli(string nodePath)
    {
        List<string> roots = new List<string>();
        if (!string.IsNullOrEmpty(nodePath)) roots.Add(Path.GetDirectoryName(nodePath));
        string npmCmd = FindPathExecutable("npm.cmd");
        if (!string.IsNullOrEmpty(npmCmd)) roots.Add(Path.GetDirectoryName(npmCmd));
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData)) roots.Add(Path.Combine(appData, "npm"));
        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            string candidate = Path.Combine(root, "node_modules", "npm", "bin", "npm-cli.js");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    static string QuoteProcessArgument(string value)
    {
        value = value ?? "";
        if (value.Length > 0 && value.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0) return value;
        StringBuilder result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    static string RunVersionProbe(string executable, string arguments)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(executable, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process process = Process.Start(psi))
            {
                if (process == null || !process.WaitForExit(5000))
                {
                    try { if (process != null) process.Kill(); } catch { }
                    return "";
                }
                string output = process.StandardOutput.ReadToEnd().Trim();
                if (output.Length == 0) output = process.StandardError.ReadToEnd().Trim();
                return process.ExitCode == 0 ? output : "";
            }
        }
        catch { return ""; }
    }

    static string JsNpmStatus()
    {
        string node = FindNodeExecutable();
        string npm = FindNpmCli(node);
        string nodeVersion = string.IsNullOrEmpty(node) ? "" : RunVersionProbe(node, "--version");
        string npmVersion = string.IsNullOrEmpty(node) || string.IsNullOrEmpty(npm)
            ? "" : RunVersionProbe(node, QuoteProcessArgument(npm) + " --version");
        bool available = nodeVersion.Length > 0 && npmVersion.Length > 0;
        return "{\"available\":" + (available ? "true" : "false")
            + ",\"node\":" + JsonString(nodeVersion) + ",\"npm\":" + JsonString(npmVersion) + "}";
    }

    static void PrepareNpmRunner()
    {
        string dir = Path.GetDirectoryName(NpmPackageRunnerPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bool same = false;
        try
        {
            if (File.Exists(NpmPackageRunnerPath))
            {
                byte[] old = File.ReadAllBytes(NpmPackageRunnerPath);
                if (old.Length == NpmPackageRunner.Length)
                {
                    same = true;
                    for (int i = 0; i < old.Length; i++) if (old[i] != NpmPackageRunner[i]) { same = false; break; }
                }
            }
        }
        catch { }
        if (!same) File.WriteAllBytes(NpmPackageRunnerPath, NpmPackageRunner);
    }

    static int InstalledNpmPackageCount()
    {
        if (!Directory.Exists(NpmPackageCachePath)) return 0;
        int count = 0;
        foreach (string dir in Directory.GetDirectories(NpmPackageCachePath))
            if (IsSafeNpmId(Path.GetFileName(dir)) && File.Exists(Path.Combine(dir, "metadata.json"))) count++;
        return count;
    }

    // 취소·강제 종료가 설치 교체 중간에 일어나도 다음 설치 전에 작업폴더를 정리하고 이전 캐시를 복구한다.
    static void RecoverNpmPackageCache()
    {
        try
        {
            if (!Directory.Exists(NpmPackageCachePath)) return;
            foreach (string dir in Directory.GetDirectories(NpmPackageCachePath))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith(".work-", StringComparison.Ordinal))
                {
                    try { Directory.Delete(dir, true); } catch { }
                    continue;
                }
                if (name.Length > 37 && IsSafeNpmId(name.Substring(0, 32))
                    && name.Substring(32).StartsWith(".old-", StringComparison.Ordinal))
                {
                    string target = Path.Combine(NpmPackageCachePath, name.Substring(0, 32));
                    try
                    {
                        if (!Directory.Exists(target)) Directory.Move(dir, target);
                        else Directory.Delete(dir, true);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    static string StartJsNpmInstall(byte[] body)
    {
        string[] request = ParseNpmInstallRequest(body);
        string node = FindNodeExecutable();
        if (string.IsNullOrEmpty(node)) throw new InvalidOperationException("no-node");
        string npmCli = FindNpmCli(node);
        if (string.IsNullOrEmpty(npmCli)) throw new InvalidOperationException("no-npm");
        lock (NpmJobsLock)
            foreach (NpmJob active in NpmJobs.Values)
                if (!active.Complete) throw new InvalidOperationException("npm-busy");
        SweepNpmJobs();
        RecoverNpmPackageCache();
        string packageId = NpmPackageId(request[0], request[2]);
        string target = Path.Combine(NpmPackageCachePath, packageId);
        if (File.Exists(Path.Combine(target, "metadata.json")))
            throw new InvalidOperationException("npm-package-exists");
        if (!Directory.Exists(target) && InstalledNpmPackageCount() >= 20)
            throw new InvalidOperationException("npm-package-limit");
        PrepareNpmRunner();

        NpmJob job = new NpmJob();
        job.Id = Guid.NewGuid().ToString("N");
        job.Log.AppendLine("npm 패키지를 별도 캐시에 설치합니다. install script는 실행하지 않습니다.");
        string[] args = new string[] { NpmPackageRunnerPath, NpmPackageCachePath, packageId, npmCli, request[0], request[1], request[2] };
        StringBuilder command = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) command.Append(' ');
            command.Append(QuoteProcessArgument(args[i]));
        }
        ProcessStartInfo psi = new ProcessStartInfo(node, command.ToString());
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.EnvironmentVariables["NO_COLOR"] = "1";

        job.Process = new Process();
        job.Process.StartInfo = psi;
        job.Process.Start();
        lock (NpmJobsLock) NpmJobs[job.Id] = job;
        Thread outReader = StartLimitedReader(job.Process.StandardOutput, job.Log);
        Thread errReader = StartLimitedReader(job.Process.StandardError, job.Log);
        Thread watcher = new Thread(delegate()
        {
            bool exited = false;
            try { exited = job.Process.WaitForExit(480000); } catch { }
            if (!exited)
            {
                bool cancelled;
                lock (job.Sync) cancelled = job.CancelRequested;
                if (!cancelled) job.Log.AppendLine("[시간 초과: 설치를 8분 후 중단했습니다.]");
                KillProcessTree(job.Process);
                try { job.Process.WaitForExit(3000); } catch { }
            }
            try { outReader.Join(2500); errReader.Join(2500); } catch { }
            int code;
            try { code = job.Process.ExitCode; } catch { code = -1; }
            lock (job.Sync)
            {
                job.ExitCode = job.CancelRequested ? -1 : (exited ? code : -1);
                job.DoneAt = DateTime.UtcNow;
                job.Complete = true;
            }
            try { job.Process.Dispose(); } catch { }
            SweepNpmJobs();
        });
        watcher.IsBackground = true;
        watcher.Start();
        return "{\"id\":" + JsonString(job.Id) + "}";
    }

    static string PollJsNpmInstall(string id, string knownLen)
    {
        NpmJob job;
        lock (NpmJobsLock) if (!NpmJobs.TryGetValue(id ?? "", out job))
            return "{\"complete\":true,\"code\":-1,\"cancelled\":false,\"log\":" + JsonString("설치 작업을 찾지 못했습니다.") + "}";
        lock (job.Sync)
        {
            int from = 0;
            bool known = int.TryParse(knownLen ?? "", out from) && from >= 0 && from <= job.Log.TextLength;
            if (known && !job.Complete && from == job.Log.TextLength) return "{\"complete\":false,\"unchanged\":true}";
            string head = "{\"complete\":" + (job.Complete ? "true" : "false")
                + ",\"code\":" + job.ExitCode + ",\"cancelled\":" + (job.CancelRequested ? "true" : "false");
            if (known) return head + ",\"logDelta\":" + JsonString(job.Log.GetTextFrom(from)) + "}";
            return head + ",\"log\":" + JsonString(job.Log.GetText()) + "}";
        }
    }

    static void CancelJsNpmInstall(string id)
    {
        NpmJob job;
        lock (NpmJobsLock) if (!NpmJobs.TryGetValue(id ?? "", out job)) return;
        lock (job.Sync)
        {
            if (job.Complete || job.CancelRequested) return;
            job.CancelRequested = true;
        }
        job.Log.AppendLine("[설치를 취소했습니다. 이전에 완료된 캐시는 그대로 남습니다.]");
        KillProcessTree(job.Process);
    }

    static void SweepNpmJobs()
    {
        lock (NpmJobsLock)
        {
            List<NpmJob> done = new List<NpmJob>();
            foreach (NpmJob job in NpmJobs.Values) if (job.Complete) done.Add(job);
            done.Sort(delegate(NpmJob a, NpmJob b) { return a.DoneAt.CompareTo(b.DoneAt); });
            DateTime now = DateTime.UtcNow;
            List<NpmJob> remove = new List<NpmJob>();
            foreach (NpmJob job in done) if ((now - job.DoneAt).TotalMinutes > 10) remove.Add(job);
            for (int i = 0; i < done.Count - 8; i++) if (!remove.Contains(done[i])) remove.Add(done[i]);
            foreach (NpmJob job in remove) NpmJobs.Remove(job.Id);
        }
    }

    static string ListJsNpmPackages()
    {
        StringBuilder json = new StringBuilder("[");
        try
        {
            if (Directory.Exists(NpmPackageCachePath))
            {
                foreach (string dir in Directory.GetDirectories(NpmPackageCachePath))
                {
                    string id = Path.GetFileName(dir);
                    if (!IsSafeNpmId(id)) continue;
                    string metadataPath = Path.Combine(dir, "metadata.json");
                    if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length > 64 * 1024) continue;
                    string metadata = File.ReadAllText(metadataPath, Encoding.UTF8).Trim();
                    if (!metadata.StartsWith("{", StringComparison.Ordinal) || !metadata.EndsWith("}", StringComparison.Ordinal)) continue;
                    if (json.Length > 1) json.Append(',');
                    json.Append(metadata);
                }
            }
        }
        catch { }
        return json.Append(']').ToString();
    }

    static bool TryReadJsNpmBundle(string id, out byte[] bundle)
    {
        bundle = null;
        if (!IsSafeNpmId(id)) return false;
        string dir = Path.Combine(NpmPackageCachePath, id);
        string metadata = Path.Combine(dir, "metadata.json");
        string path = Path.Combine(dir, "bundle.js");
        try
        {
            if (!File.Exists(metadata) || !File.Exists(path)) return false;
            FileInfo info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > 8L * 1024 * 1024) return false;
            bundle = File.ReadAllBytes(path);
            return true;
        }
        catch { bundle = null; return false; }
    }

    static bool DeleteJsNpmPackage(string id)
    {
        if (!IsSafeNpmId(id)) return false;
        string dir = Path.Combine(NpmPackageCachePath, id);
        try
        {
            if (!Directory.Exists(dir)) return false;
            Directory.Delete(dir, true);
            return true;
        }
        catch { return false; }
    }

    static readonly System.Text.RegularExpressions.Regex PkgNameRe =
        new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]*([=<>!~]=?[A-Za-z0-9._*-]+)?$");

    // 본문(공백·쉼표 구분) → 설치할 패키지 목록. 주입 가능한 인자는 여기서 막는다.
    static List<string> ParsePipPackages(byte[] body)
    {
        string text = Encoding.UTF8.GetString(body ?? new byte[0]);
        string[] raw = text.Split(new char[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
        List<string> pkgs = new List<string>();
        foreach (string p in raw)
        {
            string t = p.Trim();
            if (t.Length == 0) continue;
            if (!PkgNameRe.IsMatch(t)) throw new Exception("invalid-package: " + t);  // 주입/이상한 인자 차단
            pkgs.Add(t);
            if (pkgs.Count >= 40) break;
        }
        if (pkgs.Count == 0) throw new Exception("no-packages");
        return pkgs;
    }

    static ProcessStartInfo PipInstallStartInfo(string interp, List<string> pkgs)
    {
        StringBuilder argSb = new StringBuilder();
        if (interp == "py") argSb.Append("-3 ");
        argSb.Append("-m pip install --disable-pip-version-check --no-input");
        // 저장소에 번들된 순수 파이썬 휠(vendor/wheels)을 우선 사용 → 인터넷 없이도 클릭 한 번 설치(없는 패키지는 PyPI 폴백).
        string wheelsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vendor", "wheels");
        if (Directory.Exists(wheelsDir)) argSb.Append(" --find-links \"").Append(wheelsDir).Append("\"");
        foreach (string p in pkgs) argSb.Append(" \"").Append(p).Append("\"");

        ProcessStartInfo psi = new ProcessStartInfo(interp, argSb.ToString());
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        // 파이프로 내보낼 때 pip 은 진행바를 생략한다. 대신 줄 단위 진행(Collecting/Downloading/Installing)을 바로 흘려보내게 한다.
        psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        return psi;
    }

    // 설치를 시작만 하고 id 를 돌려준다. 로그는 버퍼에 쌓이고 프런트가 /pip-install-poll 로 증분을 받아간다.
    static string StartPipInstall(byte[] body)
    {
        string interp = FindPython();
        if (interp == null) throw new PythonMissingException();
        List<string> pkgs = ParsePipPackages(body);
        SweepPipJobs();

        PipJob job = new PipJob();
        job.Id = Guid.NewGuid().ToString("N");
        job.Log.AppendLine("pip install " + string.Join(" ", pkgs.ToArray()));

        job.Process = new Process();
        job.Process.StartInfo = PipInstallStartInfo(interp, pkgs);
        job.Process.Start();
        lock (PipJobsLock) PipJobs[job.Id] = job;

        // stdout·stderr 를 한 버퍼에 모아 pip 가 낸 순서대로 보이게 한다(LimitedTextBuffer 는 내부에서 잠근다).
        Thread outReader = StartLimitedReader(job.Process.StandardOutput, job.Log);
        Thread errReader = StartLimitedReader(job.Process.StandardError, job.Log);
        Thread watcher = new Thread(delegate()
        {
            bool exited = false;
            try { exited = job.Process.WaitForExit(300000); } catch { }   // 최대 5분(큰 휠 다운로드 여유)
            if (!exited)
            {
                bool cancelled;
                lock (job.Sync) cancelled = job.CancelRequested;
                if (!cancelled) job.Log.AppendLine("[시간 초과: 설치를 5분 후 중단했습니다.]");
                KillProcessTree(job.Process);
                try { job.Process.WaitForExit(2000); } catch { }
            }
            try { outReader.Join(2000); errReader.Join(2000); } catch { }
            int code;
            try { code = job.Process.ExitCode; } catch { code = -1; }
            lock (job.Sync)
            {
                // 취소는 pip 을 죽여서 끝내므로 종료 코드가 무엇이든 실패로 보고한다.
                job.ExitCode = job.CancelRequested ? -1 : (exited ? code : -1);
                job.DoneAt = DateTime.UtcNow;
                job.Complete = true;
            }
            try { job.Process.Dispose(); } catch { }
            SweepPipJobs();
        });
        watcher.IsBackground = true;
        watcher.Start();
        return "{\"id\":" + JsonString(job.Id) + "}";
    }

    // 증분 폴링 — 파이썬 세션(PollPythonSession)과 같은 규약. from 이 현재 길이와 같고 아직 진행 중이면 본문 없이 짧게 답한다.
    static string PollPipInstall(string id, string knownLen)
    {
        PipJob job;
        lock (PipJobsLock) if (!PipJobs.TryGetValue(id ?? "", out job))
            return "{\"complete\":true,\"code\":-1,\"cancelled\":false,\"log\":" + JsonString("설치 작업을 찾지 못했습니다.") + "}";
        lock (job.Sync)
        {
            int from = 0;
            bool known = int.TryParse(knownLen ?? "", out from) && from >= 0 && from <= job.Log.TextLength;
            if (known && !job.Complete && from == job.Log.TextLength)
                return "{\"complete\":false,\"unchanged\":true}";
            string head = "{\"complete\":" + (job.Complete ? "true" : "false")
                + ",\"code\":" + job.ExitCode
                + ",\"cancelled\":" + (job.CancelRequested ? "true" : "false");
            if (known) return head + ",\"logDelta\":" + JsonString(job.Log.GetTextFrom(from)) + "}";
            return head + ",\"log\":" + JsonString(job.Log.GetText()) + "}";
        }
    }

    static void CancelPipInstall(string id)
    {
        PipJob job;
        lock (PipJobsLock) if (!PipJobs.TryGetValue(id ?? "", out job)) return;
        lock (job.Sync)
        {
            if (job.Complete || job.CancelRequested) return;
            job.CancelRequested = true;
        }
        job.Log.AppendLine("[설치를 취소했습니다. 이미 설치된 패키지는 그대로 남습니다.]");
        KillProcessTree(job.Process);
    }

    // 끝난 작업은 로그를 잠시 남겨 두고(폴링이 늦게 와도 결과를 볼 수 있게) 오래된 것만 버린다.
    static void SweepPipJobs()
    {
        lock (PipJobsLock)
        {
            List<PipJob> done = new List<PipJob>();
            foreach (PipJob job in PipJobs.Values) if (job.Complete) done.Add(job);
            done.Sort(delegate(PipJob a, PipJob b) { return a.DoneAt.CompareTo(b.DoneAt); });
            DateTime now = DateTime.UtcNow;
            List<PipJob> remove = new List<PipJob>();
            foreach (PipJob job in done)
                if ((now - job.DoneAt).TotalMinutes > 10) remove.Add(job);
            for (int i = 0; i < done.Count - 8; i++)
                if (!remove.Contains(done[i])) remove.Add(done[i]);
            foreach (PipJob job in remove) PipJobs.Remove(job.Id);
        }
    }

    // 예전 오프라인 HTML(스트리밍 폴링을 모르는 판)을 위한 한 번에 응답하는 경로. 새 화면은 /pip-install-start 를 쓴다.
    static string PipInstall(byte[] body)
    {
        string interp = FindPython();
        if (interp == null) throw new PythonMissingException();

        List<string> pkgs = ParsePipPackages(body);
        ProcessStartInfo psi = PipInstallStartInfo(interp, pkgs);

        StringBuilder outSb = new StringBuilder();
        int exitCode = -1;
        Process proc = null;
        try
        {
            proc = new Process();
            proc.StartInfo = psi;
            proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
            proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            if (!proc.WaitForExit(300000))   // 최대 5분(큰 휠 다운로드 여유)
            {
                try { proc.Kill(); } catch { }
                try { proc.WaitForExit(2000); } catch { }
                lock (outSb) outSb.AppendLine("[시간 초과: 설치를 5분 후 중단했습니다.]");
                exitCode = -1;
            }
            else { proc.WaitForExit(); exitCode = proc.ExitCode; }
        }
        finally { if (proc != null) { try { proc.Dispose(); } catch { } } }

        string tail;
        lock (outSb) tail = outSb.ToString();
        if (tail.Length > 8000) tail = "…(생략)…\n" + tail.Substring(tail.Length - 8000);  // 끝부분(설치 결과)만

        return "{\"ok\":" + (exitCode == 0 ? "true" : "false")
             + ",\"code\":" + exitCode
             + ",\"output\":" + JsonString(tail) + "}";
    }

    // ===== Jedi 기반 문맥 자동완성 =====
    // ===== 설치된 로컬 Python 패키지의 안전한 import 색인 =====
    static readonly object PythonImportIndexLock = new object();
    static string _pythonImportIndexState = "idle"; // idle | building | ready | error
    static string _pythonImportIndexJson = "";
    static string _pythonImportIndexRunnerPath = null;

    static string PythonImportIndexRunner()
    {
        lock (PythonImportIndexLock)
        {
            if (_pythonImportIndexRunnerPath != null && File.Exists(_pythonImportIndexRunnerPath)) return _pythonImportIndexRunnerPath;
            string path = Path.Combine(Path.GetTempPath(), "moida_python_import_index.py");
            File.WriteAllText(path, @"import ast
import json
import os
import site
import sysconfig
import tokenize

MAX_FILES = 20000
MAX_ITEMS = 20000
MAX_FILE_BYTES = 1024 * 1024
skip_dirs = set(['__pycache__', 'tests', 'test', 'docs', 'doc', 'examples', 'example', 'data', 'dist-info', 'egg-info'])
bases = []
for value in list(getattr(site, 'getsitepackages', lambda: [])()) + [getattr(site, 'getusersitepackages', lambda: '')(), sysconfig.get_paths().get('purelib', ''), sysconfig.get_paths().get('platlib', '')]:
    if value and os.path.isdir(value) and value not in bases:
        bases.append(value)

items = {}
def add(name, kind, import_text):
    if len(items) >= MAX_ITEMS or not name or name.startswith('_') or not name.isidentifier():
        return
    key = (name, import_text)
    if key not in items:
        items[key] = {'name': name, 'type': kind, 'importText': import_text}

def module_for(base, full):
    rel = os.path.relpath(full, base)
    parts = rel.split(os.sep)
    leaf = parts.pop()
    stem = os.path.splitext(leaf)[0]
    if stem != '__init__':
        parts.append(stem)
    if not parts or any((not p.isidentifier()) for p in parts):
        return ''
    return '.'.join(parts)

try:
    from importlib import metadata
    for dist in metadata.distributions():
        top_level = dist.read_text('top_level.txt') or ''
        for top in top_level.splitlines():
            top = top.strip()
            if top.isidentifier():
                add(top, 'module', 'import ' + top)
except Exception:
    pass

seen_files = 0
for base in bases:
    for root, dirs, files in os.walk(base):
        dirs[:] = [d for d in dirs if not d.startswith('.') and not d.endswith(('.dist-info', '.egg-info')) and d not in skip_dirs]
        for file_name in files:
            if seen_files >= MAX_FILES or len(items) >= MAX_ITEMS:
                break
            if not file_name.endswith(('.py', '.pyi')) or file_name.startswith('.'):
                continue
            full = os.path.join(root, file_name)
            try:
                if os.path.getsize(full) > MAX_FILE_BYTES:
                    continue
                module = module_for(base, full)
                if not module:
                    continue
                seen_files += 1
                top = module.split('.')[0]
                add(top, 'module', 'import ' + top)
                with tokenize.open(full) as handle:
                    tree = ast.parse(handle.read(), filename=full)
            except Exception:
                continue
            for node in tree.body:
                if isinstance(node, ast.ClassDef):
                    add(node.name, 'class', 'from ' + module + ' import ' + node.name)
                elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                    add(node.name, 'function', 'from ' + module + ' import ' + node.name)
                elif isinstance(node, ast.ImportFrom) and node.level:
                    for alias in node.names:
                        if alias.name != '*':
                            public_name = alias.asname or alias.name
                            add(public_name, 'class', 'from ' + module + ' import ' + public_name)
        if seen_files >= MAX_FILES or len(items) >= MAX_ITEMS:
            break

rows = sorted(items.values(), key=lambda item: (item['name'].lower(), item['name'], item['importText'].count('.'), item['importText']))
print(json.dumps({'ok': True, 'state': 'ready', 'items': rows, 'truncated': seen_files >= MAX_FILES or len(items) >= MAX_ITEMS}, ensure_ascii=False, separators=(',', ':')))
", new UTF8Encoding(false));
            _pythonImportIndexRunnerPath = path;
            return path;
        }
    }

    static void BuildPythonImportIndex()
    {
        string result = "";
        string error = "";
        try
        {
            string interp = FindPython();
            if (interp == null) throw new PythonMissingException();
            string args = (interp == "py" ? "-3 " : "") + "\"" + PythonImportIndexRunner() + "\"";
            ProcessStartInfo psi = new ProcessStartInfo(interp, args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            Process proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("index-spawn-failed");
            result = proc.StandardOutput.ReadToEnd();
            error = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(60000))
            {
                try { proc.Kill(); } catch { }
                throw new TimeoutException("index-timeout");
            }
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException("index-failed: " + error);
            if (result.Length > 8 * 1024 * 1024) throw new InvalidOperationException("index-too-large");
            result = result.Trim();
            if (!result.StartsWith("{", StringComparison.Ordinal)) throw new InvalidOperationException("index-invalid-response");
        }
        catch (Exception ex)
        {
            error = FlattenMessage(ex);
            result = "";
        }
        lock (PythonImportIndexLock)
        {
            if (!string.IsNullOrEmpty(result))
            {
                _pythonImportIndexJson = result;
                _pythonImportIndexState = "ready";
            }
            else
            {
                _pythonImportIndexJson = "{\"ok\":false,\"state\":\"error\",\"reason\":" + JsonString(string.IsNullOrEmpty(error) ? "index-failed" : error) + ",\"items\":[]}";
                _pythonImportIndexState = "error";
            }
        }
    }

    static string PythonImportIndexJson()
    {
        lock (PythonImportIndexLock)
        {
            if (_pythonImportIndexState == "ready" || _pythonImportIndexState == "error") return _pythonImportIndexJson;
            if (_pythonImportIndexState == "idle")
            {
                _pythonImportIndexState = "building";
                Thread worker = new Thread(BuildPythonImportIndex);
                worker.IsBackground = true;
                worker.Start();
            }
            return "{\"ok\":true,\"state\":\"building\",\"items\":[]}";
        }
    }

    // ===== Jedi 기반 문맥 자동완성 =====
    static readonly object JediLock = new object();
    static bool? _jediReady = null;
    static string _jediRunnerPath = null;

    // ===== Jedi 프로젝트 미러 =====
    // 브라우저 폴더 핸들(showDirectoryPicker)로 연 작업공간에는 실제 디스크 경로가 없다. Jedi 가
    // 프로젝트 모듈(from 내패키지.모듈 import …)을 풀려면 진짜 폴더가 필요해서, 작업공간의 .py 를
    // 임시 폴더에 그대로 미러링하고 그 폴더를 jedi.Project 루트로 넘긴다.
    // 미러 경로는 서버만 알고 환경변수로 러너에 준다 — 요청이 임의 폴더를 가리킬 수 없게.
    static readonly object ProjectMirrorLock = new object();
    static string _projectMirrorRoot = null;
    const int ProjectMirrorMaxFiles = 20000;
    const int ProjectMirrorMaxFileBytes = 1024 * 1024;

    static string CurrentProjectMirrorRoot()
    {
        lock (ProjectMirrorLock)
        {
            return (_projectMirrorRoot != null && Directory.Exists(_projectMirrorRoot)) ? _projectMirrorRoot : null;
        }
    }

    // body: [count]([pathLen][path][dataLen][data])*  — 실행 번들과 같은 리틀엔디언 형식(대상·표준입력 없음).
    // 새 폴더에 통째로 쓰고 마지막에 교체한다 — 진행 중인 Jedi 프로세스가 읽던 파일이 사라지지 않도록.
    static string SyncPythonProjectMirror(byte[] body)
    {
        if (body == null || body.Length > 64 * 1024 * 1024) throw new Exception("bad-project-bundle");
        int pos = 0;
        int count = ReadBundleInt(body, ref pos);
        if (count < 0 || count > ProjectMirrorMaxFiles) throw new Exception("bad-project-bundle");
        string fresh = Path.Combine(Path.GetTempPath(), "moidapy_project_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fresh);
        int written = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                string rel = ReadBundleString(body, ref pos);
                int len = ReadBundleInt(body, ref pos);
                if (len < 0 || pos + len > body.Length) throw new Exception("bad-project-bundle");
                string safe = SafeRelPath(rel);
                if (safe != null && len <= ProjectMirrorMaxFileBytes && IsPythonSourcePath(safe))
                {
                    string full = Path.Combine(fresh, safe);
                    string dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    using (FileStream fs = new FileStream(full, FileMode.Create, FileAccess.Write))
                        fs.Write(body, pos, len);
                    written++;
                }
                pos += len;   // 건너뛴 파일도 스트림 위치는 그대로 진행
            }
            if (pos != body.Length) throw new Exception("bad-project-bundle");
        }
        catch
        {
            try { Directory.Delete(fresh, true); } catch { }
            throw;
        }
        string previous;
        lock (ProjectMirrorLock)
        {
            previous = _projectMirrorRoot;
            _projectMirrorRoot = fresh;
        }
        if (previous != null) try { Directory.Delete(previous, true); } catch { }   // 아직 읽는 중이면 다음 기회에 정리된다
        return "{\"ok\":true,\"files\":" + written + "}";
    }

    static bool IsPythonSourcePath(string rel)
    {
        string lower = (rel ?? "").ToLowerInvariant();
        return lower.EndsWith(".py") || lower.EndsWith(".pyw") || lower.EndsWith(".pyi");
    }

    static void ClearPythonProjectMirror()
    {
        string previous;
        lock (ProjectMirrorLock) { previous = _projectMirrorRoot; _projectMirrorRoot = null; }
        if (previous != null) try { Directory.Delete(previous, true); } catch { }
    }

    static bool RunPyCheck(string interp, string code)
    {
        try
        {
            // 여기 오는 code 는 경로가 아니라 파이썬 소스다("import jedi" 등). 지금은 호출부가 모두
            // 고정 문자열이지만, 변수를 넘기는 순간 따옴표 하나로 인자가 갈라진다.
            string args = (interp == "py" ? "-3 " : "") + "-c " + QuoteProcessArgument(code);
            ProcessStartInfo psi = new ProcessStartInfo(interp, args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            Process p = Process.Start(psi);
            if (p == null) return false;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // Jedi 사용 가능 보장(없으면 최초 1회 pip 설치 시도). 결과는 캐시 → 다음부터 즉시.
    static bool EnsureJedi()
    {
        string interp = FindPython();
        if (interp == null) return false;
        lock (JediLock)
        {
            if (_jediReady.HasValue) return _jediReady.Value;
            if (RunPyCheck(interp, "import jedi")) { _jediReady = true; return true; }
            try
            {
                StringBuilder a = new StringBuilder();
                if (interp == "py") a.Append("-3 ");
                a.Append("-m pip install --disable-pip-version-check --no-input jedi");
                ProcessStartInfo psi = new ProcessStartInfo(interp, a.ToString());
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                if (p != null) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(180000); }
            }
            catch { /* 오프라인 등 → 설치 실패 시 폴백 */ }
            _jediReady = RunPyCheck(interp, "import jedi");
            return _jediReady.Value;
        }
    }

    static string JediRunner()
    {
        lock (JediLock)
        {
            if (_jediRunnerPath != null && File.Exists(_jediRunnerPath)) return _jediRunnerPath;
            string path = Path.Combine(Path.GetTempPath(), "moida_jedi_complete.py");
            File.WriteAllText(path,
                "import sys, json, os\n" +
                "data = json.load(sys.stdin)\n" +
                "mode = data.get('mode', 'complete')\n" +
                "try:\n" +
                "    import jedi\n" +
                "except Exception:\n" +
                "    print(json.dumps({'ok': False, 'reason': 'no-jedi'})); sys.exit(0)\n" +
                "src = data.get('source','')\n" +
                "line = int(data.get('line', 1)); col = int(data.get('column', 0))\n" +
                // 프로젝트 루트는 서버가 환경변수로만 준다. 요청의 path 는 그 안의 상대경로로만 쓴다
                // (정규화 후 루트 밖을 가리키면 버린다) — 요청이 임의 경로를 열지 못하게.
                "root = os.environ.get('MOIDA_JEDI_ROOT', '') or ''\n" +
                "root = os.path.normpath(root) if root else ''\n" +   // 구분자를 os 형식으로 통일 — 아래 경로 비교의 전제
                "if root and not os.path.isdir(root): root = ''\n" +
                "def inside(rel_path):\n" +           // 미러 안으로만 해석한다(루트 밖을 가리키면 버린다)
                "    if not root or not rel_path: return ''\n" +
                "    candidate = os.path.normpath(os.path.join(root, rel_path))\n" +
                "    return candidate if (candidate == root or candidate.startswith(root + os.sep)) else ''\n" +
                "clean = lambda value: str(value or '').replace('\\\\', '/').strip('/')\n" +
                "script_path = inside(clean(data.get('path', ''))) or None\n" +
                // 실행 기준 폴더(sys.path 루트)가 곧 프로젝트 루트다. 작업공간 루트와 다를 수 있어
                // (예: llm_project/ 아래에 패키지가 있는 구조) 앱이 추정한 값을 상대경로로 받는다.
                "project_root = inside(clean(data.get('root', ''))) or root\n" +
                "def to_workspace(p):\n" +           // 미러 안의 경로 → 작업공간 상대경로(앱이 원래 탭을 열도록)
                "    if not root or not p: return ''\n" +
                "    try: full = os.path.normpath(str(p))\n" +
                "    except Exception: return ''\n" +
                "    if not full.startswith(root + os.sep): return ''\n" +
                "    return full[len(root) + 1:].replace(os.sep, '/')\n" +
                "try:\n" +
                "    project = None\n" +
                "    if project_root:\n" +
                "        try: project = jedi.Project(project_root)\n" +
                "        except Exception: project = None\n" +
                "    try:\n" +
                "        script = jedi.Script(code=src, path=script_path, project=project)\n" +
                "    except TypeError:\n" +          // 예전 Jedi(project/path 인자 없음)에서는 지금까지처럼 코드만 본다
                "        script = jedi.Script(code=src)\n" +
                "    if mode == 'definition':\n" +
                "        defs = []\n" +
                "        try:\n" +
                "            defs = script.goto(line, col, follow_imports=True, follow_builtin_imports=True)\n" +
                "        except TypeError:\n" +
                "            defs = script.goto(line, col)\n" +
                "        if not defs:\n" +
                "            try: defs = script.infer(line, col)\n" +
                "            except Exception: defs = []\n" +
                "        for d in defs:\n" +
                "            p = getattr(d, 'module_path', None)\n" +
                "            if p:\n" +
                "                print(json.dumps({'ok': True, 'path': str(p), 'workspacePath': to_workspace(p), 'line': getattr(d, 'line', 1) or 1, 'column': getattr(d, 'column', 0) or 0, 'name': getattr(d, 'name', '') or '', 'type': getattr(d, 'type', '') or ''})); sys.exit(0)\n" +
                "        print(json.dumps({'ok': False, 'reason': 'builtin'})); sys.exit(0)\n" +
                // import 검사: 위치 목록을 한 번에 받아 각 자리에서 정의를 찾아본다(프로세스 1회).
                // 못 찾은 자리의 인덱스만 돌려준다 — 앱이 그 자리에 경고 표시를 붙인다.
                "    elif mode == 'imports':\n" +
                "        targets = data.get('targets') or []\n" +
                "        unresolved = []\n" +
                "        for index, target in enumerate(targets[:120]):\n" +
                "            try:\n" +
                "                at_line = int(target.get('line', 1)); at_col = int(target.get('column', 0))\n" +
                "            except Exception:\n" +
                "                continue\n" +
                "            found = []\n" +
                "            try:\n" +
                "                found = script.goto(at_line, at_col, follow_imports=True, follow_builtin_imports=True)\n" +
                "            except TypeError:\n" +
                "                try: found = script.goto(at_line, at_col)\n" +
                "                except Exception: found = []\n" +
                "            except Exception:\n" +
                "                found = []\n" +
                "            if not found:\n" +
                "                try: found = script.infer(at_line, at_col)\n" +
                "                except Exception: found = []\n" +
                "            if not found: unresolved.append(index)\n" +
                "        print(json.dumps({'ok': True, 'unresolved': unresolved})); sys.exit(0)\n" +
                "    elif mode == 'help':\n" +
                "        names = []\n" +
                "        try:\n" +
                "            names = script.help(line, col)\n" +
                "        except Exception:\n" +
                "            names = []\n" +
                "        for d in names:\n" +
                "            doc = ''\n" +
                "            try: doc = d.docstring(raw=False) or ''\n" +
                "            except Exception: doc = ''\n" +
                "            sig = ''\n" +
                "            try:\n" +
                "                sigs = d.get_signatures()\n" +
                "                if sigs: sig = sigs[0].to_string()[:400]\n" +
                "            except Exception: pass\n" +
                "            name = getattr(d, 'name', '') or ''\n" +
                "            if name or doc or sig:\n" +
                "                print(json.dumps({'ok': True, 'name': name, 'type': getattr(d, 'type', '') or '', 'signature': sig, 'docstring': doc[:4000]})); sys.exit(0)\n" +
                "        print(json.dumps({'ok': False, 'reason': 'no-help'})); sys.exit(0)\n" +
                "    else:\n" +
                "        comps = script.complete(line, col)\n" +
                "        items = []; seen = set()\n" +
                "        for c in comps[:50]:\n" +
                "            n = c.name\n" +
                "            if not n or n in seen: continue\n" +
                "            seen.add(n)\n" +
                "            kind = getattr(c, 'type', '') or ''\n" +
                "            signature = ''\n" +
                "            if kind == 'function':\n" +
                "                try:\n" +
                "                    signatures = c.get_signatures()\n" +
                "                    if signatures: signature = signatures[0].to_string()[:700]\n" +
                "                except Exception:\n" +
                "                    pass\n" +
                "            items.append({'name': n, 'type': kind, 'signature': signature})\n" +
                "        print(json.dumps({'ok': True, 'items': items}))\n" +
                "except Exception:\n" +
                "    print(json.dumps({'ok': False, 'reason': 'error'}))\n",
                new UTF8Encoding(false));
            _jediRunnerPath = path;
            return path;
        }
    }

    static string JediComplete(byte[] body)
    {
        string interp = FindPython();
        if (interp == null) throw new PythonMissingException();
        if (!EnsureJedi()) return "{\"ok\":false,\"reason\":\"no-jedi\"}";

        string runner = JediRunner();
        string args = (interp == "py" ? "-3 " : "") + "\"" + runner + "\"";
        ProcessStartInfo psi = new ProcessStartInfo(interp, args);
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        string mirror = CurrentProjectMirrorRoot();
        if (mirror != null) psi.EnvironmentVariables["MOIDA_JEDI_ROOT"] = mirror;   // 작업공간 미러가 있으면 그 폴더를 프로젝트로

        Process proc = Process.Start(psi);
        if (proc == null) return "{\"ok\":false,\"reason\":\"spawn\"}";
        byte[] inb = body ?? new byte[0];
        try { proc.StandardInput.BaseStream.Write(inb, 0, inb.Length); proc.StandardInput.BaseStream.Flush(); }
        catch { }
        try { proc.StandardInput.Close(); } catch { }
        string outp = proc.StandardOutput.ReadToEnd();
        try { proc.StandardError.ReadToEnd(); } catch { }
        if (!proc.WaitForExit(8000)) { try { proc.Kill(); } catch { } return "{\"ok\":false,\"reason\":\"timeout\"}"; }
        outp = (outp ?? "").Trim();
        return outp.Length == 0 ? "{\"ok\":false,\"reason\":\"empty\"}" : outp;
    }

    static string JediDefinition(byte[] body)
    {
        return JediComplete(body);
    }

    static string RunPython(byte[] body)
    {
        string interp = FindPython();
        if (interp == null) throw new PythonMissingException();

        byte[] src = body;
        string stdin = "";
        // 새 형식: [sourceLen][source][stdinLen][stdin]. 예전 HTML의 순수 소스 바디도 계속 허용한다.
        try
        {
            int pos = 0;
            int sourceLen = ReadBundleInt(body, ref pos);
            if (sourceLen >= 0 && pos + sourceLen + 4 <= body.Length)
            {
                byte[] parsed = new byte[sourceLen];
                Buffer.BlockCopy(body, pos, parsed, 0, sourceLen);
                pos += sourceLen;
                int stdinLen = ReadBundleInt(body, ref pos);
                if (stdinLen >= 0 && pos + stdinLen == body.Length)
                {
                    src = parsed;
                    stdin = Encoding.UTF8.GetString(body, pos, stdinLen);
                }
            }
        }
        catch { /* 구버전 요청은 body 전체를 파이썬 소스로 취급 */ }

        string tmpDir = Path.Combine(Path.GetTempPath(), "moidapy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            string scriptPath = Path.Combine(tmpDir, "script.py");
            File.WriteAllBytes(scriptPath, src);   // 업로드 바이트 그대로(소스의 인코딩 선언 존중)
            return RunPythonFile(interp, scriptPath, tmpDir, stdin, tmpDir);
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
        }
    }

    // 압축 트리 번들([targetLen][target][count]([pathLen][path][dataLen][data])*[stdin][cwd])을 임시폴더에 복원 후
    // 지정한 프로젝트 cwd 에서 target 스크립트 실행 — 같은 압축의 옆 파일 import·상대경로 읽기 지원
    static string RunPythonBundle(byte[] body)
    {
        string interp = FindPython();
        if (interp == null) throw new PythonMissingException();

        int pos = 0;
        string target = ReadBundleString(body, ref pos);
        int count = ReadBundleInt(body, ref pos);
        if (count < 0 || count > 100000) throw new Exception("bad-bundle");
        string targetSafe = SafeRelPath(target);
        if (targetSafe == null) throw new Exception("bad-target");
        string stdin = "";
        string requestedCwd = "";

        string tmpDir = Path.Combine(Path.GetTempPath(), "moidapy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            for (int i = 0; i < count; i++)
            {
                string rel = ReadBundleString(body, ref pos);
                int len = ReadBundleInt(body, ref pos);
                if (len < 0 || pos + len > body.Length) throw new Exception("bad-bundle");
                string safe = SafeRelPath(rel);
                if (safe != null)
                {
                    string full = Path.Combine(tmpDir, safe);
                    string dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    using (FileStream fs = new FileStream(full, FileMode.Create, FileAccess.Write))
                        fs.Write(body, pos, len);
                }
                pos += len;   // 안전하지 않은 경로는 건너뛰되 스트림 위치는 그대로 진행
            }

            // 새 클라이언트는 파일 목록 뒤에 표준 입력 문자열을 붙인다. 없으면 구버전 번들이다.
            if (pos < body.Length) stdin = ReadBundleString(body, ref pos);
            if (pos < body.Length) requestedCwd = ReadBundleString(body, ref pos);
            if (pos != body.Length) throw new Exception("bad-bundle");

            string targetFull = Path.Combine(tmpDir, targetSafe);
            if (!File.Exists(targetFull)) throw new Exception("target-not-found");
            string workDir = ResolveBundleWorkDir(tmpDir, requestedCwd, Path.GetDirectoryName(targetFull));
            return RunPythonFile(interp, targetFull, workDir, stdin, tmpDir);
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
        }
    }

    static int ReadBundleInt(byte[] b, ref int pos)
    {
        if (pos < 0 || pos + 4 > b.Length) throw new Exception("bad-bundle");
        int v = b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16) | (b[pos + 3] << 24);
        pos += 4;
        return v;
    }

    static string ReadBundleString(byte[] b, ref int pos)
    {
        int len = ReadBundleInt(b, ref pos);
        if (len < 0 || pos + len > b.Length) throw new Exception("bad-bundle");
        string s = Encoding.UTF8.GetString(b, pos, len);
        pos += len;
        return s;
    }

    // zip-slip 방지: 절대경로·드라이브·".." 상위 탈출·잘못된 문자를 차단하고 상대경로로 정규화(null=거부)
    static string SafeRelPath(string rel)
    {
        if (string.IsNullOrEmpty(rel)) return null;
        rel = rel.Replace('\\', '/');
        char[] invalid = Path.GetInvalidFileNameChars();
        List<string> keep = new List<string>();
        foreach (string raw in rel.Split('/'))
        {
            string seg = raw.Trim();
            if (seg == "" || seg == ".") continue;
            if (seg == "..") return null;
            bool bad = false;
            foreach (char c in invalid) if (seg.IndexOf(c) >= 0) { bad = true; break; }
            if (bad) return null;
            keep.Add(seg);
        }
        if (keep.Count == 0) return null;
        return string.Join(Path.DirectorySeparatorChar.ToString(), keep.ToArray());
    }

    static string ResolveBundleWorkDir(string root, string requested, string fallback)
    {
        if (string.IsNullOrWhiteSpace(requested)) return fallback;
        string safe = SafeRelPath(requested);
        if (safe == null) throw new Exception("bad-cwd");
        string full = Path.Combine(root, safe);
        Directory.CreateDirectory(full);
        return full;
    }

    static string RunPythonFile(string interp, string scriptPath, string workDir, string stdin, string projectRoot)
    {
        string runnerPath = Path.Combine(Path.GetTempPath(), "moidapy_runner_" + Guid.NewGuid().ToString("N") + ".py");
        string plotDir = Path.Combine(Path.GetTempPath(), "moidapy_plots_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plotDir);
        File.WriteAllText(runnerPath,
            "import os, runpy, sys\n" +
            "sys.argv[0] = os.environ['CLASSDOCK_SCRIPT']\n" +
            "_ps_script_dir = os.path.dirname(os.environ['CLASSDOCK_SCRIPT'])\n" +
            "_ps_project_root = os.environ.get('CLASSDOCK_PROJECT_ROOT', '')\n" +
            "_ps_paths = []\n" +
            "_ps_cur = _ps_script_dir\n" +
            "while _ps_cur:\n" +
            "    _ps_paths.append(_ps_cur)\n" +
            "    if _ps_project_root and os.path.normcase(os.path.abspath(_ps_cur)) == os.path.normcase(os.path.abspath(_ps_project_root)):\n" +
            "        break\n" +
            "    _ps_next = os.path.dirname(_ps_cur)\n" +
            "    if _ps_next == _ps_cur:\n" +
            "        break\n" +
            "    _ps_cur = _ps_next\n" +
            "for _ps_path in reversed(_ps_paths):\n" +
            "    if _ps_path and _ps_path not in sys.path:\n" +
            "        sys.path.insert(0, _ps_path)\n" +
            "try:\n" +
            "    runpy.run_path(os.environ['CLASSDOCK_SCRIPT'], run_name='__main__')\n" +
            "finally:\n" +
            "    try:\n" +
            "        import matplotlib.pyplot as _ps_plt\n" +
            "        for _ps_i, _ps_n in enumerate(_ps_plt.get_fignums()[:8]):\n" +
            "            _ps_plt.figure(_ps_n).savefig(os.path.join(os.environ['CLASSDOCK_PLOT_DIR'], 'plot_%02d.png' % _ps_i), bbox_inches='tight')\n" +
            "        _ps_plt.close('all')\n" +
            "    except Exception:\n" +
            "        pass\n",
            new UTF8Encoding(false));

        // -X utf8: UTF-8 모드(한글 출력 깨짐 방지). py 런처는 -3 으로 파이썬3 고정.
        string args = (interp == "py" ? "-3 " : "") + "-X utf8 \"" + runnerPath + "\"";

        ProcessStartInfo psi = new ProcessStartInfo(interp, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.WorkingDirectory = workDir;
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.EnvironmentVariables["MPLBACKEND"] = "Agg";
        psi.EnvironmentVariables["PYTHONDONTWRITEBYTECODE"] = "1";   // 세션 실행과 동일하게 __pycache__ 찌꺼기를 남기지 않는다
        psi.EnvironmentVariables["CLASSDOCK_SCRIPT"] = scriptPath;
        psi.EnvironmentVariables["CLASSDOCK_PROJECT_ROOT"] = string.IsNullOrEmpty(projectRoot) ? workDir : projectRoot;
        psi.EnvironmentVariables["CLASSDOCK_PLOT_DIR"] = plotDir;

        LimitedTextBuffer outSb = new LimitedTextBuffer();
        LimitedTextBuffer errSb = new LimitedTextBuffer();
        int exitCode = -1;
        Process proc = null;
        Thread outReader = null, errReader = null;
        try
        {
            proc = new Process();
            proc.StartInfo = psi;
            proc.Start();
            outReader = StartLimitedReader(proc.StandardOutput, outSb);
            errReader = StartLimitedReader(proc.StandardError, errSb);
            try
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(stdin ?? "");
                if (inputBytes.Length > 0) proc.StandardInput.BaseStream.Write(inputBytes, 0, inputBytes.Length);
                proc.StandardInput.BaseStream.Flush();
                proc.StandardInput.BaseStream.Close();      // 준비한 UTF-8 입력을 모두 전달한 뒤 EOF
            }
            catch { }

            bool timedOut = false;
            bool memoryLimit = false;
            Stopwatch watch = Stopwatch.StartNew();
            while (!proc.WaitForExit(250))
            {
                if (watch.ElapsedMilliseconds >= 60000) { timedOut = true; break; }
                if (ProcessTreeWorkingSetBytes(proc.Id) > PythonProcessMemoryLimitBytes) { memoryLimit = true; break; }
            }
            if (timedOut || memoryLimit)
            {
                KillProcessTree(proc);
                try { proc.WaitForExit(2000); } catch { }
                errSb.AppendLine(memoryLimit
                    ? "[메모리 제한: 실행이 4GB를 넘어 중단했습니다.]"
                    : "[시간 초과: 60초를 넘겨 실행을 중단했습니다.]");
                exitCode = -1;
            }
            else
            {
                proc.WaitForExit();                          // 비동기 출력 버퍼 flush 보장
                exitCode = proc.ExitCode;
            }
            try { if (outReader != null) outReader.Join(2000); if (errReader != null) errReader.Join(2000); } catch { }
        }
        finally
        {
            if (proc != null) { try { proc.Dispose(); } catch { } }
        }

        string imagesJson = "[]";
        try
        {
            string[] files = Directory.GetFiles(plotDir, "*.png");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            StringBuilder images = new StringBuilder("[");
            int count = Math.Min(files.Length, 8);
            for (int i = 0; i < count; i++)
            {
                byte[] bytes = File.ReadAllBytes(files[i]);
                if (bytes.Length > 8 * 1024 * 1024) continue;
                if (images.Length > 1) images.Append(',');
                images.Append(JsonString("data:image/png;base64," + Convert.ToBase64String(bytes)));
            }
            images.Append(']');
            imagesJson = images.ToString();
        }
        catch { }
        finally
        {
            try { if (File.Exists(runnerPath)) File.Delete(runnerPath); } catch { }
            try { if (Directory.Exists(plotDir)) Directory.Delete(plotDir, true); } catch { }
        }

        return "{\"stdout\":" + JsonString(outSb.GetText())
             + ",\"stderr\":" + JsonString(errSb.GetText())
             + ",\"code\":" + exitCode
             + ",\"images\":" + imagesJson + "}";
    }

    static string JsonString(string s)
    {
        if (s == null) s = "";
        StringBuilder sb = new StringBuilder(s.Length + 16);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    static string FlattenMessage(Exception ex)
    {
        if (ex == null) return "";
        while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
        string msg = ex.Message;
        if (ex.InnerException != null) msg += " / " + FlattenMessage(ex.InnerException);
        return msg;
    }

    static object Get(object o, string name)
    {
        return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);
    }
    static object Invoke(object o, string name, object[] args)
    {
        return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
    }
    static object InvokeRetry(object o, string name, object[] args)
    {
        Exception last = null;
        for (int i = 0; i < 30; i++)
        {
            try { return Invoke(o, name, args); }
            catch (Exception ex)
            {
                last = ex;
                if (!IsComBusy(ex)) throw;
                Thread.Sleep(250);
            }
        }
        throw last;
    }
    static bool IsComBusy(Exception ex)
    {
        while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
        COMException ce = ex as COMException;
        if (ce == null) return false;
        return ce.ErrorCode == unchecked((int)0x80010001) ||  // RPC_E_CALL_REJECTED
               ce.ErrorCode == unchecked((int)0x8001010A);    // RPC_E_SERVERCALL_RETRYLATER
    }
    static void TrySet(object o, string name, object val)
    {
        try { o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new object[] { val }); }
        catch { }
    }

    // ===================== 시험 제출 받기(교실 LAN) =====================
    // 학생 EXE 가 선생님 EXE 로 제출본(.examdone)을 바로 보내는 통로.
    //
    // 기본 서버(루프백)에는 /save-file, /workspace-save 처럼 디스크를 건드리는 통로가 열려 있다.
    // 거기에 외부 접속을 허용하면 제출 하나 받자고 그 통로 전체를 교실 네트워크에 내놓게 되므로,
    // 선생님이 [제출 받기]를 켠 동안에만 열리는 '제출 전용' 리스너를 따로 둔다.
    // 이 리스너가 아는 경로는 아래 셋뿐이고 나머지는 전부 404 다.
    //   OPTIONS /exam-submit   CORS·사설망(PNA) 사전 요청
    //   GET     /exam-hello    학생의 [연결 확인] — 세션 열림 여부와 시험 제목만
    //   POST    /exam-submit   제출본 접수
    // 학생 PC 에는 이 PC 의 로컬 토큰이 없으므로 토큰 대신 '세션 코드 + 열린 동안만 + 경로 화이트리스트'로 막는다.
    // 제출본 자체는 이미 선생님 공개키로 봉인돼 있어 평문 HTTP 로 실어도 내용은 새지 않는다.
    const int ExamReceiveMaxItems = 300;                 // 세션당 총 접수 상한
    const int ExamReceiveMaxBodyBytes = 1024 * 1024;     // 제출본 1개 상한
    const int ExamReceivePerIpPerMinute = 20;            // 같은 IP 도배 차단
    static readonly object ExamReceiveLock = new object();
    static TcpListener ExamReceiveListener = null;
    static int ExamReceivePort = 0;
    static string ExamReceiveCode = "";
    static string ExamReceiveExamId = "";
    static string ExamReceiveTitle = "";
    static DateTime ExamReceiveLastActivity = DateTime.MinValue;
    static readonly List<string> ExamReceiveItems = new List<string>();      // 접수한 .examdone 원문(색인 = seq)
    static readonly HashSet<string> ExamReceiveSeen = new HashSet<string>(StringComparer.Ordinal);
    static readonly Dictionary<string, int> ExamReceiveRate = new Dictionary<string, int>(StringComparer.Ordinal);
    static DateTime ExamReceiveRateWindow = DateTime.MinValue;
    static readonly TimeSpan ExamReceiveIdleTimeout = TimeSpan.FromHours(3);

    static string ExamReceiveNewCode()
    {
        byte[] buf = new byte[4];
        using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) rng.GetBytes(buf);
        int value = (int)(BitConverter.ToUInt32(buf, 0) % 1000000u);
        return value.ToString("D6");
    }

    // 학생에게 불러 줄 이 PC 의 주소. 172.16~31 을 포함한 사설 대역만 후보로 둔다.
    static List<string> ExamReceiveAddresses()
    {
        List<string> found = new List<string>();
        try
        {
            foreach (IPAddress ip in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                byte[] b = ip.GetAddressBytes();
                if (b[0] == 127) continue;
                bool priv = (b[0] == 10) || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
                if (priv) found.Add(ip.ToString());
            }
        }
        catch { }
        return found;
    }

    static string ExamReceiveStatusJson(int since)
    {
        StringBuilder sb = new StringBuilder(512);
        lock (ExamReceiveLock)
        {
            bool open = ExamReceiveListener != null;
            sb.Append("{\"open\":").Append(open ? "true" : "false");
            sb.Append(",\"port\":").Append(ExamReceivePort);
            sb.Append(",\"code\":").Append(JsonString(ExamReceiveCode));
            sb.Append(",\"title\":").Append(JsonString(ExamReceiveTitle));
            sb.Append(",\"total\":").Append(ExamReceiveItems.Count);
            sb.Append(",\"addresses\":[");
            List<string> addr = ExamReceiveAddresses();
            for (int i = 0; i < addr.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonString(addr[i]));
            }
            sb.Append("]");
            // since 이후로 새로 들어온 것만 원문 그대로 실어 보낸다(한 번에 최대 20개).
            if (since < 0) since = 0;
            sb.Append(",\"since\":").Append(since).Append(",\"items\":[");
            int sent = 0;
            for (int i = since; i < ExamReceiveItems.Count && sent < 20; i++, sent++)
            {
                if (sent > 0) sb.Append(',');
                sb.Append("{\"seq\":").Append(i + 1).Append(",\"payload\":").Append(ExamReceiveItems[i]).Append('}');
            }
            sb.Append("]}");
        }
        return sb.ToString();
    }

    static string ExamReceiveStart(string examId, string title)
    {
        lock (ExamReceiveLock)
        {
            if (ExamReceiveListener != null)
            {
                ExamReceiveExamId = examId ?? "";
                if (!string.IsNullOrEmpty(title)) ExamReceiveTitle = title;
                ExamReceiveLastActivity = DateTime.UtcNow;
                return ExamReceiveStatusJson(0);
            }
            TcpListener started = null;
            int chosen = 0;
            for (int cand = 17650; cand <= 17659 && started == null; cand++)
            {
                try
                {
                    TcpListener l = new TcpListener(IPAddress.Any, cand);
                    l.Start();
                    started = l;
                    chosen = cand;
                }
                catch { /* 점유·차단 → 다음 후보 */ }
            }
            if (started == null) return "{\"open\":false,\"error\":\"listen-failed\"}";

            ExamReceiveListener = started;
            ExamReceivePort = chosen;
            ExamReceiveCode = ExamReceiveNewCode();
            ExamReceiveExamId = examId ?? "";
            ExamReceiveTitle = title ?? "";
            ExamReceiveLastActivity = DateTime.UtcNow;
            ExamReceiveItems.Clear();
            ExamReceiveSeen.Clear();
            ExamReceiveRate.Clear();
            ExamReceiveRateWindow = DateTime.UtcNow;

            TcpListener captured = started;
            Thread accept = new Thread(delegate() { ExamReceiveAcceptLoop(captured); });
            accept.IsBackground = true;
            accept.Start();
            return ExamReceiveStatusJson(0);
        }
    }

    static void ExamReceiveStop()
    {
        TcpListener closing = null;
        lock (ExamReceiveLock)
        {
            closing = ExamReceiveListener;
            ExamReceiveListener = null;
            ExamReceivePort = 0;
            ExamReceiveCode = "";
        }
        if (closing != null) { try { closing.Stop(); } catch { } }
    }

    static void ExamReceiveAcceptLoop(TcpListener listener)
    {
        while (true)
        {
            lock (ExamReceiveLock) { if (ExamReceiveListener != listener) break; }
            TcpClient client = null;
            try { client = listener.AcceptTcpClient(); }
            catch { break; }   // Stop() 으로 닫힌 경우
            // 아무도 보내지 않는 채로 오래 열려 있으면 스스로 닫는다(켜 둔 걸 잊는 사고 방지).
            lock (ExamReceiveLock)
            {
                if (ExamReceiveListener == listener && DateTime.UtcNow - ExamReceiveLastActivity > ExamReceiveIdleTimeout)
                {
                    try { client.Close(); } catch { }
                    break;
                }
            }
            TcpClient captured = client;
            Thread worker = new Thread(delegate() { ExamReceiveHandle(captured); });
            worker.IsBackground = true;
            worker.Start();
        }
        try { listener.Stop(); } catch { }
        lock (ExamReceiveLock) { if (ExamReceiveListener == listener) { ExamReceiveListener = null; ExamReceivePort = 0; ExamReceiveCode = ""; } }
    }

    static void ExamReceiveWrite(Stream stream, string status, string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
        string header =
            "HTTP/1.1 " + status + "\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            "Content-Length: " + payload.Length + "\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Private-Network: true\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(payload, 0, payload.Length);
    }

    static bool ExamReceiveRateOk(string ip)
    {
        lock (ExamReceiveLock)
        {
            if (DateTime.UtcNow - ExamReceiveRateWindow > TimeSpan.FromMinutes(1))
            {
                ExamReceiveRate.Clear();
                ExamReceiveRateWindow = DateTime.UtcNow;
            }
            int used;
            ExamReceiveRate.TryGetValue(ip, out used);
            if (used >= ExamReceivePerIpPerMinute) return false;
            ExamReceiveRate[ip] = used + 1;
            return true;
        }
    }

    // 제출본은 평문 메타데이터(format·examId·student 등)와 봉인 덩어리로 이뤄진 최상위 JSON 이다.
    // 여기서 보는 값은 파일 이름과 대조용일 뿐이라 완전한 파서 대신 최상위 문자열만 훑어 꺼낸다.
    static string ExamJsonString(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return "";
        string needle = "\"" + key + "\"";
        int at = json.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return "";
        int i = at + needle.Length;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
        if (i >= json.Length || json[i] != ':') return "";
        i++;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
        if (i >= json.Length || json[i] != '"') return "";
        i++;
        StringBuilder sb = new StringBuilder(64);
        while (i < json.Length && json[i] != '"')
        {
            if (json[i] == '\\' && i + 1 < json.Length)
            {
                i++;
                char esc = json[i];
                if (esc == 'n') sb.Append('\n');
                else if (esc == 't') sb.Append('\t');
                else if (esc == 'u' && i + 4 < json.Length)
                {
                    int code;
                    if (int.TryParse(json.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out code)) sb.Append((char)code);
                    i += 4;
                }
                else sb.Append(esc);
            }
            else sb.Append(json[i]);
            i++;
            if (sb.Length > 400) break;
        }
        return sb.ToString();
    }

    static string ExamSafeNameToken(string raw, string fallback)
    {
        if (raw == null) raw = "";
        StringBuilder sb = new StringBuilder(raw.Length);
        foreach (char c in raw.Trim())
        {
            if (char.IsControl(c)) continue;
            if ("\\/:*?\"<>|".IndexOf(c) >= 0) { sb.Append('_'); continue; }
            sb.Append(c);
            if (sb.Length >= 40) break;
        }
        string cleaned = sb.ToString().Trim().TrimEnd('.');
        return cleaned.Length > 0 ? cleaned : fallback;
    }

    static void ExamReceiveHandle(TcpClient client)
    {
        string ip = "?";
        try
        {
            IPEndPoint remote = client.Client.RemoteEndPoint as IPEndPoint;
            if (remote != null) ip = remote.Address.ToString();
        }
        catch { }
        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 15000;
                List<byte> head = new List<byte>(1024);
                bool complete = false;
                int b;
                while ((b = stream.ReadByte()) != -1)
                {
                    head.Add((byte)b);
                    int n = head.Count;
                    if (n >= 4 && head[n - 4] == 13 && head[n - 3] == 10 && head[n - 2] == 13 && head[n - 1] == 10) { complete = true; break; }
                    if (n > MaxHttpHeaderBytes) { ExamReceiveWrite(stream, "431 Request Header Fields Too Large", "{\"ok\":false}"); return; }
                }
                if (!complete) { ExamReceiveWrite(stream, "400 Bad Request", "{\"ok\":false}"); return; }

                string headerText = Encoding.ASCII.GetString(head.ToArray());
                string[] lines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                string[] rp = (lines.Length > 0 ? lines[0] : "").Split(' ');
                string method = rp.Length > 0 ? rp[0] : "";
                string rawPath = rp.Length > 1 ? rp[1] : "/";
                string path = rawPath;
                string query = "";
                int q = rawPath.IndexOf('?');
                if (q >= 0) { path = rawPath.Substring(0, q); query = rawPath.Substring(q + 1); }

                int contentLength = 0;
                string code = "";
                for (int i = 1; i < lines.Length; i++)
                {
                    int c = lines[i].IndexOf(':');
                    if (c <= 0) continue;
                    string key = lines[i].Substring(0, c).Trim();
                    string val = lines[i].Substring(c + 1).Trim();
                    if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out contentLength);
                    else if (key.Equals("X-Exam-Code", StringComparison.OrdinalIgnoreCase)) code = val;
                }

                if (method == "OPTIONS")
                {
                    // 학생 앱은 127.0.0.1 오리진이라 크로스 오리진 + 사설망 사전 요청이 먼저 온다.
                    string preflight =
                        "HTTP/1.1 204 No Content\r\n" +
                        "Access-Control-Allow-Origin: *\r\n" +
                        "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                        "Access-Control-Allow-Headers: *\r\n" +
                        "Access-Control-Allow-Private-Network: true\r\n" +
                        "Access-Control-Max-Age: 600\r\n" +
                        "Content-Length: 0\r\n" +
                        "Connection: close\r\n" +
                        "\r\n";
                    byte[] pre = Encoding.ASCII.GetBytes(preflight);
                    stream.Write(pre, 0, pre.Length);
                    return;
                }

                bool open;
                string wantCode, wantExamId, title;
                int total;
                lock (ExamReceiveLock)
                {
                    open = ExamReceiveListener != null;
                    wantCode = ExamReceiveCode;
                    wantExamId = ExamReceiveExamId;
                    title = ExamReceiveTitle;
                    total = ExamReceiveItems.Count;
                }
                if (!open) { ExamReceiveWrite(stream, "409 Conflict", "{\"ok\":false,\"error\":\"closed\"}"); return; }
                if (!ExamReceiveRateOk(ip)) { ExamReceiveWrite(stream, "429 Too Many Requests", "{\"ok\":false,\"error\":\"too-many\"}"); return; }

                if (query.IndexOf("code=", StringComparison.Ordinal) >= 0 && code.Length == 0)
                {
                    foreach (string part in query.Split('&'))
                    {
                        if (part.StartsWith("code=", StringComparison.Ordinal)) code = Uri.UnescapeDataString(part.Substring(5));
                    }
                }
                if (code != wantCode) { ExamReceiveWrite(stream, "403 Forbidden", "{\"ok\":false,\"error\":\"bad-code\"}"); return; }

                if (method == "GET" && path == "/exam-hello")
                {
                    ExamReceiveWrite(stream, "200 OK", "{\"ok\":true,\"title\":" + JsonString(title) + ",\"total\":" + total + "}");
                    return;
                }
                if (method != "POST" || path != "/exam-submit")
                {
                    ExamReceiveWrite(stream, "404 Not Found", "{\"ok\":false,\"error\":\"unknown\"}");
                    return;
                }
                if (contentLength <= 0 || contentLength > ExamReceiveMaxBodyBytes)
                {
                    ExamReceiveWrite(stream, "413 Payload Too Large", "{\"ok\":false,\"error\":\"too-large\"}");
                    return;
                }

                byte[] body = new byte[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int got = stream.Read(body, read, contentLength - read);
                    if (got <= 0) break;
                    read += got;
                }
                if (read != contentLength) { ExamReceiveWrite(stream, "400 Bad Request", "{\"ok\":false,\"error\":\"incomplete\"}"); return; }

                string json = Encoding.UTF8.GetString(body).Trim();
                if (!json.StartsWith("{", StringComparison.Ordinal) || !json.EndsWith("}", StringComparison.Ordinal)
                    || ExamJsonString(json, "format") != "classdock-exam-result")
                {
                    ExamReceiveWrite(stream, "400 Bad Request", "{\"ok\":false,\"error\":\"bad-format\"}");
                    return;
                }
                string examId = ExamJsonString(json, "examId");
                string student = ExamJsonString(json, "student");
                string examTitle = ExamJsonString(json, "examTitle");
                if (student.Length == 0) { ExamReceiveWrite(stream, "400 Bad Request", "{\"ok\":false,\"error\":\"no-student\"}"); return; }
                if (wantExamId.Length > 0 && examId != wantExamId)
                {
                    ExamReceiveWrite(stream, "409 Conflict", "{\"ok\":false,\"error\":\"other-exam\"}");
                    return;
                }

                string fingerprint;
                using (SHA256 sha = SHA256.Create()) fingerprint = BitConverter.ToString(sha.ComputeHash(body)).Replace("-", "");

                lock (ExamReceiveLock)
                {
                    ExamReceiveLastActivity = DateTime.UtcNow;
                    // 저장은 됐는데 응답이 유실되면 학생 화면엔 실패로 보인다. 다시 눌렀을 때 오류를 내면
                    // "낸 건가 안 낸 건가"가 되므로, 같은 제출본은 조용히 접수 완료로 답한다.
                    if (ExamReceiveSeen.Contains(fingerprint))
                    {
                        ExamReceiveWrite(stream, "200 OK", "{\"ok\":true,\"duplicate\":true,\"receipt\":" + JsonString(fingerprint.Substring(0, 8)) + "}");
                        return;
                    }
                    if (ExamReceiveItems.Count >= ExamReceiveMaxItems)
                    {
                        ExamReceiveWrite(stream, "507 Insufficient Storage", "{\"ok\":false,\"error\":\"full\"}");
                        return;
                    }
                }

                // 파일이 곧 진실이다 — 앱이 죽어도 남고, 파일로 받은 제출과 같은 물건이 된다.
                string folder = "제출함/" + ExamSafeNameToken(examTitle.Length > 0 ? examTitle : title, "시험지");
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string rel = folder + "/" + ExamSafeNameToken(student, "학생") + "_" + stamp + ".examdone";
                string full;
                bool saved = false;
                if (TryResolveSaveRootPath(rel, out full))
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(full);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllBytes(full, body);
                        saved = true;
                    }
                    catch { }
                }
                if (!saved)
                {
                    // 학생 화면이 로컬 .examdone 파일로 폴백할 수 있도록 성공으로 접수하지 않는다.
                    ExamReceiveWrite(stream, "500 Internal Server Error", "{\"ok\":false,\"error\":\"save-failed\"}");
                    return;
                }

                lock (ExamReceiveLock)
                {
                    ExamReceiveSeen.Add(fingerprint);
                    ExamReceiveItems.Add(json);
                }
                ExamReceiveWrite(stream, "200 OK", "{\"ok\":true,\"receipt\":" + JsonString(fingerprint.Substring(0, 8)) + "}");
            }
        }
        catch { /* 끊긴 연결은 조용히 버린다 */ }
    }
}

class PowerPointMissingException : Exception { }
class PythonMissingException : Exception { }
class JavaMissingException : Exception { }
// <video src> 하나에만 쓰는 재생 표. 실제 경로가 아니라 원본 폴더 ID + 상대 경로를 들고 있어,
// 표가 새어 나가도 그 폴더 밖은 열 수 없다.
sealed class MediaTicket
{
    public string RootId;
    public string RelPath;
    public DateTime ExpiresUtc;
}

// 경로 방식 MP4 변환 한 건. 상태·진행률은 HTTP 스레드가 폴링으로 읽는다.
sealed class MediaConvertJob
{
    public string Id;
    public string InPath;
    public string OutPath;
    public string OutName;
    public bool ForceVideoEncode;
    public volatile string State;    // queued | running | done | error | cancelled
    public volatile string Stage;    // remux(모두 복사) | copy(소리 변환) | hardware(GPU) | encode(CPU)
    public volatile string Error;
    public volatile bool Cancelled;
    public Process Proc;             // MediaJobLock 아래에서만 읽고 쓴다
    public long DurationUs;          // Interlocked 로만 다룬다
    public long DoneUs;
    // ffmpeg 가 알려 주는 배속(원본 1초를 몇 초 만에 처리하는지) × 1000. 0 이면 아직 모른다.
    // 남은 시간은 이 값으로 어림한다 — ffmpeg 자신이 쓰는 것과 같은 누적 평균이라 잘 안 흔들린다.
    public long SpeedMilli;
    // 현재 단계가 시작된 시각(UTC ticks). 2차 재인코딩으로 넘어가면 진행률이 0 부터 다시 가므로
    // 경과 시간도 단계별로 다시 센다.
    public long StageStartedTicks;
    public DateTime StartedUtc;
}

class FfmpegMissingException : Exception { }
class DbMismatchException : Exception { }
