"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const loaders = fs.readFileSync(path.join(__dirname, "../src/js/file-loaders.js"), "utf8");
const runContext = fs.readFileSync(path.join(__dirname, "../src/js/python-run-context.js"), "utf8");

function checksum(header, signed = false){
  header.fill(32, 148, 156);
  let sum = 0;
  for (const byte of header) sum += signed && byte >= 128 ? byte - 256 : byte;
  header.write(sum.toString(8).padStart(6, "0") + "\0 ", 148, 8, "ascii");
  return header;
}
function member(name, data = "", type = "0", prefix = ""){
  const bytes = Buffer.from(data), header = Buffer.alloc(512);
  header.write(name, 0, 100, "utf8");
  header.write("0000644\0", 100, 8, "ascii");
  header.write(bytes.length.toString(8).padStart(11, "0") + "\0", 124, 12, "ascii");
  header.write(type, 156, 1, "ascii");
  header.write("ustar\0", 257, 8, "ascii");
  header.write(prefix, 345, 155, "utf8");
  return Buffer.concat([checksum(header), bytes, Buffer.alloc((512 - bytes.length % 512) % 512)]);
}
function withSize(raw, data = ""){
  const bytes = member("file.txt", data);
  bytes.fill(0, 124, 136);
  if (Buffer.isBuffer(raw)) raw.copy(bytes, 124);
  else bytes.write(raw, 124, 12, "ascii");
  checksum(bytes.subarray(0, 512));
  return bytes;
}
function harness(){
  const context = vm.createContext({
    TextDecoder, Blob, File, window:{}, console:{ error(){} }
  });
  vm.runInContext(loaders, context);
  return {
    context,
    parse(bytes){
      context.input = bytes;
      // 음수 크기로 같은 헤더를 되읽는 회귀도 테스트 프로세스를 멈추지 못하게 한다.
      return vm.runInContext("parseTar(input)", context, { timeout:200 });
    }
  };
}
function contents(entries){
  return Array.from(entries, entry => ({ name:entry.name, text:Buffer.from(entry.data).toString("utf8") }));
}

test("TAR 일반 파일·빈 파일·ustar 경로와 블록 경계를 보존한다", () => {
  const bytes = Buffer.concat([
    member("first.txt", "가나다", "0", "folder"),
    member("empty.txt", "", "\0"), member("block.txt", "a".repeat(512), "7"),
    member("last.txt", "end"), Buffer.alloc(1024)
  ]);
  const entries = harness().parse(bytes);
  assert.deepEqual(contents(entries), [
    { name:"folder/first.txt", text:"가나다" }, { name:"empty.txt", text:"" },
    { name:"block.txt", text:"a".repeat(512) }, { name:"last.txt", text:"end" }
  ]);
  assert.equal(entries[0].data.buffer, bytes.buffer, "파일 본문을 복사하지 않는다");
});

test("TAR GNU 긴 이름과 메타·디렉터리·링크 건너뛰기를 유지한다", () => {
  const longName = "folder/".repeat(20) + "긴이름.txt";
  const bytes = Buffer.concat([
    member("././@LongLink", longName + "\0", "L"), member("short", "long name"),
    member("dir/", "", "5"), member("link", "", "2"), member("hard", "", "1"),
    member("PaxHeader", "metadata", "x"), member("global", "metadata", "g"),
    member("next.txt", "next"), Buffer.alloc(1024)
  ]);
  assert.deepEqual(contents(harness().parse(bytes)), [
    { name:longName, text:"long name" }, { name:"next.txt", text:"next" }
  ]);
});

test("TAR 종료 블록이 없거나 하나뿐이어도 완전한 마지막 항목은 읽는다", () => {
  const api = harness();
  assert.equal(api.parse(Buffer.alloc(0)).length, 0);
  assert.equal(api.parse(Buffer.alloc(1024)).length, 0);
  assert.equal(api.parse(member("a.txt", "ok")).length, 1);
  assert.equal(api.parse(Buffer.concat([member("a.txt", "ok"), Buffer.alloc(512)])).length, 1);
});

test("TAR 비ASCII 파일명의 구형 signed 체크섬도 읽는다", () => {
  const bytes = member("한글.txt", "ok");
  checksum(bytes.subarray(0, 512), true);
  assert.deepEqual(contents(harness().parse(bytes)), [{ name:"한글.txt", text:"ok" }]);
});

test("TAR 숫자 필드의 공백·NUL 패딩과 GNU 양수 이진 크기를 읽는다", () => {
  const api = harness();
  assert.equal(api.parse(withSize(" 0000000001 ", "x"))[0].data.length, 1);
  assert.equal(api.parse(withSize("\0".repeat(12)))[0].data.length, 0);
  const binary = Buffer.alloc(12); binary[0] = 0x80; binary[11] = 3;
  assert.deepEqual(contents(api.parse(withSize(binary, "abc"))), [{ name:"file.txt", text:"abc" }]);
});

const damaged = [
  ["음수 크기", () => withSize("-0000001000")],
  ["8진수가 아닌 크기", () => withSize("00000000008")],
  ["숫자 뒤 쓰레기", () => withSize("0000000001x", "x")],
  ["필드 중간 NUL", () => withSize("1\0garbage", "x")],
  ["음수 이진 크기", () => withSize(Buffer.alloc(12, 255))],
  ["안전 정수 범위를 넘는 이진 크기", () => {
    const value = Buffer.alloc(12); value[0] = 0x80; value[4] = 0x20;
    return withSize(value);
  }],
  ["파일 끝을 넘는 선언 크기", () => withSize("77777777777")],
  ["잘린 헤더", () => member("file.txt", "x").subarray(0, 511)],
  ["잘린 본문", () => member("file.txt", "abc").subarray(0, 514)],
  ["잘린 데이터 패딩", () => member("file.txt", "abc").subarray(0, 1023)],
  ["마지막 불완전 헤더", () => Buffer.concat([member("file.txt", "abc"), Buffer.from("broken")])],
  ["깨진 체크섬", () => { const bytes = member("file.txt", "x"); bytes[0] ^= 1; return bytes; }],
  ["잘못된 체크섬 숫자", () => { const bytes = member("file.txt", "x"); bytes.write("0000008\0", 148); return bytes; }],
  ["생략 대상 메타의 잘린 본문", () => member("metadata", "abc", "x").subarray(0, 514)]
];
for (const [label, make] of damaged){
  test("손상된 TAR 거부: " + label, () => {
    assert.throws(() => harness().parse(make()), /손상된 TAR:/);
  });
}

test("뒤 항목이 손상되면 앞 항목도 부분 추출하지 않고 기존 오류 안내로 끝낸다", async () => {
  const api = harness(), calls = [];
  Object.assign(api.context, {
    showLoading:() => calls.push("show"), hideLoading:() => calls.push("hide"),
    toast:message => calls.push(message),
    ZIP_OPENABLE:["txt"], fileExtOf:name => name.split(".").pop(),
    makeGroup:() => { calls.push("group"); throw new Error("그룹 생성 전에 검증해야 한다"); },
    handleFiles:() => { throw new Error("파일 열기 전에 검증해야 한다"); }
  });
  const broken = withSize("77777777777");
  const bytes = Buffer.concat([member("good.txt", "ok"), broken]);
  api.context.file = new File([bytes], "broken.tar");
  await vm.runInContext("loadTar(file)", api.context);
  assert.deepEqual(calls, ["show", "tar 파일을 열지 못했습니다.", "hide"]);
});

test("TAR 실행용 옆 파일 추출도 동일한 파서 검증을 거친다", () => {
  const api = harness();
  vm.runInContext(runContext, api.context);
  api.context.input = member("folder/main.py", "print(1)");
  const entries = vm.runInContext("tarTreeAll(input)", api.context);
  assert.equal(entries[0].path, "folder/main.py");
  assert.equal(Buffer.from(entries[0].bytes).toString(), "print(1)");
  api.context.input = withSize("00000000008");
  assert.throws(() => vm.runInContext("tarTreeAll(input)", api.context), /손상된 TAR:/);
});

