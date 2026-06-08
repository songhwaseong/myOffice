/*
 * 오프라인 단일 HTML 빌드 스크립트
 * - pdf-signer.html 의 모든 CDN <script src> / <link> 를 vendor/* 인라인으로 치환
 * - pdf.worker 는 <script type="text/js-worker"> 블록으로 삽입 (런타임에 blob 워커로 사용)
 * 실행:  node build-offline.js
 */
const fs = require("fs");
const path = require("path");

const root = __dirname;
const read = (p) => fs.readFileSync(path.join(root, p), "utf8");
// <script> 안에서 조기 종료(</script>)를 막는 이스케이프
const esc = (code) => code.replace(/<\/script/gi, "<\\/script");

let html = read("pdf-signer.html");

// [CDN URL, vendor 파일] — HTML 에 등장하는 순서와 무관하게 매칭
const scripts = [
  ["https://cdnjs.cloudflare.com/ajax/libs/jquery/3.7.1/jquery.min.js", "jquery.min.js"],
  ["https://cdnjs.cloudflare.com/ajax/libs/jszip/3.10.1/jszip.min.js", "jszip3.min.js"],
  ["https://cdnjs.cloudflare.com/ajax/libs/jszip/2.6.1/jszip.min.js", "jszip.min.js"],
  ["https://cdnjs.cloudflare.com/ajax/libs/jszip-utils/0.1.0/jszip-utils.min.js", "jszip-utils.min.js"],
  ["https://cdn.jsdelivr.net/gh/meshesha/PPTXjs@master/js/divs2slides.min.js", "divs2slides.min.js"],
  ["https://cdn.jsdelivr.net/gh/meshesha/PPTXjs@master/js/pptxjs.min.js", "pptxjs.min.js"],
  ["https://unpkg.com/docx-preview@0.3.5/dist/docx-preview.min.js", "docx-preview.min.js"],
  ["https://cdnjs.cloudflare.com/ajax/libs/xlsx/0.18.5/xlsx.full.min.js", "xlsx.full.min.js"],
  ["https://cdnjs.cloudflare.com/ajax/libs/crypto-js/4.2.0/crypto-js.min.js", "crypto-js.min.js"],
  ["vendor/office-decrypt.js", "office-decrypt.js"],
  ["vendor/hwp.global.js", "hwp.global.js"],
  ["https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js", "pdf.min.js"],   // + worker
  ["https://cdnjs.cloudflare.com/ajax/libs/pdf-lib/1.17.1/pdf-lib.min.js", "pdf-lib.min.js"],
  ["https://cdn.jsdelivr.net/npm/@zip.js/zip.js/dist/zip-full.min.js", "zip-full.min.js"],
];

for (const [url, file] of scripts) {
  const tag = `<script src="${url}"></script>`;
  if (!html.includes(tag)) {
    console.error("CDN script 태그를 찾지 못함:", url);
    process.exit(1);
  }
  let inline = `<script>\n${esc(read("vendor/" + file))}\n</script>`;
  if (file === "pdf.min.js") {
    inline += `\n<script id="pdfWorkerSrc" type="text/js-worker">\n${esc(read("vendor/pdf.worker.min.js"))}\n</script>`;
  }
  // ★ 두번째 인자는 반드시 함수 — 라이브러리 코드 속 $&, $1 등의 특수치환 방지
  html = html.replace(tag, () => inline);
}

// CSS <link> → <style> 인라인
const cssTag = `<link rel="stylesheet" href="https://cdn.jsdelivr.net/gh/meshesha/PPTXjs@master/css/pptxjs.css">`;
if (html.includes(cssTag)) {
  html = html.replace(cssTag, () => `<style>\n${read("vendor/pptxjs.css")}\n</style>`);
}

// 안내 문구도 오프라인용으로 교체
html = html.replace(
  "🔒 모든 처리는 이 브라우저 안에서만 이뤄집니다. 파일은 외부로 전송되지 않아요.",
  "🔒 인터넷 없이 동작합니다. 모든 처리는 이 브라우저 안에서만 이뤄지며 파일은 외부로 전송되지 않아요."
);

const out = "pdf-signer-offline.html";
fs.writeFileSync(path.join(root, out), html, "utf8");
const kb = Math.round(fs.statSync(path.join(root, out)).size / 1024);
console.log(`생성 완료: ${out} (${kb} KB)`);
