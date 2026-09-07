"use strict";

const test = require("node:test");
const path = require("node:path");
const { execFileSync } = require("node:child_process");

// 네이티브 Storage의 이름 속성 동작을 검증한다. 사용자 파일·브라우저 대신 별도 프로세스의 메모리 저장소를 쓴다.
function withNativeStorage(scenario){
  execFileSync(process.execPath, ["--experimental-webstorage", "--no-warnings", "-"], {
    cwd:path.join(__dirname, ".."), encoding:"utf8", timeout:10000,
    input:`
      const assert = require("node:assert/strict");
      const fs = require("node:fs");
      const vm = require("node:vm");
      const storage = globalThis.sessionStorage;
      const prototype = Object.getPrototypeOf(storage);
      const methods = ["setItem", "removeItem", "clear"];
      const descriptors = Object.fromEntries(methods.map(key => [key, Object.getOwnPropertyDescriptor(prototype, key)]));
      const timers = new Map(), events = {}, posts = [], requests = [];
      let timerId = 0, serverStatus = 200, serverBody = "{}", fetchError = null;
      class XHR {
        constructor(){ this.headers = {}; }
        open(method, url, async){ this.method = method; this.url = url; this.async = async; }
        setRequestHeader(key, value){ this.headers[key] = value; }
        send(body){
          if (this.method === "GET"){ this.status = serverStatus; this.responseText = serverBody; }
          else posts.push({ body:JSON.parse(body), headers:this.headers });
        }
      }
      const window = {
        __CLASSDOCK_LOCAL_TOKEN__:"test-local-token",
        XMLHttpRequest:XHR,
        addEventListener(name, fn){ events[name] = fn; },
        fetch:async (url, init) => {
          requests.push({ url, ...init, headers:new Headers(init.headers) });
          if (fetchError) throw fetchError;
          return { ok:true };
        }
      };
      const context = vm.createContext({
        localStorage:storage, window, XMLHttpRequest:XHR, URL, Headers,
        location:{ protocol:"http:", hostname:"127.0.0.1", origin:"http://127.0.0.1:17645", href:"http://127.0.0.1:17645/" },
        setTimeout(fn){ const id = ++timerId; timers.set(id, fn); return id; },
        clearTimeout(id){ timers.delete(id); },
        console:{ warn(){}, error(){} }
      });
      function boot(){ vm.runInContext(fs.readFileSync("src/js/state-sync.js", "utf8"), context); }
      function tick(){
        const pending = [...timers.values()]; timers.clear();
        for (const fn of pending) fn();
      }
      function backup(){
        vm.runInContext(fs.readFileSync("src/js/backup.js", "utf8"), context);
        return vm.runInContext("({ pause:mnBackupPauseLocalStorage, replace:mnBackupReplaceLocalStorage, api:MNBackup })", context);
      }
      (async () => { ${scenario} })().catch(error => { console.error(error); process.exitCode = 1; });
    `
  });
}

test("Storage 메서드 키를 만들지 않고 변경을 묶어 최신 설정을 서버로 보낸다", () => {
  withNativeStorage(`
    serverBody = JSON.stringify({ theme:"dark" });
    boot();
    assert.equal(storage.getItem("theme"), "dark");
    assert.equal(timers.size, 0, "초기 복원은 자동 저장을 예약하지 않는다");
    for (const key of methods) assert.equal(storage.getItem(key), null);
    storage.setItem("theme", "light");
    storage.setItem("temporary", "value");
    storage.removeItem("temporary");
    assert.equal(timers.size, 1);
    tick();
    assert.deepEqual(posts.map(post => post.body), [{ theme:"light" }]);
    assert.equal(posts[0].headers["X-ClassDock-Token"], "test-local-token");
    storage.clear();
    assert.equal(timers.size, 1);
    tick();
    assert.deepEqual(posts[1].body, {});
    for (const key of methods) assert.equal(storage.getItem(key), null);
  `);
});

test("다른 Storage 수신자와 기존 래퍼는 보존하고 실패한 변경은 전송하지 않는다", () => {
  withNativeStorage(`
    const other = {}, forwarded = [];
    for (const key of methods){
      const original = descriptors[key].value;
      Object.defineProperty(prototype, key, { ...descriptors[key], value:function(...args){
        if (this === other){ forwarded.push([key, ...args]); return "other-storage"; }
        if (args[0] === "blocked") throw new Error("write-blocked");
        return original.apply(this, args);
      } });
    }
    boot();
    for (const key of methods) assert.equal(prototype[key].call(other, "key", "value"), "other-storage");
    assert.deepEqual(forwarded.map(row => row[0]), methods);
    assert.equal(timers.size, 0);
    assert.throws(() => storage.setItem("blocked", "value"), /write-blocked/);
    assert.equal(timers.size, 0);
    storage.setItem("actual", "saved");
    assert.equal(timers.size, 1);
    tick();
    assert.deepEqual(posts[0].body, { actual:"saved" });
    for (const key of methods){
      const actual = Object.getOwnPropertyDescriptor(prototype, key);
      for (const flag of ["enumerable", "configurable", "writable"]) assert.equal(actual[flag], descriptors[key][flag]);
    }
  `);
});

test("메서드 설치 도중 실패하면 이미 설치한 래퍼도 되돌린다", () => {
  withNativeStorage(`
    Object.defineProperty(prototype, "removeItem", { ...descriptors.removeItem, configurable:false, writable:false });
    boot();
    assert.equal(prototype.setItem, descriptors.setItem.value);
    assert.equal(prototype.clear, descriptors.clear.value);
    storage.setItem("normal", "works");
    assert.equal(storage.getItem("normal"), "works");
    assert.equal(timers.size, 0);
    for (const key of methods) assert.equal(storage.getItem(key), null);
  `);
});

for (const mode of ["offline", "server-failed"]){
  test(mode + " 환경에서는 Storage 메서드와 저장 항목을 바꾸지 않는다", () => {
    withNativeStorage(`
      if ("${mode}" === "offline") context.location.protocol = "file:";
      else serverStatus = 503;
      boot();
      for (const key of methods) assert.deepEqual(Object.getOwnPropertyDescriptor(prototype, key), descriptors[key]);
      assert.equal(storage.length, 0);
      storage.setItem("local", "only");
      assert.equal(timers.size, 0);
    `);
  });
}

test("백업 일시정지 중 일반 쓰기는 차단되고 복원 쓰기·재개 후 동기화는 유지된다", () => {
  withNativeStorage(`
    boot();
    storage.setItem("classdock-tabs:v1", "CURRENT");
    tick(); posts.length = 0;
    const api = backup(), wrappers = Object.fromEntries(methods.map(key => [key, prototype[key]]));
    const io = api.pause();
    storage.setItem("classdock-tabs:v1", "STALE");
    storage.removeItem("classdock-tabs:v1");
    storage.clear();
    assert.equal(storage.getItem("classdock-tabs:v1"), "CURRENT");
    assert.equal(timers.size, 0);
    api.replace({ "classdock-tabs:v1":"BACKUP" }, [], io);
    assert.equal(timers.size, 1);
    storage.setItem("classdock-tabs:v1", "STALE");
    tick();
    assert.equal(posts[0].body["classdock-tabs:v1"], "BACKUP");
    for (const key of methods) assert.equal(storage.getItem(key), null);
    io.resume(); io.resume();
    for (const key of methods) assert.equal(prototype[key], wrappers[key]);
    storage.setItem("classdock-tabs:v1", "RESUMED");
    tick();
    assert.equal(posts[1].body["classdock-tabs:v1"], "RESUMED");
  `);
});

test("실제 백업 복원 흐름이 실패해도 Storage 동기화 래퍼가 복구된다", () => {
  withNativeStorage(`
    boot();
    const api = backup();
    context.confirmDialog = async () => true;
    context.toast = () => {};
    context.mnBackupParseRestore = async () => ({
      manifest:{ openBoards:[] }, localStorageData:{}, indexedDbData:{ databases:[] }, workspace:new Uint8Array(0)
    });
    context.mnBackupValidateDbDump = () => {};
    context.mnBackupEnsureDbs = async () => {};
    context.mnBackupRestoreWorkspace = async () => { throw new Error("restore-failed"); };
    assert.equal(await api.api.restoreBackup({}), false);
    storage.setItem("after-failure", "saved");
    assert.equal(timers.size, 1);
    tick();
    assert.equal(posts[0].body["after-failure"], "saved");
  `);
});

test("종료 시 예약을 비우고 최신 설정을 인증 헤더가 있는 keepalive 요청으로 보낸다", () => {
  withNativeStorage(`
    boot();
    storage.setItem("last", "value");
    assert.equal(timers.size, 1);
    events.pagehide();
    assert.equal(timers.size, 0);
    assert.equal(requests.length, 1);
    assert.deepEqual(JSON.parse(requests[0].body), { last:"value" });
    assert.equal(requests[0].keepalive, true);
    assert.equal(requests[0].headers.get("X-ClassDock-Token"), "test-local-token");
    storage.setItem("last", "later-pagehide-handler");
    window.__mnFlushAppState();
    assert.equal(timers.size, 0);
    assert.equal(JSON.parse(requests[1].body).last, "later-pagehide-handler");
  `);
});
