/* 포트(origin) 무관 설정 유지 브리지 — EXE 로컬 서버에 설정을 저장/복원한다.
 *
 * 브라우저 localStorage 는 origin(127.0.0.1:포트)별로 갈린다. 런처가 다른 포트로 뜨면
 * 테마·자동복원 설정·탭 순서 등이 빈 origin 이라 초기화된다. 이를 막기 위해 서버측 단일
 * 저장소(app-state.json)를 원본으로 삼는다.
 *   1) 시작 시 동기 XHR 로 서버 저장분을 받아 localStorage 를 먼저 채운다
 *      (theme.js 등 다른 모듈이 localStorage 를 읽기 전이라 이후 초기 읽기에 그대로 반영된다).
 *   2) 이후 localStorage 변경을 디바운스해서 서버로 미러링한다.
 * 서버가 없는 환경(file:// 오프라인 HTML·일반 브라우저)에서는 아무 것도 하지 않고 기존 방식을 쓴다.
 */
(function () {
  var isLocal = (location.protocol === "http:" || location.protocol === "https:") &&
                (location.hostname === "127.0.0.1" || location.hostname === "localhost");
  if (!isLocal) return;

  var localToken = String(window.__CLASSDOCK_LOCAL_TOKEN__ || "");
  function isSameOriginRequest(input) {
    try {
      var raw = (typeof input === "string") ? input : (input && input.url) || "";
      if (!raw) return false;
      return new URL(raw, location.href).origin === location.origin;
    } catch (_) { return false; }
  }
  if (localToken && window.fetch) {
    try {
      var rawFetch = window.fetch.bind(window);
      window.fetch = function (input, init) {
        try {
          if (isSameOriginRequest(input)) {
            init = init ? Object.assign({}, init) : {};
            var baseHeaders = init.headers || (input && input.headers) || undefined;
            var headers = new Headers(baseHeaders);
            if (!headers.has("X-ClassDock-Token")) headers.set("X-ClassDock-Token", localToken);
            init.headers = headers;
          }
        } catch (_) {}
        return rawFetch(input, init);
      };
    } catch (_) {}
  }

  if (localToken && window.XMLHttpRequest) {
    try {
      var rawOpen = XMLHttpRequest.prototype.open;
      var rawSend = XMLHttpRequest.prototype.send;
      XMLHttpRequest.prototype.open = function () {
        this.__mnSameOrigin = isSameOriginRequest(arguments[1]);
        return rawOpen.apply(this, arguments);
      };
      XMLHttpRequest.prototype.send = function () {
        if (this.__mnSameOrigin) {
          try { this.setRequestHeader("X-ClassDock-Token", localToken); } catch (_) {}
        }
        return rawSend.apply(this, arguments);
      };
    } catch (_) {}
  }

  // 1) 서버 저장분으로 localStorage 하이드레이션(동기 — 모듈 로드 전에 값 확정).
  var serverOk = false;
  try {
    var xhr = new XMLHttpRequest();
    xhr.open("GET", "/app-state", false);   // 동기: 로컬 서버라 즉시 응답. 이후 모든 초기 읽기가 값을 본다.
    xhr.send(null);
    if (xhr.status === 200) {
      serverOk = true;
      if (xhr.responseText) {
        var data = JSON.parse(xhr.responseText);
        if (data && typeof data === "object") {
          for (var k in data) {
            if (!Object.prototype.hasOwnProperty.call(data, k)) continue;
            if (typeof data[k] === "string") {
              try { localStorage.setItem(k, data[k]); } catch (_) {}   // 서버를 원본으로 삼아 덮어씀
            }
          }
        }
      }
    }
  } catch (_) { serverOk = false; }
  if (!serverOk) return;   // 우리 서버가 아니면(다른 로컬 서버 등) 미러링도 하지 않는다.

  // 2) localStorage 변경을 서버로 디바운스 미러링.
  var storage = localStorage;
  var timer = null;

  function snapshot() {
    var snap = {};
    for (var i = 0; i < localStorage.length; i++) {
      var key = localStorage.key(i);
      if (key === null) continue;
      snap[key] = localStorage.getItem(key);
    }
    return snap;
  }
  function push() {
    timer = null;
    try {
      var x = new XMLHttpRequest();
      x.open("POST", "/app-state", true);
      x.setRequestHeader("Content-Type", "application/json");
      x.send(JSON.stringify(snapshot()));
    } catch (_) {}
  }
  function schedule() {
    if (timer) return;
    timer = setTimeout(push, 500);
  }
  function flushForPageHide() {
    if (timer) { clearTimeout(timer); timer = null; }
    try {
      var body = JSON.stringify(snapshot());
      if (window.fetch) {
        window.fetch("/app-state", {
          method: "POST", headers: { "Content-Type": "application/json" }, body: body, keepalive: true
        });
      } else {
        var x = new XMLHttpRequest();
        x.open("POST", "/app-state", false);
        x.setRequestHeader("Content-Type", "application/json");
        x.send(body);
      }
    } catch (_) {}
  }
  window.__mnFlushAppState = flushForPageHide;

  // Storage 인스턴스에 메서드를 대입하면 문자열 저장 항목이 생길 수 있다.
  // 프로토타입을 감싸되 이 localStorage의 성공한 변경만 감지한다.
  // 백업 복원의 쓰기 일시정지도 이 래퍼를 보존하고 복구할 수 있다.
  var replaced = [];
  try {
    var prototype = Object.getPrototypeOf(storage);
    ["setItem", "removeItem", "clear"].forEach(function (name) {
      var descriptor = Object.getOwnPropertyDescriptor(prototype, name);
      if (!descriptor || typeof descriptor.value !== "function") {
        throw new Error("Storage method unavailable: " + name);
      }
      var original = descriptor.value;
      Object.defineProperty(prototype, name, Object.assign({}, descriptor, {
        value: function () {
          var result = original.apply(this, arguments);
          if (this === storage) schedule();
          return result;
        }
      }));
      replaced.push([name, descriptor]);
    });
  } catch (_) {
    // 일부 메서드만 설치된 상태를 남기지 않는다.
    while (replaced.length) {
      var entry = replaced.pop();
      Object.defineProperty(prototype, entry[0], entry[1]);
    }
  }

  // 페이지를 떠나기 전 마지막 변경을 확실히 반영한다. URL 토큰 노출을 막기 위해
  // sendBeacon 대신 헤더를 붙일 수 있는 keepalive fetch를 우선 사용한다.
  window.addEventListener("pagehide", function () {
    if (!timer) return;
    flushForPageHide();
  });
})();
