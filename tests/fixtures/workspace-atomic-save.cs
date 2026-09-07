using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Collections.Generic;

// 앱 Main을 실행하지 않고 실제 저장 함수를 임시 폴더로 돌린다.
// 테스트용 런처 사본의 Move/Replace만 아래 함수로 연결해 교체 직전 실패를 재현한다.
static class WorkspaceAtomicSaveTest
{
    const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;
    static readonly Type Launcher = typeof(ClassDockLauncher);
    static string faultTarget;
    static int checks, scenarios;
    static readonly List<string> failures = new List<string>();

    public static void Move(string from, string to)
    {
        if (to == faultTarget) throw new IOException("simulated commit failure");
        File.Move(from, to);
    }
    public static void Replace(string from, string to, string backup)
    {
        if (to == faultTarget) throw new IOException("simulated commit failure");
        File.Replace(from, to, backup);
    }
    static object Call(string name, params object[] args)
    {
        try { return Launcher.GetMethod(name, Flags).Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    static void Require(bool ok, string message)
    {
        checks++;
        if (!ok) throw new Exception(message);
    }
    static void ExpectFailure(Action action)
    {
        bool failed = false;
        try { action(); }
        catch (IOException) { failed = true; }
        catch (UnauthorizedAccessException) { failed = true; }
        Require(failed, "save must report failure");
    }
    static void EqualFile(string target, byte[] expected)
    {
        Require(File.Exists(target), "original file must still exist");
        Require(Convert.ToBase64String(File.ReadAllBytes(target)) == Convert.ToBase64String(expected), "original bytes must remain");
    }
    static void NoTemps(string root)
    {
        Require(Directory.GetFiles(root, "*.tmp").Length == 0, "temporary files must be cleaned");
    }
    static byte[] Bundle(params string[] pairs)
    {
        using (MemoryStream bytes = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(bytes, Encoding.UTF8))
        {
            writer.Write(pairs.Length / 2);
            for (int i = 0; i < pairs.Length; i += 2)
            {
                byte[] name = Encoding.UTF8.GetBytes(pairs[i]), data = Encoding.UTF8.GetBytes(pairs[i + 1]);
                writer.Write(name.Length); writer.Write(name);
                writer.Write(data.Length); writer.Write(data);
            }
            writer.Flush(); return bytes.ToArray();
        }
    }
    static byte[] Paths(params string[] paths)
    {
        using (MemoryStream bytes = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(bytes, Encoding.UTF8))
        {
            writer.Write(paths.Length);
            foreach (string path in paths)
            {
                byte[] name = Encoding.UTF8.GetBytes(path);
                writer.Write(name.Length); writer.Write(name);
            }
            writer.Flush(); return bytes.ToArray();
        }
    }
    static void Scenario(string root, string name, Action<string> action)
    {
        string folder = Path.Combine(root, (++scenarios).ToString());
        Directory.CreateDirectory(folder);
        Launcher.GetField("WorkspacePath", Flags).SetValue(null, Path.Combine(folder, "workspace.bin"));
        Launcher.GetField("AppStatePath", Flags).SetValue(null, Path.Combine(folder, "app-state.json"));
        try { action(folder); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + failures[failures.Count - 1]); }
        finally { faultTarget = null; }
    }
    static void CheckFailure(string folder, string mode, bool locked)
    {
        string target = Path.Combine(folder, mode == "state" ? "app-state.json" : "workspace.bin");
        byte[] original = mode == "state" ? Encoding.UTF8.GetBytes("{\"theme\":\"dark\"}") : Bundle("old.txt", "original", "keep.txt", "keep");
        File.WriteAllBytes(target, original);
        Action save;
        if (mode == "state") save = delegate { Call("SaveAppState", Encoding.UTF8.GetBytes("{\"theme\":\"light\"}")); };
        else if (mode == "remove") save = delegate { Call("RemoveWorkspaceFiles", Paths("old.txt")); };
        else if (mode == "remove-all") save = delegate { Call("RemoveWorkspaceFiles", Paths("old.txt", "keep.txt")); };
        else save = delegate { Call("SaveWorkspace", Bundle("new.txt", "new"), mode == "replace"); };
        if (locked)
        {
            using (FileStream held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ExpectFailure(save); EqualFile(target, original); NoTemps(folder);
            }
        }
        else
        {
            faultTarget = target;
            ExpectFailure(save); EqualFile(target, original); NoTemps(folder);
        }
    }
    public static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        foreach (string mode in new[] { "state", "replace", "merge", "remove" })
        {
            string selected = mode;
            Scenario(root, mode + " commit failure", folder => CheckFailure(folder, selected, false));
            Scenario(root, mode + " locked destination", folder => CheckFailure(folder, selected, true));
        }
        Scenario(root, "last removal locked", folder => CheckFailure(folder, "remove-all", true));
        Scenario(root, "normal state and workspace saves", delegate(string folder)
        {
            string state = Path.Combine(folder, "app-state.json"), workspace = Path.Combine(folder, "workspace.bin");
            Call("SaveAppState", Encoding.UTF8.GetBytes("{}"));
            Call("SaveAppState", Encoding.UTF8.GetBytes("{\"theme\":\"dark\"}"));
            EqualFile(state, Encoding.UTF8.GetBytes("{\"theme\":\"dark\"}"));
            Require((int)Call("SaveWorkspace", Bundle("a.txt", "A", "b.txt", "B"), true) == 2, "initial count");
            Require((int)Call("SaveWorkspace", Bundle("a.txt", "updated", "c.txt", "C"), false) == 3, "merge count");
            EqualFile(workspace, Bundle("b.txt", "B", "a.txt", "updated", "c.txt", "C"));
            Require((int)Call("RemoveWorkspaceFiles", Paths("b.txt")) == 2, "selective removal count");
            EqualFile(workspace, Bundle("a.txt", "updated", "c.txt", "C"));
            Require((int)Call("RemoveWorkspaceFiles", Paths("a.txt", "c.txt")) == 0, "last removal count");
            Require(!File.Exists(workspace), "last removal deletes workspace");
            Call("SaveWorkspace", Bundle("same.txt", "first", "same.txt", "last"), true);
            EqualFile(workspace, Bundle("same.txt", "last"));
            Call("SaveWorkspace", Bundle(), true);
            EqualFile(workspace, Bundle());
            NoTemps(folder);
        });
        Scenario(root, "invalid merge input preserves original", delegate(string folder)
        {
            string workspace = Path.Combine(folder, "workspace.bin");
            byte[] original = Bundle("original.txt", "original"); File.WriteAllBytes(workspace, original);
            bool failed = false;
            try { Call("SaveWorkspace", new byte[] { 1, 0, 0, 0 }, false); } catch { failed = true; }
            Require(failed, "invalid bundle rejected"); EqualFile(workspace, original); NoTemps(folder);
        });
        Scenario(root, "truncated existing workspace preserves original", delegate(string folder)
        {
            string workspace = Path.Combine(folder, "workspace.bin");
            byte[] original = Bundle("old.txt", "original"); Array.Resize(ref original, original.Length - 1); File.WriteAllBytes(workspace, original);
            bool failed = false;
            try { Call("SaveWorkspace", Bundle("new.txt", "new"), false); } catch (Exception ex) { failed = ex.Message == "bad-workspace"; }
            Require(failed, "truncated workspace rejected");
            EqualFile(workspace, original); NoTemps(folder);
        });
        Console.WriteLine("Workspace atomic checks: " + scenarios + " scenarios, " + checks + " assertions, " + failures.Count + " failures");
        return failures.Count == 0 ? 0 : 1;
    }
}
