"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");
const { spawnSync } = require("node:child_process");
const root = path.join(__dirname, "..");
const csc = ["Framework64", "Framework"].map(framework =>
  path.join(process.env.SystemRoot || "C:/Windows", "Microsoft.NET", framework, "v4.0.30319", "csc.exe")
).find(file => fs.existsSync(file));

test("설정·작업공간 저장은 교체 실패와 파일 잠금에서 원본을 보존하고 임시 파일을 정리한다", {
  skip:process.platform !== "win32" || !csc
}, () => {
  const tempRoot = fs.realpathSync(os.tmpdir());
  const temp = fs.mkdtempSync(path.join(tempRoot, "classdock-workspace-atomic-"));
  try {
    const exe = path.join(temp, "atomic-save-test.exe"), stub = path.join(temp, "stub.txt");
    const launcher = path.join(temp, "launcher.cs");
    fs.writeFileSync(stub, "<html></html>");
    // 실제 소스는 변경하지 않는다. 복사본에서 OS 교체 호출에만 실패 주입 지점을 둔다.
    const source = fs.readFileSync(path.join(root, "desktop/launcher.cs"), "utf8");
    assert.ok(source.includes("File.Move(") && source.includes("File.Replace("));
    fs.writeFileSync(launcher, source.replace(/\bFile\.Move\(/g, "WorkspaceAtomicSaveTest.Move(")
      .replace(/\bFile\.Replace\(/g, "WorkspaceAtomicSaveTest.Replace("));
    const resources = ["app.html", "python_kernel.py", "db_worker.py", "npm_package_runner.js", "ssh_shell_integration.bash"]
      .map(name => "/resource:" + stub + "," + name);
    const compiled = spawnSync(csc, [...resources, "/nologo", "/target:exe", "/main:WorkspaceAtomicSaveTest",
      "/r:System.IO.Compression.dll", "/r:System.Security.dll", "/out:" + exe, launcher,
      path.join(root, "desktop/ssh_terminal.cs"), path.join(root, "desktop/ssh_files.cs"),
      path.join(__dirname, "fixtures/workspace-atomic-save.cs")
    ], { encoding:"utf8", timeout:30000, windowsHide:true });
    assert.equal(compiled.status, 0, compiled.error?.message || compiled.stdout + compiled.stderr);
    const result = spawnSync(exe, [temp], { encoding:"utf8", timeout:30000, windowsHide:true });
    assert.equal(result.status, 0, result.error?.message || result.stdout + result.stderr);
    assert.match(result.stdout, /Workspace atomic checks: 12 scenarios, \d+ assertions, 0 failures/);
  } finally {
    assert.equal(path.dirname(path.resolve(temp)), tempRoot, "cleanup must stay inside the test temporary directory");
    fs.rmSync(temp, { recursive:true, force:true });
  }
});
