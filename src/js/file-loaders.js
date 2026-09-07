"use strict";

const UNKNOWN_TEXT_SAMPLE_BYTES = 8192;
const UNKNOWN_TEXT_ARCHIVE_PROBE_CAP = 32 * 1024 * 1024;

// 등록되지 않은 확장자라도 실제 내용이 텍스트라면 안전하게 연다. NUL/제어문자가 많은
// 파일은 이진으로 보고 건너뛰므로 임의의 바이너리를 텍스트로 손상시키지 않는다.
function isLikelyTextBytes(bytes){
  const view = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes || 0);
  if (view.length >= 2 && ((view[0] === 0xFF && view[1] === 0xFE) || (view[0] === 0xFE && view[1] === 0xFF))) return true;
  let ctrl = 0;
  for (let i = 0; i < view.length; i++){
    const b = view[i];
    if (b === 0) return false;
    if (b < 9 || (b > 13 && b < 32)) ctrl++;
  }
  return !view.length || ctrl / view.length <= 0.1;
}
async function isLikelyTextFile(file){
  try { return isLikelyTextBytes(new Uint8Array(await file.slice(0, UNKNOWN_TEXT_SAMPLE_BYTES).arrayBuffer())); }
  catch(_){ return false; }
}
function isUnknownTextArchiveCandidate(ext, size){
  return !ZIP_OPENABLE.includes(ext) && (Number(size) || 0) <= UNKNOWN_TEXT_ARCHIVE_PROBE_CAP;
}

/* ===== 큰 파일은 열기 전에 한 번 묻는다 =====
   file.size 는 지금껏 "이미 열린 파일인가"를 가리는 키로만 쓰였고 경계로는 쓰이지 않았다.
   영상(1GB)과 백업에만 상한이 있고 PDF·표·압축은 통째로 ArrayBuffer 에 올린다. 큰 파일 하나를
   잘못 열면 화면이 죽는데, 그때 사라지는 건 그 파일이 아니라 열려 있던 '다른 탭의 저장하지 않은
   편집'이다. 자동복원이 받쳐 주지만 마지막 저장 이후는 잃는다.

   막지 않고 묻기만 한다 — 실제로 어디서 죽는지는 기기 메모리에 따라 다르고, 교실 PC 에서 큰
   자료를 여는 것 자체는 정당한 일이다. 판단에 필요한 것(파일 크기·무슨 일이 생길 수 있는지)을
   주고 고르게 한다.

   상한은 "파일 크기가 메모리에서 몇 배로 부푸는가"로 나눴다.
     · 표    : 셀 하나가 JS 객체가 되어 가장 크게 부푼다 → 가장 낮게
     · 압축  : 풀면서 커지고, 그 안의 파일을 다시 연다
     · PDF   : 원본 버퍼는 들고 있지만 페이지는 보이는 것만 그리고 벗어나면 캔버스를 비운다 → 가장 여유롭게
   영상·오디오는 <video> 가 스트리밍으로 읽고 이미 1GB 상한이 있어 빼고,
   sqlite 는 런처가 디스크에서 직접 읽으므로 뺀다. */
const OPEN_CONFIRM_LIMITS = {
  xlsx:40, xlsm:40, xlsb:40, xls:40, ods:40,
  zip:250, tar:250, gz:250, tgz:250,
  pdf:400
};
const OPEN_CONFIRM_DEFAULT_MB = 150;

function openConfirmLimitBytes(ext){
  if (VIDEO_EXTS.includes(ext) || AUDIO_EXTS.includes(ext) || SQLITE_EXTS.includes(ext)) return 0;   // 0 = 묻지 않는다
  return (OPEN_CONFIRM_LIMITS[ext] || OPEN_CONFIRM_DEFAULT_MB) * 1024 * 1024;
}
function openSizeText(bytes){
  const mb = (Number(bytes) || 0) / (1024 * 1024);
  return mb >= 1024 ? (mb / 1024).toFixed(1) + "GB" : Math.round(mb) + "MB";
}
/* 사용자가 직접 연 파일에만 묻는다.
   복원은 사용자가 그 자리에 없고, 폴더·압축 안의 파일은 파일마다 창을 띄우면 진행이 막힌다
   (그 경로는 지금도 상한이 없다 — 폴더 안의 아주 큰 파일은 여전히 화면을 죽일 수 있다). */
async function confirmLargeFileOpen(file, ext, options){
  if (options.restoreFromWorkspace || options.isScratch || options.transient) return true;
  if (options.parentId || options.archiveCtx) return true;
  const limit = openConfirmLimitBytes(ext);
  const size = Number(file && file.size) || 0;
  if (!limit || size <= limit) return true;
  if (typeof confirmDialog !== "function") return true;   // 확인 창을 쓸 수 없으면 예전처럼 그냥 연다
  // confirmDialog 는 넘겨받은 글을 그대로 DOM 에 쓰므로(정적 셸이 아니라 번역 스캔을 거치지 않는다)
  // 여기서 번역해서 넘긴다.
  const i18n = (typeof MNI18N === "object" && MNI18N) || null;
  const ask = (template, vars) => (i18n && typeof i18n.tf === "function")
    ? i18n.tf(template, vars) : template.replace(/\{(\w+)\}/g, (m, key) => vars[key] != null ? String(vars[key]) : m);
  const label = (ko) => (i18n && typeof i18n.t === "function") ? i18n.t(ko) : ko;
  return await confirmDialog(
    ask("'{name}'은(는) {size}로 큽니다. 여는 동안 화면이 한동안 멈출 수 있고, 메모리가 모자라면 열려 있는 다른 탭의 저장하지 않은 내용까지 사라질 수 있어요. 먼저 저장한 뒤 열기를 권합니다.",
      { name:file.name, size:openSizeText(size) }),
    label("그래도 열기"), label("열지 않기"));
}

/* ===== 파일 로딩 ===== */
async function handleFiles(files, options={}){
  const arr = [...files];
  const bulk = options.bulk || arr.length > 1;        // 여러 개·압축 내부 → 첫 항목만 자동 표시(나머지는 클릭 시 렌더)
  let firstDoc = null;                                 // 호출부가 연 문서를 바로 쓸 수 있게(정의 이동 등) 반환
  for (const file of arr){
    throwIfUiCancelled();
    const ext = fileExtOf(file.name);
    const opts = { ...options, bulk, size: file.size || 0, fsHandle: options.fsHandle || file.__fsHandle || null,
      fsDirHandle: options.fsDirHandle || file.__fsDirHandle || null,
      sqliteDiskPath: options.sqliteDiskPath || file.__sqliteDiskPath || null,
      nativeAbsolutePath: options.nativeAbsolutePath || file.__nativeAbsolutePath || null,
      workspacePath: options.transient ? null : (options.workspacePath || file.webkitRelativePath || (!options.parentId ? file.name : null)) };
    opts.textEncoding = await inspectTextFileEncoding(file, ext);
    opts.sourceKey = options.sourceKey || [options.parentId || "root", opts.workspacePath || options.relPath || file.name, file.size || 0, file.lastModified || 0].join("|");
    if (opts.fsHandle && !opts.fsHandle.__classdockNativeHandle && opts.workspacePath && typeof saveFsHandle === "function")
      saveFsHandle(opts.workspacePath, opts.fsHandle);
    // 최근 목록에는 "다시 열 수 있는" 것만 남긴다 — 핸들을 보관한 최상위 파일만이고,
    // 폴더·압축 안의 파일(parentId 있음)과 임시 문서는 각각 폴더 항목·대상 아님으로 처리한다.
    if (opts.fsHandle && !opts.fsHandle.__classdockNativeHandle && opts.workspacePath && !options.parentId && !options.transient && !options.isScratch
        && typeof MNRecent !== "undefined") MNRecent.rememberFile(file.name, opts.workspacePath);
    const duplicate = await workspaceFindOpenDocument(file, opts);
    if (duplicate){
      const attachment = workspaceAttachExistingDoc(duplicate, opts.parentId || null);
      if (!uiBatchDepth) setActiveDoc(duplicate.id);
      else if (!uiBatchActiveCandidate) uiBatchActiveCandidate = duplicate.id;
      toast(attachment === "moved"
        ? `이미 열려 있던 파일을 폴더 아래로 정리했습니다: ${file.name}`
        : attachment
          ? `이미 열려 있는 파일을 이 작업공간에도 공유했습니다: ${file.name}`
          : `이미 열린 파일입니다: ${file.name}`,
      attachment ? 2800 : 1800);
      if (!firstDoc) firstDoc = duplicate;
      continue;
    }
    // 이미 열린 파일을 가려낸 다음에 묻는다 — 다시 누른 것뿐인데 경고를 볼 이유가 없다.
    if (!await confirmLargeFileOpen(file, ext, opts)) continue;
    if (opts.archiveCtx && !opts.relPath) opts.relPath = file.name;   // 여러 파일 동시 업로드(평면)의 옆파일 경로
    try {
      let made = null;
      if (ext === "pdf") await loadPdf(await file.arrayBuffer(), file.name, opts);
      else if (ext === "mnote" && typeof loadMnote === "function") made = await loadMnote(file, opts);
      else if (ext === "map" && typeof loadMapDoc === "function") made = await loadMapDoc(file, opts);
      else if (ext === "timeline" && typeof loadTimelineDoc === "function") made = await loadTimelineDoc(file, opts);
      else if (ext === "dbconn" && typeof loadDbConnDoc === "function") made = await loadDbConnDoc(file, opts);
      else if (ext === "concept" && typeof loadConceptDoc === "function") made = await loadConceptDoc(file, opts);
      else if (ext === "study" && typeof loadStudyDoc === "function") made = await loadStudyDoc(file, opts);
      else if (ext === "msheet" && typeof loadMusicSheet === "function") made = await loadMusicSheet(file, opts);
      else if ((ext === "musicxml" || ext === "mxl") && typeof loadMusicXml === "function") made = await loadMusicXml(file, opts);
      else if (ext === "lesson" && typeof loadLesson === "function") made = await loadLesson(file, opts);
      else if (ext === "task" && typeof loadTask === "function") made = await loadTask(file, opts);
      else if (ext === "taskdone" && typeof loadTaskSubmission === "function") made = await loadTaskSubmission(file, opts);
      else if (ext === "examkey" && typeof loadExamMaster === "function") made = await loadExamMaster(file, opts);
      else if (ext === "exam" && typeof loadExamPaper === "function") made = await loadExamPaper(file, opts);
      else if (ext === "examdone" && typeof loadExamSubmission === "function") made = await loadExamSubmission(file, opts);
      else if (ext === "zip") await loadZip(file, opts);
      else if (ext === "tar") await loadTar(file, opts);
      else if (ext === "gz" || ext === "tgz") await loadGz(file, opts);   // .gz / .tgz / .tar.gz
      else if (ext === "pptx"){
        const pptxBytes = await readPptxBytes(file);
        if (appSettings.pptxExactByDefault === true){
          const pdfBuf = await tryConvertPptxToPdf(pptxBytes);  // 설정에서 고른 경우에만 자동 정확 변환
          // PPTX를 PDF로 변환해 보여 주는 경우에는 PDF 저장이 원본 PPTX를 덮어쓰면 안 된다.
          // 따라서 변환 미리보기에는 파일 핸들과 원본 저장 모드를 넘기지 않는다.
          if (pdfBuf) await loadPdf(pdfBuf, file.name.replace(/\.pptx$/i, ".pdf"), { ...opts, fsHandle:null, fsDirHandle:null, originalSaveMode:false });
          else made = await loadOffice(file, "pptx", { ...opts, pptxBytes, pptxConvertError: _lastPptxConvertError || "알 수 없는 변환 실패" });
        } else {
          // PowerPoint 설치 여부를 묻지 않고 곧바로 브라우저 미리보기로 연다. COM 실행 불량이나 첫 실행
          // 대화상자가 파일 열기 전체를 수십 초 막는 일을 피하고, 정확 변환은 문서 배너의 버튼에 맡긴다.
          made = await loadOffice(file, "pptx", { ...opts, pptxBytes, pptxQuickPreview:true });
        }
      }
      else if (SQLITE_EXTS.includes(ext)) made = await loadSqlite(file, opts);
      else if (BINARY_ASSET_EXTS.has(ext)) made = await loadBinaryAsset(file, opts);
      else if (ext === "ipynb"){
        if (typeof notebookModeEnabled === "function" && notebookModeEnabled()){
          // [실험·Phase1] 셀 노트북 뷰(읽기전용 미리보기). 콘솔에서 mnNotebookMode(false) 로 끄면 기존 변환(.py) 뷰.
          try {
            const __nbModel = ipynbToModel(await file.text());
            made = makeDoc("office", file.name, opts);
            made.notebook = true; made.notebookModel = __nbModel;
            made.render = async () => { made.el.innerHTML = ""; made.el.scrollTop = 0; renderNotebookView(__nbModel, made.el, made); };
            // 사이드바 내용 검색 결과 클릭 → 일치한 셀로 이동(렌더 뒤에 불린다)
            made.contentSearchFocus = (query) => nbFocusSearchMatch(made, query);
            refreshChrome();
            activateIfIdle(made, opts);
          } catch(e){ toast((e && e.message) || "노트북을 열지 못했어요.", 4000); made = await loadText(file, opts); }
        } else {
        // 주피터 노트북 → 파이썬 소스로 변환한 뒤 Python 실습 뷰어로 연다
        let pySrc = null;
        try { pySrc = ipynbToPython(await file.text(), file.name); }
        catch(e){ toast((e && e.message) || "노트북을 변환하지 못했어요.", 4000); made = await loadText(file, opts); }
        if (pySrc != null){
          const pyName = file.name.replace(/\.ipynb$/i, "") + ".py";
          const pyFile = new File([pySrc], pyName, { type: "text/plain" });
          // 변환된 노트북은 .py 로 다룬다 — 경로 표시·저장 대상도 .py 로 맞춘다.
          // (폴더 새로고침의 탭 복원도 같은 ipynb 분기를 거치므로 .py 경로끼리 일관되게 매칭된다)
          const pyOpts = { ...opts, textEncoding: null };
          if (pyOpts.workspacePath) pyOpts.workspacePath = pyOpts.workspacePath.replace(/\.ipynb$/i, ".py");
          if (pyOpts.relPath) pyOpts.relPath = pyOpts.relPath.replace(/\.ipynb$/i, ".py");
          // 원본 .ipynb 파일 핸들은 물려받지 않는다(저장 때 노트북을 파이썬으로 덮어쓰지 않도록).
          // 대신 같은 폴더 핸들을 들고 있다가, 저장할 때 그 폴더에 X.py 를 새로 만든다(원본 .ipynb 는 그대로 보존).
          pyOpts.fsHandle = null;
          pyOpts.fsDirHandle = file.__fsDirHandle || null;
          made = await loadOffice(pyFile, "py", pyOpts);
          if (made) made.notebook = true;   // 셀 단위 실행(에러가 나도 다음 셀 계속)을 위해 표시
        }
        }
      }
      else if (ext === "doc"){
        // 이름만 .doc 인 파일이 흔하다(실은 docx, 또는 그냥 텍스트). 앞부분을 보고 알맞은 통로로 보낸다.
        const head = new Uint8Array(await file.slice(0, 8).arrayBuffer());
        const kind = docLegacyKindOf(head);
        if (kind === "text") made = await loadText(file, opts);        // 텍스트 뷰(편집·저장 가능)
        else made = await loadOffice(file, kind === "docx" ? "docx" : "doc", opts);
      }
      else if (["docx","xlsx","xls","csv","hwp","hwpx","md","markdown","mdx","txt","html","htm","xhtml"].includes(ext) || (ext in CODE_EXTS)) made = await loadOffice(file, ext, opts);
      else if (IMG_EXTS.includes(ext)) made = await loadImage(file, opts);
      else if (VIDEO_EXTS.includes(ext) || AUDIO_EXTS.includes(ext)) made = await loadVideo(file, opts);
      // 자막은 텍스트 뷰로 명시 배정 — UTF-16 저장 SMI가 loadText 바이너리 판별에 오판되지 않게
      else if (SUBTITLE_EXTS.includes(ext)) made = await loadOffice(file, "txt", opts);
      else made = await loadText(file, opts);          // 알 수 없는 확장자 → 텍스트면 열고 아니면 안내
      if (made && !firstDoc) firstDoc = made;
      const opened = opts.sourceKey ? docsBySourceKey.get(opts.sourceKey) : null;
      if (opened){
        // 폴더 새로고침의 변경 판별(경로+크기+수정시각)용 — 원본 파일 수정시각을 문서에 새겨 둔다.
        opened.__srcMtime = file.lastModified || 0;
        // 디스크에 없는 편집본을 자동 복원 스냅샷으로 되살린 문서 — 폴더 동기화가 덮어쓰지 않게 표시한다.
        if (file && file.__workspaceRecovery) opened.workspaceRecovery = true;
        // 닫은 탭 복원용: 최상위 실제 파일로 연 문서엔 원본 File과 열기 옵션을 보관해 둔다(아카이브 내부·임시 문서 제외).
        if (!opts.parentId && !opts.archiveCtx && !options.transient && file instanceof File){
          opened.__reopen = { file, name: opened.name,
            options: { workspacePath: opts.workspacePath, fsHandle: opts.fsHandle, textEncoding: opts.textEncoding,
              sqliteDiskPath: opts.sqliteDiskPath, originalSaveMode: opts.originalSaveMode } };
        }
      }
    } catch (e){ if (e && e.message === "operation-cancelled") throw e; console.error(e); }
  }
  return firstDoc;
}

// 닫은 탭 복원 스택(최근 닫은 파일 12개). 일괄/내부 닫기는 제외하고 사용자가 직접 닫은 파일만 쌓인다.
let closedDocStack = [];
async function reopenClosedDoc(){
  const entry = closedDocStack.pop();
  if (!entry || !entry.file){ toast("다시 열 닫은 파일이 없어요.", 1800); return; }
  try { await handleFiles([entry.file], entry.options || {}); }
  catch(e){ if (!(e && e.message === "operation-cancelled")) toast("파일을 다시 열지 못했어요.", 3000); }
}

/* ===== PPTX → PDF 변환 (exe 백엔드 + 설치된 PowerPoint) =====
   pptxjs 는 도형/그룹 좌표를 못 맞춰 원본과 크게 달라진다. exe 로 실행 중이면 launcher 의
   /convert-pptx 엔드포인트(PowerPoint COM)로 정확히 PDF 변환해 PDF 뷰어로 띄운다(서명도 가능).
   - file:// (브라우저 단독 offline HTML)이거나 PowerPoint 미설치면 null → 기존 pptxjs 폴백. */
let _pptxBackend = null;   // null=미확인, true/false=캐시
let _lastPptxConvertError = "";
async function readPptxBytes(file){
  const bytes = new Uint8Array(await file.arrayBuffer());
  if (!looksEncryptedOffice(bytes)) return bytes;
  const dec = await promptAndDecrypt(bytes, "pptx");
  if (!dec) throw new Error("cancelled");
  return dec;
}
async function pptxBackendAvailable(){
  if (location.protocol !== "http:" && location.protocol !== "https:") return false;   // file:// → 백엔드 없음
  if (_pptxBackend !== null) return _pptxBackend;
  try {
    const res = await fetch("/can-convert", { method: "GET" });
    _pptxBackend = res.ok && (await res.text()).trim() === "yes";
  } catch(e){ _pptxBackend = false; }
  return _pptxBackend;
}
async function tryConvertPptxToPdf(pptxBytes, options={}){
  _lastPptxConvertError = "";
  // 사용자가 '다시 시도'를 눌렀다면 이전 실패 캐시를 버리고 설치 상태부터 새로 확인한다.
  if (options.forceBackendCheck === true) _pptxBackend = null;
  if (!(await pptxBackendAvailable())) {
    const isHttp = location.protocol === "http:" || location.protocol === "https:";
    const msg = isHttp ? "PowerPoint 변환 백엔드를 사용할 수 없음" : "ClassDock.exe에서만 정확 변환 가능";
    _lastPptxConvertError = msg;
    toast(options.inline === true
      ? (isHttp ? "PowerPoint 변환 백엔드를 사용할 수 없어 현재 간이 미리보기를 유지합니다." : "정확 변환은 ClassDock.exe에서 사용할 수 있습니다. 현재 간이 미리보기를 유지합니다.")
      : (isHttp ? "PowerPoint 변환 백엔드를 사용할 수 없어 간이 미리보기로 열어요." : "PPTX 도형을 정확히 보려면 ClassDock.exe로 열어주세요."), 4000);
    return null;
  }
  const ctrl = new AbortController();
  const requestedTimeout = Number(options.timeoutMs) || 180000;
  const timeoutMs = Math.max(1000, Math.min(requestedTimeout, 180000));
  const inline = options.inline === true;
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    if (!inline) showLoading("PowerPoint으로 변환 중… (대형 파일은 잠시 걸려요)");
    const buf = normalizeArrayBuffer(pptxBytes);
    const res = await fetch("/convert-pptx", { method: "POST", headers: { "Content-Type": "application/octet-stream" }, body: buf, signal: ctrl.signal });
    if (!res.ok) {
      const msg = await res.text().catch(() => "");
      console.warn("pptx pdf conversion failed:", res.status, msg);
      _lastPptxConvertError = msg || ("HTTP " + res.status);
      if (res.status === 501 || /no-powerpoint|0x80080005|server execution failed|서버 실행이 실패/i.test(msg)) _pptxBackend = false;
      toast("PowerPoint 변환에 실패해 간이 미리보기를 유지합니다.", 3500);
      return null;                                         // 501(PowerPoint 없음)/500 등 → 폴백
    }
    if (((res.headers.get("Content-Type") || "").toLowerCase()).indexOf("application/pdf") < 0) {
      _lastPptxConvertError = "PowerPoint 변환 결과가 PDF가 아님";
      toast("PowerPoint 변환 결과가 올바르지 않아 간이 미리보기로 열어요.", 3500);
      return null;
    }
    const pdf = await res.arrayBuffer();
    if (pdf && pdf.byteLength > 100) {
      toast("PowerPoint 정확 변환(PDF)으로 열었어요.", 2500);
      return pdf;
    }
    _lastPptxConvertError = "빈 PDF 결과";
    return null;
  } catch(e){
    console.warn("pptx pdf conversion skipped:", e);
    const timedOut = !!(e && e.name === "AbortError");
    _lastPptxConvertError = timedOut
      ? "PowerPoint가 " + Math.round(timeoutMs / 1000) + "초 동안 응답하지 않음"
      : (e && e.message ? e.message : String(e || "unknown"));
    if (timedOut) {
      // 같은 세션에서 다시 눌렀을 때 또 기다리지 않는다. 앱을 다시 열면 설치 여부를 새로 확인한다.
      _pptxBackend = false;
      toast("PowerPoint가 응답하지 않아 간이 미리보기를 유지합니다.", 3500);
    } else toast("PowerPoint 변환을 사용할 수 없어 간이 미리보기를 유지합니다.", 3500);
    return null;
  }
  finally { clearTimeout(timer); if (!inline) hideLoading(); }
}

// 간이 PPTX 위의 "정확한 PDF로 열기" 버튼이 쓰는 명시적 변환 통로.
// 원본 PPTX 탭은 남겨 슬라이드 찾아 바꾸기를 계속 쓸 수 있게 하고, 변환 결과는 별도 PDF 탭으로 연다.
async function openPptxExactPreview(file, pptxBytes, options={}){
  const baseKey = options.sourceKey || ["pptx", file && file.name || "presentation", file && file.size || 0, file && file.lastModified || 0].join("|");
  const exactSourceKey = baseKey + "|exact-pdf";
  const existing = typeof docsBySourceKey !== "undefined" ? docsBySourceKey.get(exactSourceKey) : null;
  if (existing){ setActiveDoc(existing.id); return existing; }

  // 버튼 작업은 문서 화면을 막지 않고, PowerPoint 시작 불량이면 15초 안에 미리보기로 돌아온다.
  const pdfBuf = await tryConvertPptxToPdf(pptxBytes, { timeoutMs:15000, inline:true, forceBackendCheck:true });
  if (!pdfBuf) return null;   // 실패해도 현재 간이 미리보기는 그대로 유지한다.
  const pdfName = (file && file.name || "presentation.pptx").replace(/\.pptx$/i, ".pdf");
  return loadPdf(pdfBuf, pdfName, {
    ...options, sourceKey:exactSourceKey, workspacePath:null, transient:true, bulk:false,
    fsHandle:null, fsDirHandle:null, originalSaveMode:false
  });
}

/* ===== 압축(zip) 풀어서 내부 파일을 각각 열기 (zip.js — 무암호 + AES 암호 지원) ===== */
async function loadZip(file, options={}){
  if (typeof MNLazy !== "undefined") await MNLazy.tryNeed("zip");   // 압축 라이브러리는 첫 사용 때 로드
  if (typeof zip === "undefined"){ toast("압축 라이브러리를 불러오지 못했습니다."); return; }
  zip.configure({ useWebWorkers: false });                 // file:// 에서도 동작하도록 워커 미사용
  showLoading("압축 여는 중…");

  // 1) 엔트리 훑어서 — 열 수 있는 게 있는지 + 암호가 걸렸는지 파악
  let openable = 0, unsupported = 0, encrypted = false;
  const archivePaths = [];
  try {
    const r = new zip.ZipReader(new zip.BlobReader(file));
    for (const e of await r.getEntries()){
      if (e.directory) continue;
      const path = safeArchivePath(e.filename);
      if (!path) continue;
      if (path.indexOf("__MACOSX/") === 0) continue;        // 맥 메타데이터
      const base = path.split("/").pop();                   // 경로 제거 → 파일명만
      if (base && base !== ".DS_Store") archivePaths.push(path);
      if (!base || (base.charAt(0) === "." && !isEnvFile(base))) continue;   // 숨김(.DS_Store 등) — .env 계열은 예외
      const ext = fileExtOf(base);
      if (!ZIP_OPENABLE.includes(ext) && !isUnknownTextArchiveCandidate(ext, e.uncompressedSize)){ unsupported++; continue; }
      openable++;
      if (e.encrypted) encrypted = true;
    }
    await r.close();
  } catch(e){
    console.error(e); hideLoading();
    toast("압축을 열지 못했습니다. 올바른 zip 파일인지 확인해 주세요.", 3500);
    return;
  }
  if (!openable){
    hideLoading();
    toast(unsupported ? window.tf("압축 안에 열 수 있는 형식이 없어요. · {n}개 형식 미지원", { n: unsupported }) : "압축이 비어 있어요.", 3500);
    return;
  }

  // 2) 암호가 걸렸으면 암호 확정 (오피스 암호와 동일하게 최대 5회 재시도)
  let password = null;
  if (encrypted){
    hideLoading();
    for (let attempt = 0; ; attempt++){
      const pw = await askPassword(attempt === 0
        ? "암호로 보호된 압축입니다. 암호를 입력하세요."
        : "암호가 올바르지 않습니다. 다시 입력해 주세요.");
      if (pw === null) return;                              // 취소
      showLoading("암호 확인 중…");
      const ok = await zipPasswordOk(file, pw);
      hideLoading();
      if (ok){ password = pw; break; }
      if (attempt >= 4){ toast("암호를 확인하지 못했어요.", 3000); return; }
    }
  }

  const zipGroup = makeGroup("zip", file.name, options.parentId || null);
  zipGroup.zipLimits = true;
  zipGroup.workspacePaths = [options.workspacePath || file.name];
  // 같은 압축에서 나온 .py 실행 시 옆 파일(import·데이터)을 함께 쓰도록, 실행할 때 이 압축을 통째로 다시 푼다.
  const archiveCtx = {
    name: file.name,
    paths: archivePaths,
    extract: (keep) => extractZipAll(file, password, keep)
  };
  const zipFolders = new Map();
  function zipParentFor(path){
    const parts = String(path || "").split("/").filter(Boolean);
    parts.pop();
    let parentId = zipGroup.nodeId, key = "";
    for (const part of parts){
      key = key ? key + "/" + part : part;
      if (!zipFolders.has(key)){
        zipFolders.set(key, makeGroup("folder", part, parentId).nodeId);
      }
      parentId = zipFolders.get(key);
    }
    return parentId;
  }

  // 3) 실제 추출 → 하나씩 열어 메모리 피크를 낮춤
  showLoading("압축 푸는 중…");
  let opened = 0, failed = 0, oversized = 0, r = null, extractedBytes = 0;
  try {
    r = new zip.ZipReader(new zip.BlobReader(file), password ? { password } : undefined);
    const entries = await r.getEntries();
    for (const e of entries){
      if (e.directory) continue;
      const path = e.filename || "";
      if (path.indexOf("__MACOSX/") === 0) continue;
      const base = path.split("/").pop();
      if (!base || (base.charAt(0) === "." && !isEnvFile(base))) continue;
      const ext = fileExtOf(base);
      const entrySize = Number(e.uncompressedSize) || 0;
      const knownOpenable = ZIP_OPENABLE.includes(ext);
      if (!knownOpenable && !isUnknownTextArchiveCandidate(ext, entrySize)) continue;
      if (entrySize > ZIP_ENTRY_CAP || extractedBytes + entrySize > ZIP_EXTRACT_CAP){ oversized++; continue; }
      try {
        updateLoading(`압축 푸는 중… (${opened + failed + 1}/${openable})`);
        const m = ZIP_MIME[ext];
        const innerFile = new File([await e.getData(new zip.BlobWriter())], base, m ? { type: m } : undefined);
        extractedBytes += innerFile.size || entrySize;
        if (!knownOpenable && !(await isLikelyTextFile(innerFile))){ unsupported++; continue; }
        const parentId = zipParentFor(path);
        hideLoading();
        await handleFiles([innerFile], { parentId, bulk: true, relPath: path, archiveCtx,
          restoreFromWorkspace: !!options.restoreFromWorkspace });
        opened++;
        await yieldToBrowser();
        showLoading(`압축 푸는 중… (${opened}/${openable})`);
      } catch(err){
        console.error(err);
        failed++;
      }                               // 개별 추출 실패
    }
  } catch(e){ console.error(e); }
  finally {
    try { if (r) await r.close(); } catch(e){ console.warn(e); }
  }
  hideLoading();

  if (!opened){ await closeGroup(zipGroup.nodeId); toast("압축을 풀지 못했어요.", 3000); return; }
  // 폴더와 같은 규칙 — 압축을 열었다고 안의 첫 파일까지 띄우지는 않는다(자동 복원은 예외).
  if (!options.restoreFromWorkspace && !autoOpenFirstFileEnabled()) suppressUiBatchAutoOpen(zipGroup.nodeId);
  const summary = formatZipOpenSummary({ opened, unsupported, oversized, failed });
  toast(summary + " · ZIP은 읽기 중심이며 원본 새로고침·덮어쓰기는 지원하지 않아요. (ⓘ)", 6000);
}

/* ===== tar / gzip (.tar / .gz / .tar.gz / .tgz) ===== */
// gzip 해제: 브라우저 내장 DecompressionStream 사용(외부 라이브러리·네트워크 불필요).
async function gunzipBytes(bytes){
  if (typeof DecompressionStream === "undefined")
    throw new Error("이 브라우저는 gzip 해제를 지원하지 않습니다.");
  const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

// tar 파서: 512바이트 헤더+데이터 블록을 훑어 일반 파일만 추출({name, data}[]).
function parseTar(buf){
  const td = new TextDecoder();
  const str = (s, len) => td.decode(buf.subarray(s, s + len)).replace(/\0[\s\S]*$/, "").trim();
  const octal = (s, len) => {
    const text = td.decode(buf.subarray(s, s + len)).replace(/^[\0 ]+|[\0 ]+$/g, "");
    // parseInt만 쓰면 음수와 "123x" 같은 부분 숫자도 통과한다.
    if (!/^[0-7]*$/.test(text)) throw new Error("손상된 TAR: 올바르지 않은 숫자 필드");
    return text ? parseInt(text, 8) : 0;
  };
  const fileSize = (s) => {
    let size = 0;
    if (buf[s] === 0x80){
      // GNU의 양수 base-256 크기. 음수와 안전 정수 범위 밖의 값은 허용하지 않는다.
      for (let i = 1; i < 12; i++){
        size = size * 256 + buf[s + i];
        if (!Number.isSafeInteger(size)) throw new Error("손상된 TAR: 파일 크기 범위 초과");
      }
    } else {
      size = octal(s, 12);
    }
    return size;
  };
  const files = []; let off = 0, longName = null;
  while (off < buf.length){
    if (buf.length - off < 512) throw new Error("손상된 TAR: 잘린 헤더");
    let empty = true, unsignedSum = 0, signedSum = 0;
    for (let i = 0; i < 512; i++){
      const raw = buf[off + i];
      if (raw !== 0) empty = false;
      // 체크섬 필드 자체는 공백으로 계산한다. 구형 signed 체크섬도 호환한다.
      const byte = i >= 148 && i < 156 ? 32 : raw;
      unsignedSum += byte;
      signedSum += byte < 128 ? byte : byte - 256;
    }
    if (empty) break;                                   // 끝(빈 블록)
    const checksum = octal(off + 148, 8);
    if (checksum !== unsignedSum && checksum !== signedSum)
      throw new Error("손상된 TAR: 헤더 체크섬 불일치");

    let name = str(off, 100);
    const size = fileSize(off + 124);
    const type = String.fromCharCode(buf[off + 156] || 0);
    const prefix = str(off + 345, 155);                 // ustar 긴 경로
    if (prefix) name = prefix + "/" + name;
    const dataStart = off + 512;
    const paddedSize = Math.ceil(size / 512) * 512;
    // subarray는 범위 초과를 조용히 잘라 내므로 본문·패딩을 먼저 검증한다.
    // 생략할 메타 항목도 같은 검증을 거쳐야 다음 헤더 위치를 신뢰할 수 있다.
    if (size > buf.length - dataStart || paddedSize > buf.length - dataStart)
      throw new Error("손상된 TAR: 잘린 파일 데이터 또는 패딩");
    const data = buf.subarray(dataStart, dataStart + size);
    off = dataStart + paddedSize;
    if (type === "L"){ longName = td.decode(data).replace(/\0[\s\S]*$/, ""); continue; }  // GNU 긴 이름
    if (longName){ name = longName; longName = null; }
    if (type === "0" || type === "\0" || type === "" || type === "7") files.push({ name, data });
    // 디렉토리(5)·심볼릭(1,2)·메타(x,g)는 건너뜀
  }
  return files;
}

// tar 바이트를 그룹으로 펼쳐 내부 파일을 각각 연다(zip 과 동일한 폴더 트리 구성).
async function extractTar(tarBytes, name, options = {}){
  const entries = parseTar(tarBytes).filter(en => {
    const base = (en.name.split("/").pop() || "");
    if (!base || (base.charAt(0) === "." && !isEnvFile(base)) || en.name.indexOf("PaxHeader") >= 0) return false;
    return ZIP_OPENABLE.includes(fileExtOf(base)) || isLikelyTextBytes(en.data);
  });
  if (!entries.length){ toast("압축 안에 열 수 있는 형식이 없어요.", 3000); return; }
  const group = makeGroup("zip", name, options.parentId || null);
  group.workspacePaths = [options.workspacePath || name];
  // 같은 tar 에서 나온 .py 실행 시 옆 파일을 함께 쓰도록, 실행할 때 tar 를 통째로 다시 푼다.
  const archiveCtx = { name, extract: () => tarTreeAll(tarBytes) };
  const folders = new Map();
  const parentFor = (path) => {
    const parts = String(path || "").split("/").filter(Boolean); parts.pop();
    let parentId = group.nodeId, key = "";
    for (const part of parts){
      key = key ? key + "/" + part : part;
      if (!folders.has(key)) folders.set(key, makeGroup("folder", part, parentId).nodeId);
      parentId = folders.get(key);
    }
    return parentId;
  };
  let opened = 0;
  for (const en of entries){
    const base = en.name.split("/").pop();
    const m = ZIP_MIME[(base.split(".").pop() || "").toLowerCase()];
    const innerFile = new File([en.data], base, m ? { type: m } : undefined);
    await handleFiles([innerFile], { parentId: parentFor(en.name), bulk: true, relPath: en.name, archiveCtx,
      restoreFromWorkspace: !!options.restoreFromWorkspace });
    opened++;
    await yieldToBrowser();
  }
  if (!opened){ await closeGroup(group.nodeId); toast("압축을 풀지 못했어요.", 3000); }
  else {
    if (!options.restoreFromWorkspace && !autoOpenFirstFileEnabled()) suppressUiBatchAutoOpen(group.nodeId);
    toast(window.tf("{n}개 열기", { n: opened }), 3000);
  }
}

async function loadTar(file, options = {}){
  showLoading("압축 푸는 중…");
  try {
    await extractTar(new Uint8Array(await file.arrayBuffer()), file.name, options);
  } catch(e){ console.error(e); toast("tar 파일을 열지 못했습니다.", 3500); }
  finally { hideLoading(); }
}

async function loadGz(file, options = {}){
  showLoading("압축 푸는 중…");
  try {
    const out = await gunzipBytes(new Uint8Array(await file.arrayBuffer()));
    const lower = file.name.toLowerCase();
    if (lower.endsWith(".tgz") || lower.endsWith(".tar.gz")){
      await extractTar(out, file.name.replace(/\.(tgz|tar\.gz)$/i, ".tar"), options);
    } else {
      // 단일 파일 gzip → 확장자(.gz)만 떼고 그대로 처리
      const innerName = file.name.replace(/\.gz$/i, "") || "decompressed";
      hideLoading();
      await handleFiles([new File([out], innerName)], options);
      // tar 계열뿐 아니라 단일 파일 gzip도 설정의 "압축" 규칙을 똑같이 따른다.
      if (!options.restoreFromWorkspace && !autoOpenFirstFileEnabled()) suppressUiBatchAutoOpen(null);
    }
  } catch(e){ console.error(e); toast("gzip 압축을 풀지 못했습니다. (지원: gzip · tar.gz)", 3500); }
  finally { hideLoading(); }
}

/* 암호 검증: 첫 암호화 엔트리를 주어진 암호로 풀어본다(성공=true) */
async function zipPasswordOk(file, pw){
  try {
    const r = new zip.ZipReader(new zip.BlobReader(file), { password: pw });
    const te = (await r.getEntries()).find(e => e.encrypted && !e.directory);
    if (te) await te.getData(new zip.BlobWriter());         // 암호가 틀리면 여기서 throw
    await r.close();
    return true;
  } catch(e){ return false; }
}

async function openFilesWithHandles(options={}){
  if (typeof window === "undefined" || typeof window.showOpenFilePicker !== "function") return false;
  let handles;
  try {
    handles = await window.showOpenFilePicker({ multiple: true });
  } catch(e){
    if (e && e.name !== "AbortError") console.warn(e);
    return !!(e && e.name === "AbortError");
  }
  const files = [];
  for (const handle of handles || []){
    try { files.push(withFileHandle(await handle.getFile(), handle)); }
    catch(e){ console.warn(e); }
  }
  if (files.length) queueFiles(files, options);
  return true;
}
function pickFilesOrInput(input, options={}){
  openFilesWithHandles(options).then(handled => {
    if (!handled && input) input.click();
  });
}

function queueFiles(files, options={}){
  const batch = [...files];
  if (!batch.length) return fileQueue;
  let opts = options;
  // 여러 파일을 한 번에 올리면 그 묶음을 같은 작업폴더의 옆 파일로 묶는다(.py 실행 시 import/파일읽기 지원).
  if (!options.archiveCtx){
    const loose = batch.filter(f => !["zip","tar","gz","tgz"].includes((f.name.split(".").pop() || "").toLowerCase()));
    if (loose.length > 1){
      const ctx = makeFileSiblingCtx(loose.map(f => ({ file: f, relPath: f.name })), "여러 파일");
      opts = { ...options, archiveCtx: ctx };
    }
  }
  fileQueue = fileQueue
    .then(() => runUiBatch(async () => {
      // 자동 복원 저장은 파일을 먼저 연 뒤에 한다 — 저장 준비(파일 복사) 시간을 기다리지 않고 바로 화면에 뜨게.
      const replaceWorkspace = docs.length === 0;
      await handleFiles(batch, opts);
      await rememberWorkspace(batch, replaceWorkspace, { silent: true });
    }))
    .catch((e) => { if (e && e.message === "operation-cancelled") toast("파일 열기를 취소했어요."); else console.error(e); });
  return fileQueue;
}

/* 폴더 열기(webkitdirectory / File System Access API)
   - 지원 브라우저는 디렉터리 핸들을 보관해 이후 폴더 새로고침을 한 번에 처리한다.
   - 미지원 환경은 기존 folder input으로 폴더를 다시 선택하는 방식으로 폴백한다. */
let pendingFolderRefreshId = null;

function setFileRelativePath(file, path){
  try { Object.defineProperty(file, "webkitRelativePath", { value: path, configurable: true }); } catch(e){}
  return file;
}
// 폴더 핸들을 IndexedDB 에 보관(구조화 복제 가능) → 자동 복원으로 되살아난 폴더도 '새로고침' 버튼이
// 권한 확인 1클릭만으로 디스크에서 다시 읽는다. (이미지 대량 폴더의 자동 복원 제외와 짝을 이룸)
const FOLDER_HANDLE_KEY_PREFIX = "__folder-handle__/";
async function loadRememberedFolderHandle(rootName){
  if (typeof loadFsHandle !== "function") return null;
  try {
    const handle = await loadFsHandle(FOLDER_HANDLE_KEY_PREFIX + rootName);
    return handle && handle.kind === "directory" ? handle : null;
  } catch(_){ return null; }
}
function rememberFolderHandle(rootGroup, rootName){
  if (typeof saveFsHandle !== "function" || typeof loadFsHandle !== "function") return;
  if (rootGroup.folderHandle){
    if (!rootGroup.folderHandle.__classdockNativeHandle)
      saveFsHandle(FOLDER_HANDLE_KEY_PREFIX + rootName, rootGroup.folderHandle);
    return;
  }
  if (!rootGroup.originalSaveMode) return;
  // EXE에서는 서버가 기억한 실제 Windows 경로만 복원한다. 브라우저 핸들을 다시 붙이면
  // 앱 재실행 뒤 첫 저장 때 Chromium의 "이 사이트에서 파일을 편집" 권한창이 다시 뜬다.
  (async () => {
    const native = typeof nativeSourceSupported === "function" && await nativeSourceSupported();
    if (native){
      const handle = typeof restoreNativeSourceFolder === "function" ? await restoreNativeSourceFolder(rootName) : null;
      if (handle && !rootGroup.folderHandle){
        rootGroup.folderHandle = handle;
        rootGroup.nativeRootPath = handle.nativePath || "";
      }
      return;
    }
    const handle = await loadRememberedFolderHandle(rootName);
    if (handle && !rootGroup.folderHandle) rootGroup.folderHandle = handle;
  })().catch(() => {});
}
function folderScanLoadingText(progress){
  const row = progress || {};
  const files = Math.max(0, Number(row.files) || 0).toLocaleString();
  const folders = Math.max(0, Number(row.folders) || 0).toLocaleString();
  if (row.phase === "reading"){
    const processed = Math.max(0, Number(row.processed) || 0).toLocaleString();
    const total = Math.max(0, Number(row.total) || 0).toLocaleString();
    return `폴더 파일 불러오는 중… (${processed}/${total})`;
  }
  return `폴더 파일 확인 중… (파일 ${files}개 · 폴더 ${folders}개)`;
}
// 이미 열려 있는 문서의 원본 스냅샷을 경로별로 모은다. 디렉터리 목록이 알려준 크기·수정시각이
// 그대로면 디스크(EXE에서는 로컬 HTTP)를 다시 읽지 않고 이 File 을 재사용한다.
function folderSnapshotReuseMap(rootId){
  const map = new Map();
  if (typeof navBranchIds !== "function" || typeof docs === "undefined") return map;
  const branchIds = navBranchIds(rootId);
  for (const doc of docs){
    if (!branchIds.has(doc.nodeId)) continue;
    const file = doc.sourceFile;
    if (!file || typeof file.arrayBuffer !== "function") continue;
    const key = normalizedRunPath(doc.workspacePath || doc.relPath || doc.name);
    if (key && !map.has(key)) map.set(key, file);
  }
  return map;
}
// 목록이 준 메타(size·lastModified)가 기존 스냅샷과 완전히 같을 때만 재사용한다.
// 메타를 주지 않는 브라우저 File System Access 핸들에서는 항상 null → 기존처럼 getFile() 한다.
function reusableSnapshotFile(reuse, relPath, meta){
  if (!reuse || !reuse.size || !meta) return null;
  const cached = reuse.get(normalizedRunPath(relPath));
  if (!cached || typeof cached.arrayBuffer !== "function") return null;
  const size = Number(meta.size), mtime = Number(meta.lastModified);
  if (!isFinite(size) || !isFinite(mtime) || !mtime) return null;
  if ((Number(cached.size) || 0) !== size) return null;
  if ((Number(cached.lastModified) || 0) !== mtime) return null;
  return cached;
}
function folderSyncUnreadablePath(value, skippedFiles, skippedDirs){
  const key = normalizedRunPath(value);
  return skippedFiles.has(key) || skippedDirs.some(dir => key === dir || key.startsWith(dir + "/"));
}
function retainedWorkspacePathsForSkipped(oldPaths, skippedFiles, skippedDirs){
  return [...new Set((oldPaths || []).map(normalizedRunPath).filter(path =>
    path && folderSyncUnreadablePath(path, skippedFiles, skippedDirs)))];
}
// 읽지 못한 항목을 사용자에게 한 번에 알린다(파일 하나 때문에 동기화 전체가 실패하지 않으므로,
// 무엇이 빠졌는지는 반드시 알려야 한다).
function reportSkippedFolderEntries(skipped){
  const list = [...(skipped || [])];
  if (!list.length) return;
  const names = list.slice(0, 3).map(item => (item.path || "").split("/").pop() || item.path).join(", ");
  const more = list.length > 3 ? " 외 " + (list.length - 3) + "개" : "";
  toast(names + more + "을(를) 읽지 못해 건너뛰었어요. 다른 프로그램이 사용 중일 수 있어요.", 5200);
}
async function collectDirectoryHandleFiles(handle, options={}){
  if (!handle || handle.kind !== "directory") return { files: [], folderPaths: [], skipped: [] };
  const rootName = handle.name || "폴더";
  const found = [];                          // { entry, dir, parts } — 경로 순회로 먼저 모은다
  const folderPaths = [rootName];
  const reuse = options.reuseFiles instanceof Map ? options.reuseFiles : null;
  const skipped = [];                        // { path, kind } — 잠김·삭제 등으로 읽지 못한 항목
  const onProgress = typeof options.onProgress === "function" ? options.onProgress : null;
  let lastProgressAt = 0, lastProgressCount = -1;
  const report = (phase, processed=0, total=0, force=false) => {
    if (!onProgress) return;
    const count = phase === "reading" ? processed : found.length + folderPaths.length;
    const now = typeof performance !== "undefined" ? performance.now() : Date.now();
    if (!force && count - lastProgressCount < 25 && now - lastProgressAt < 120) return;
    lastProgressCount = count;
    lastProgressAt = now;
    onProgress({ phase, files:found.length, folders:folderPaths.length, processed, total });
  };
  report("scanning", 0, 0, true);
  const walk = async (dir, parts) => {
    // 목록 읽기와 하위 순회를 분리한다 — 깊은 곳의 실패가 이 폴더의 남은 형제까지 삼키지 않게.
    const children = [];
    try {
      for await (const entry of dir.values()){
        throwIfUiCancelled();
        if (!entry || !entry.name) continue;
        children.push(entry);
      }
    } catch(error){
      if (error && error.message === "operation-cancelled") throw error;
      // 순회 도중 사라지거나 접근이 막힌 폴더가 있어도 나머지 트리는 그대로 동기화한다.
      const path = [rootName].concat(parts).join("/");
      console.warn("폴더 목록을 읽지 못해 건너뜁니다:", path, error);
      skipped.push({ path, kind:"directory" });
      return;
    }
    for (const entry of children){
      if (entry.kind === "directory"){
        if (entry.name.charAt(0) === ".") continue;
        const nextParts = parts.concat(entry.name);
        folderPaths.push([rootName].concat(nextParts).join("/"));
        report("scanning");
        await walk(entry, nextParts);
      } else if (entry.kind === "file"){
        found.push({ entry, dir, parts });
        report("scanning");
      }
    }
  };
  await walk(handle, []);
  report("scanning", 0, 0, true);
  // getFile() 은 항목마다 브라우저 왕복이 있어 수천 개 폴더에서 순차 실행이 느리다 → 순서 보존 제한 병렬.
  let loaded = 0;
  report("reading", loaded, found.length, true);
  const collected = await mapWithConcurrency(found, 8, async (item) => {
    throwIfUiCancelled();
    const relPath = [rootName].concat(item.parts, item.entry.name).join("/");
    const done = () => report("reading", ++loaded, found.length, loaded === found.length);
    let file = reusableSnapshotFile(reuse, relPath, item.entry.meta);
    if (!file){
      try { file = await item.entry.getFile(); }
      catch(error){
        if (error && error.message === "operation-cancelled") throw error;
        // 파일 하나가 잠겨 있거나(실행 중인 파이썬의 로그 등) 스캔 뒤에 사라져도
        // 폴더 동기화 전체를 버리지 않는다 — 그 항목만 빼고 나머지를 반영한다.
        console.warn("파일을 읽지 못해 건너뜁니다:", relPath, error);
        skipped.push({ path: relPath, kind:"file" });
        done();
        return null;
      }
    }
    file = withDirHandle(withFileHandle(file, item.entry), item.dir);
    setFileRelativePath(file, relPath);
    done();
    return file;
  });
  return { files: collected.filter(Boolean), folderPaths, skipped };
}
async function collectFolderEntryPaths(entries, fileList){
  const paths = new Set();
  const firstFilePath = String((fileList && fileList[0] && (fileList[0].webkitRelativePath || fileList[0].name)) || "");
  const rootName = normalizedRunPath(firstFilePath).split("/")[0] || "";
  const addParents = (value, includeSelf=false) => {
    const parts = normalizedRunPath(value).split("/").filter(Boolean);
    const end = includeSelf ? parts.length : Math.max(0, parts.length - 1);
    for (let i = 1; i <= end; i++) paths.add(parts.slice(0, i).join("/"));
  };
  [...(fileList || [])].forEach(file => addParents(file.webkitRelativePath || file.name));
  const visit = async (entry) => {
    if (!entry) return;
    let path = normalizedRunPath(entry.fullPath || entry.name);
    if (rootName && path && path.split("/")[0] !== rootName) path = rootName + "/" + path;
    if (entry.isDirectory){
      if ((entry.name || "").charAt(0) === ".") return;
      addParents(path, true);
      let children = [];
      try { children = await readAllDirectoryEntries(entry); } catch(e){ console.warn(e); }
      for (const child of children) await visit(child);
    } else if (entry.isFile) {
      addParents(path);
    }
  };
  for (const entry of entries || []) await visit(entry);
  return [...paths].sort((a, b) => a.split("/").length - b.split("/").length || a.localeCompare(b));
}

// EXE에서는 Windows 폴더 선택창이 고른 실제 절대경로를 로컬 서버가 보관하고,
// 아래 핸들이 File System Access API와 같은 최소 인터페이스를 제공한다.
// 일반 브라우저에는 이 엔드포인트가 없으므로 기존 showDirectoryPicker를 그대로 사용한다.
const NATIVE_SOURCE_ROOTS_KEY = "classdock-native-source-roots:v1";
let nativeSourceFolderCapability = null;
function nativeSourceUrl(path, id, rel, extra=""){
  return path + "?id=" + encodeURIComponent(id || "") + "&path=" + encodeURIComponent(rel || "") + extra;
}
function nativeJoinRel(parent, name){
  const left = String(parent || "").replace(/\/+$/, "");
  const right = String(name || "").replace(/^\/+/, "");
  return left ? left + "/" + right : right;
}
function nativeJoinAbsolute(parent, name){
  return String(parent || "").replace(/[\\/]+$/, "") + "\\" + String(name || "").replace(/^[\\/]+/, "");
}
function nativeHandleError(response, text){
  const name = response && response.status === 404 ? "NotFoundError"
    : response && response.status === 403 ? "NotAllowedError"
    : "InvalidStateError";
  try { return new DOMException(text || name, name); }
  catch(_){ const error = new Error(text || name); error.name = name; return error; }
}
async function nativeSourceFetch(url, options={}){
  const response = await fetch(url, { cache:"no-store", ...options });
  if (!response.ok) throw nativeHandleError(response, await response.text());
  return response;
}
function rememberNativeSourceRoot(name, path){
  if (!name || !path) return;
  try {
    const saved = JSON.parse(localStorage.getItem(NATIVE_SOURCE_ROOTS_KEY) || "{}");
    saved[String(name)] = String(path);
    localStorage.setItem(NATIVE_SOURCE_ROOTS_KEY, JSON.stringify(saved));
  } catch(_){}
}
function rememberedNativeSourceRoot(name){
  try {
    const saved = JSON.parse(localStorage.getItem(NATIVE_SOURCE_ROOTS_KEY) || "{}");
    return String(saved && saved[String(name)] || "");
  } catch(_){ return ""; }
}
async function nativeSourceSupported(){
  if (nativeSourceFolderCapability != null) return nativeSourceFolderCapability;
  if (location.protocol !== "http:" && location.protocol !== "https:") return (nativeSourceFolderCapability = false);
  try {
    const response = await fetch("/source-folder-capability", { cache:"no-store" });
    nativeSourceFolderCapability = response.ok && (await response.text()).trim() === "yes";
  } catch(_){ nativeSourceFolderCapability = false; }
  return nativeSourceFolderCapability;
}
async function nativeSourceEntry(id, rel){
  const response = await nativeSourceFetch(nativeSourceUrl("/source-folder-entry", id, rel));
  return response.json();
}
async function nativeWritableBytes(value){
  if (value instanceof Blob) return new Uint8Array(await value.arrayBuffer());
  if (value instanceof ArrayBuffer) return new Uint8Array(value);
  if (ArrayBuffer.isView(value)) return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
  if (value && typeof value === "object" && value.type === "write") return nativeWritableBytes(value.data);
  return new TextEncoder().encode(String(value == null ? "" : value));
}
class NativeSourceFileHandle {
  constructor(rootId, rootPath, relPath, meta=null){
    this.kind = "file";
    this.__classdockNativeHandle = true;
    this.rootId = rootId;
    this.rootPath = rootPath;
    this.relPath = String(relPath || "").replace(/\\/g, "/").replace(/^\/+/, "");
    this.name = this.relPath.split("/").pop() || "file";
    this.nativePath = nativeJoinAbsolute(rootPath, this.relPath.replace(/\//g, "\\"));
    this.meta = meta || null;
  }
  async queryPermission(){ return "granted"; }
  async requestPermission(){ return "granted"; }
  async isSameEntry(other){
    return !!(other && other.kind === "file" && String(other.nativePath || "").toLowerCase() === this.nativePath.toLowerCase());
  }
  // 영상·오디오는 바이트를 받아오지 않는다. 수업 영상은 수 GB 짜리도 있어 통째로 옮기면 런처와
  // 브라우저가 각각 그만큼 메모리를 쓰고, 2GB 를 넘으면 런처의 byte[] 상한에 걸려 아예 열리지 않는다.
  // 대신 "런처가 아는 파일"이라는 표식만 들려 보낸다 — 재생은 /media-stream 이 Range 로 흘려보내고,
  // MP4 변환도 경로만 넘겨 처리한다(video-viewer.js).
  nativeMediaFile(meta){
    const file = new File([], this.name, { lastModified:Number(meta && meta.lastModified) || Date.now() });
    const define = (key, value) => {
      try { Object.defineProperty(file, key, { value, configurable:true }); } catch(_){}
    };
    define("size", Number(meta && meta.size) || 0);
    define("__nativeAbsolutePath", this.nativePath);
    define("__nativeSource", { rootId:this.rootId, relPath:this.relPath });
    return typeof withFileHandle === "function" ? withFileHandle(file, this) : file;
  }
  async getFile(){
    let meta = this.meta;
    if (!meta) meta = await nativeSourceEntry(this.rootId, this.relPath);
    if (typeof isMediaFileName === "function" && isMediaFileName(this.name)) return this.nativeMediaFile(meta);
    const response = await nativeSourceFetch(nativeSourceUrl("/source-folder-file", this.rootId, this.relPath));
    const blob = await response.blob();
    let file = new File([blob], this.name, {
      type:blob.type || "application/octet-stream",
      lastModified:Number(meta && meta.lastModified) || Date.now()
    });
    try { Object.defineProperty(file, "__nativeAbsolutePath", { value:this.nativePath, configurable:true }); } catch(_){}
    if (typeof withFileHandle === "function") file = withFileHandle(file, this);
    return file;
  }
  async createWritable(){
    const handle = this;
    let bytes = new Uint8Array(0), closed = false;
    return {
      async write(value){
        if (closed) throw new Error("writable-closed");
        bytes = await nativeWritableBytes(value);
      },
      async close(){
        if (closed) return;
        closed = true;
        await nativeSourceFetch(nativeSourceUrl("/source-folder-file", handle.rootId, handle.relPath), {
          method:"POST",
          headers:{ "Content-Type":"application/octet-stream", "X-ClassDock-Action":"1" },
          body:bytes
        });
        handle.meta = { kind:"file", name:handle.name, size:bytes.byteLength, lastModified:Date.now() };
      },
      async abort(){ closed = true; }
    };
  }
}
class NativeSourceDirectoryHandle {
  constructor(rootId, rootPath, relPath=""){
    this.kind = "directory";
    this.__classdockNativeHandle = true;
    this.rootId = rootId;
    this.rootPath = String(rootPath || "").replace(/[\\/]+$/, "");
    this.relPath = String(relPath || "").replace(/\\/g, "/").replace(/^\/+|\/+$/g, "");
    this.name = this.relPath ? this.relPath.split("/").pop() : (this.rootPath.split(/[\\/]/).pop() || this.rootPath);
    this.nativePath = this.relPath ? nativeJoinAbsolute(this.rootPath, this.relPath.replace(/\//g, "\\")) : this.rootPath;
  }
  async queryPermission(){ return "granted"; }
  async requestPermission(){ return "granted"; }
  async isSameEntry(other){
    return !!(other && other.kind === "directory" && String(other.nativePath || "").toLowerCase() === this.nativePath.toLowerCase());
  }
  async resolve(other){
    if (!other || !other.nativePath) return null;
    const base = this.nativePath.replace(/[\\/]+$/, "");
    const target = String(other.nativePath).replace(/[\\/]+$/, "");
    if (target.toLowerCase() === base.toLowerCase()) return [];
    const prefix = base + "\\";
    if (!target.toLowerCase().startsWith(prefix.toLowerCase())) return null;
    return target.slice(prefix.length).split(/[\\/]/).filter(Boolean);
  }
  async *values(){
    const response = await nativeSourceFetch(nativeSourceUrl("/source-folder-list", this.rootId, this.relPath));
    const data = await response.json();
    for (const item of (data.items || [])){
      const rel = nativeJoinRel(this.relPath, item.name);
      yield item.kind === "directory"
        ? new NativeSourceDirectoryHandle(this.rootId, this.rootPath, rel)
        : new NativeSourceFileHandle(this.rootId, this.rootPath, rel, item);
    }
  }
  async getDirectoryHandle(name, options={}){
    const rel = nativeJoinRel(this.relPath, name);
    try {
      const item = await nativeSourceEntry(this.rootId, rel);
      if (item.kind !== "directory") throw nativeHandleError(null, "source-entry-is-file");
    } catch(error){
      if (!options.create || error.name !== "NotFoundError") throw error;
      await nativeSourceFetch(nativeSourceUrl("/source-folder-directory", this.rootId, rel), {
        method:"POST", headers:{ "X-ClassDock-Action":"1" }
      });
    }
    return new NativeSourceDirectoryHandle(this.rootId, this.rootPath, rel);
  }
  async getFileHandle(name, options={}){
    const rel = nativeJoinRel(this.relPath, name);
    let meta = null;
    try {
      meta = await nativeSourceEntry(this.rootId, rel);
      if (meta.kind !== "file") throw nativeHandleError(null, "source-entry-is-directory");
    } catch(error){
      if (!options.create || error.name !== "NotFoundError") throw error;
      await nativeSourceFetch(nativeSourceUrl("/source-folder-file", this.rootId, rel), {
        method:"POST",
        headers:{ "Content-Type":"application/octet-stream", "X-ClassDock-Action":"1" },
        body:new Uint8Array(0)
      });
      meta = { kind:"file", name:String(name), size:0, lastModified:Date.now() };
    }
    return new NativeSourceFileHandle(this.rootId, this.rootPath, rel, meta);
  }
  async removeEntry(name, options={}){
    const rel = nativeJoinRel(this.relPath, name);
    await nativeSourceFetch(nativeSourceUrl("/source-folder-remove", this.rootId, rel,
      "&recursive=" + (options.recursive ? "1" : "0")), {
      method:"POST", headers:{ "X-ClassDock-Action":"1" }
    });
  }
}
async function chooseNativeSourceFolder(){
  if (!(await nativeSourceSupported())) return { supported:false, handle:null };
  const headers = { "X-ClassDock-Action":"1" };
  await nativeSourceFetch("/choose-source-folder", { method:"POST", headers });
  for (let attempt = 0; attempt < 1200; attempt++){
    await new Promise(resolve => setTimeout(resolve, 250));
    const response = await nativeSourceFetch("/choose-source-folder-status", { headers });
    const status = await response.json();
    if (status.state === "opening") continue;
    if (status.state === "cancelled") return { supported:true, handle:null };
    if (status.state === "saved" && status.id && status.result){
      const handle = new NativeSourceDirectoryHandle(status.id, status.result);
      rememberNativeSourceRoot(handle.name, status.result);
      return { supported:true, handle };
    }
    throw new Error(status.result || "folder-picker-failed");
  }
  throw new Error("folder-picker-timeout");
}
async function restoreNativeSourceFolder(rootName){
  if (!(await nativeSourceSupported())) return null;
  const path = rememberedNativeSourceRoot(rootName);
  if (!path) return null;
  try {
    const response = await nativeSourceFetch("/source-folder-restore", {
      method:"POST",
      headers:{ "Content-Type":"text/plain;charset=utf-8", "X-ClassDock-Action":"1" },
      body:path
    });
    const data = await response.json();
    return data.id && data.path ? new NativeSourceDirectoryHandle(data.id, data.path) : null;
  } catch(_){ return null; }
}
async function chooseFolderHandle(startIn=null){
  // EXE에서는 Windows가 돌려준 실제 경로를 우선한다. 이 핸들은 재실행 뒤에도 런처가
  // 직접 복원하므로 Chromium의 사이트별 폴더 쓰기 권한을 매번 다시 묻지 않는다.
  try {
    const native = await chooseNativeSourceFolder();
    if (native.supported) return native.handle; // 취소도 그대로 끝내 브라우저 권한창으로 넘어가지 않는다
  } catch(error){
    console.warn("native source folder picker failed:", error);
    nativeSourceFolderCapability = false;
    if (typeof toast === "function")
      toast("Windows 폴더 선택창을 열지 못해 기본 폴더 선택창으로 전환합니다.", 3600);
  }
  const supportsBrowserPicker = typeof window !== "undefined" && typeof window.showDirectoryPicker === "function";
  if (supportsBrowserPicker){
    const options = { mode: "read" };
    if (startIn && startIn.kind === "directory") options.startIn = startIn;
    try { return await window.showDirectoryPicker(options); }
    catch(e){
      if (options.startIn && !(e && e.name === "AbortError")){
        try { return await window.showDirectoryPicker({ mode: "read" }); }
        catch(f){
          if (!(f && f.name === "AbortError")) console.warn("directory picker failed:", f);
          return null;
        }
      }
      if (!(e && e.name === "AbortError")) console.warn("directory picker failed:", e);
      return null;
    }
  }
  return null;
}
// 폴더로 연 파일은 항상 원본 파일에 바로 저장한다(별도 컨펌 없이). 폴더 핸들에 쓰기 권한을 한 번 받아
// 두면 하위 파일 저장이 매번 권한 팝업 없이 조용히 진행된다(권한은 하위로 상속). 권한을 못 받아도
// 원본 저장 모드는 켠 채 두어, 첫 저장 때 파일 단위로 다시 권한을 요청하게 한다.
async function ensureFolderWriteAccess(handle){
  if (!handle || handle.kind !== "directory" || typeof handle.requestPermission !== "function") return;
  try {
    let permission = typeof handle.queryPermission === "function"
      ? await handle.queryPermission({ mode:"readwrite" })
      : "prompt";
    if (permission !== "granted") permission = await handle.requestPermission({ mode:"readwrite" });
  } catch(e){ console.warn("folder write permission request failed:", e); }
}
// 이미 열린 폴더 트리와 새로 여는 폴더의 관계를 핸들로 판별한다. 하위 폴더는 루트가 달라
// 경로 문자열로는 같은 파일임을 알 수 없으므로 isSameEntry/resolve 만이 유일한 비교 수단이다.
// same=같은 폴더, child=새 폴더가 기존 트리 안, parents=기존 트리(들)가 새 폴더 안.
async function classifyRelatedFolderRoots(handle){
  const result = { same:null, child:null, parents:[] };
  if (!handle || handle.kind !== "directory" || typeof handle.isSameEntry !== "function") return result;
  const roots = navNodes.filter(n => workspaceNodeVisible(n) && n.type === "group" && n.folderRefreshRootId === n.nodeId
    && n.folderHandle && n.folderHandle.kind === "directory");
  for (const root of roots){
    try {
      if (await root.folderHandle.isSameEntry(handle)){ if (!result.same) result.same = root; continue; }
      if (typeof root.folderHandle.resolve !== "function") continue;
      if (await root.folderHandle.resolve(handle)){ if (!result.child) result.child = root; continue; }
      if (await handle.resolve(root.folderHandle)) result.parents.push(root);
    } catch(e){ console.warn("폴더 포함 관계 확인 실패:", e); }   // 비교 실패 시 기존처럼 새 트리로 연다
  }
  return result;
}
// 새로 여는 폴더가 이미 열린 트리를 포함하면(부모 폴더를 다시 연 경우) 작은 트리를 닫아 하나로 합친다.
// 같은 디스크 파일이 문서 두 벌로 열려 서로의 저장을 덮어쓰는 것을 막기 위해서다.
// 저장하지 않은 편집이 있는 트리는 닫지 않고 경고만 한다 — 중복보다 편집 유실이 더 나쁘다.
function absorbContainedFolderRoots(parents){
  const keptNames = [];
  for (const root of parents){
    const branchIds = navBranchIds(root.nodeId);
    const hasUnsaved = docs.some(doc => branchIds.has(doc.nodeId) &&
      (doc.hasUnsavedEdits || (doc.isScratch && !doc._named) ||
       (doc.kind === "pdf" && doc.elements && doc.elements.length)));
    if (hasUnsaved){ keptNames.push(root.name); continue; }
    closeGroup(root.nodeId, { forgetWorkspace:true });
    toast("이미 열려 있던 '" + root.name + "' 폴더를 새로 연 폴더 안으로 합쳤어요.", 3200);
  }
  if (keptNames.length)
    toast("'" + keptNames.join("', '") + "'에 저장하지 않은 편집이 있어 그대로 두었어요. 같은 파일이 두 곳에 열려 있으니 저장에 주의하세요.", 5600);
}
async function pickFolderOrInput(input){
  pendingFolderRefreshId = null;
  const supportsNativePicker = typeof nativeSourceSupported === "function" && await nativeSourceSupported();
  const supportsPicker = typeof window !== "undefined" && typeof window.showDirectoryPicker === "function";
  if (!supportsNativePicker && !supportsPicker){
    if (input) input.click();
    return;
  }
  const handle = await chooseFolderHandle();
  if (!handle) return;
  // 같은 폴더/하위 폴더를 다시 열면 새 트리(중복 문서·저장 충돌)를 만들지 않고 기존 트리를 새로고침한다.
  const related = await classifyRelatedFolderRoots(handle);
  if (related.same || related.child){
    const root = related.same || related.child;
    if (related.same){
      root.folderHandle = handle;          // 방금 고른 핸들이 권한이 확실하다 — 교체
      await ensureFolderWriteAccess(handle);
      root.originalSaveMode = true;        // '폴더 열기'로 다시 열었으니 원본 저장 모드로 승격
      rememberFolderHandle(root, root.name);
    }
    toast(related.same
      ? "'" + root.name + "' 폴더는 이미 열려 있어요. 새로 여는 대신 동기화합니다."
      : "선택한 폴더는 이미 열린 '" + root.name + "' 안에 있어요. '" + root.name + "'을(를) 동기화합니다.", 3400);
    await requestFolderRefresh(root.nodeId);
    return;
  }
  await ensureFolderWriteAccess(handle);   // 원본 저장용 쓰기 권한 1회 확보(컨펌 창 없이)
  if (related.parents.length) absorbContainedFolderRoots(related.parents);
  showLoading("폴더 파일 확인 중…");
  try {
    const snapshot = await collectDirectoryHandleFiles(handle);
    reportSkippedFolderEntries(snapshot.skipped);
    queueFolder(snapshot.files, { folderHandle: handle, folderPaths: snapshot.folderPaths, originalSaveMode: true });
  } catch(e){
    if (e && e.message === "operation-cancelled") toast("폴더 열기를 취소했어요.");
    else { console.error(e); toast("폴더를 읽지 못했어요.", 3000); }
  } finally {
    hideLoading();
  }
}
function handleFolderInputSelection(fileList, options={}){
  const refreshId = pendingFolderRefreshId;
  pendingFolderRefreshId = null;
  if (refreshId) return queueFolderRefresh(refreshId, fileList, options);
  return queueFolder(fileList, options);
}
function clearPendingFolderRefresh(){ pendingFolderRefreshId = null; }
function queueFolder(fileList, options={}){
  const files = [...fileList];
  if (!files.length && !(options.folderPaths && options.folderPaths.length)) return fileQueue;
  let keepOpenedGroupExpanded = false;
  fileQueue = fileQueue.then(() => runUiBatch(async () => {
    // 자동 복원 저장은 폴더를 먼저 연 뒤에 한다 — 수백 MB 복사를 기다리지 않고 바로 화면에 뜨게.
    const replaceWorkspace = navNodes.length === 0;
    const rootGroup = await openFolderFiles(files, options);
    keepOpenedGroupExpanded = !!rootGroup && !options.restoreFromWorkspace && !autoOpenFirstFileEnabled();
    // webkitdirectory 폴백은 빈 폴더 경로를 주지 않는다. 그래도 상대경로의 루트는 남겨야
    // 대량 이미지가 자동 복원에서 생략됐다는 표식을 다음 실행에도 복원할 수 있다.
    const folderPaths = options.folderPaths && options.folderPaths.length
      ? options.folderPaths
      : [...new Set(files
          .map(file => normalizedRunPath(file && file.webkitRelativePath || ""))
          .filter(path => path.includes("/"))
          .map(path => path.split("/")[0]))];
    if (files.length || folderPaths.length){
      const originalSaveFolderPaths = rootGroup && rootGroup.originalSaveMode
        ? [rootGroup.name]
        : [];
      await rememberWorkspace(files, replaceWorkspace, { silent: true, folderPaths, originalSaveFolderPaths });
    }
  }))
    .then(() => {
      // 자동으로 파일을 고르지 않은 경우에는 방금 연 폴더를 펼친 상태를 유지한다.
      // 기존 활성 문서의 분기로 다시 접으면 사용자가 새 폴더 파일을 고를 수 없게 된다.
      if (!keepOpenedGroupExpanded) collapseToActiveBranch();
    })
    .catch((e) => { if (e && e.message === "operation-cancelled") toast("폴더 열기를 취소했어요."); else console.error(e); });
  return fileQueue;
}

async function folderOpenableFiles(fileList){
  // 열 수 있는 형식 + 숨김파일 제외(.env 계열은 예외)
  const openable = [];
  for (const f of [...fileList]){
    const base = f.name || "";
    if (!base || isHiddenFolderEntry(f.webkitRelativePath || base)) continue;
    if (ZIP_OPENABLE.includes(fileExtOf(base)) || await isLikelyTextFile(f)) openable.push(f);
  }
  return openable;
}
async function openFolderFiles(fileList, options={}){
  const openable = await folderOpenableFiles(fileList);
  const folderPaths = [...new Set((options.folderPaths || []).map(normalizedRunPath).filter(Boolean))]
    .sort((a, b) => a.split("/").length - b.split("/").length || a.localeCompare(b));
  const pendingImageFolderPaths = [...new Set((options.pendingImageFolderPaths || []).map(normalizedRunPath).filter(Boolean))];
  const sourceSkipFolderPaths = [...new Set((options.sourceSkipFolderPaths || []).map(normalizedRunPath).filter(Boolean))];
  if (!openable.length && !folderPaths.length){ toast("폴더 안에 열 수 있는 파일이나 폴더가 없어요.", 3000); return; }

  const rootName = ((openable[0] && openable[0].webkitRelativePath || folderPaths[0] || "").split("/")[0]) || "폴더";
  const rootGroup = makeGroup("folder", rootName, null);
  rootGroup.folderRefreshRootId = rootGroup.nodeId;
  rootGroup.originalSaveMode = !!options.originalSaveMode;
  rootGroup.folderHandle = options.folderHandle || null;
  rootGroup.nativeRootPath = String(options.nativeRootPath ||
    (rootGroup.folderHandle && rootGroup.folderHandle.__classdockNativeHandle && rootGroup.folderHandle.nativePath) || "");
  rememberFolderHandle(rootGroup, rootName);   // 핸들 IDB 보관/복구 — 재실행 뒤에도 '폴더 새로고침'이 권한 1클릭으로 동작
  // 대량 사진이 자동 복원에서 생략됐다는 표식이 있으면, 다른 문서가 함께 복원됐어도 실제 폴더를 다시 읽을 수 있게 한다.
  rootGroup.restorePendingImages = !!options.restoreFromWorkspace && pendingImageFolderPaths.some(path => path === rootName);
  rootGroup.imageSkipWorkspacePath = workspaceImageSkipMarkerPath(rootName);
  // 용량이 커서 파일 내용 없이 폴더 위치만 기억한 원본 폴더 — 복원 직후 디스크에서 다시 읽어 채운다.
  rootGroup.restorePendingSource = !!options.restoreFromWorkspace && sourceSkipFolderPaths.some(path => path === rootName);
  rootGroup.folderPaths = folderPaths.length ? folderPaths : [rootName];
  rootGroup.workspacePaths = [
    ...[...fileList].map(f => f.webkitRelativePath || (rootName + "/" + f.name)),
    ...rootGroup.folderPaths.map(workspaceFolderMarkerPath),
    ...(rootGroup.originalSaveMode
      ? [workspaceOriginalSaveMarkerPath(rootName), workspaceSourceSkipMarkerPath(rootName)] : [])
  ];
  const workspacePathsByFolder = indexWorkspacePathsByFolder(rootGroup.workspacePaths);
  // 폴더 전체(데이터 파일 포함, 숨김 경로 제외)를 옆 파일로 묶는다 — .py 실행 시 import/파일읽기 지원
  const folderCtx = makeFileSiblingCtx(
    [...fileList]
      .filter(f => !isHiddenFolderEntry(f.webkitRelativePath || f.name || ""))
      .map(f => ({ file: f, relPath: f.webkitRelativePath || (rootName + "/" + f.name) })),
    rootName,
    folderPaths
  );
  rootGroup.newPythonContext = {
    parentId: rootGroup.nodeId, dir: rootName, archiveCtx: folderCtx, label: rootName
  };
  const folders = new Map();                 // 상대경로 key → 그룹 nodeId
  const parentFor = (relPath) => {
    const parts = String(relPath || "").split("/").filter(Boolean);
    parts.pop();                             // 파일명 제거
    if (parts.length) parts.shift();         // 루트 폴더명 제거(rootGroup 이 담당)
    let parentId = rootGroup.nodeId, key = rootName;
    for (const part of parts){
      key += "/" + part;
      if (!folders.has(key)){
        const subgroup = makeGroup("folder", part, parentId);
        subgroup.workspacePaths = workspacePathsByFolder.get(key) || [];
        subgroup.newPythonContext = {
          parentId: subgroup.nodeId, dir: key, archiveCtx: folderCtx, label: part
        };
        subgroup.folderRefreshRootId = rootGroup.nodeId;
        folders.set(key, subgroup.nodeId);
      }
      parentId = folders.get(key);
    }
    return parentId;
  };
  // FileList에는 빈 디렉터리가 들어오지 않지만, Chrome/Edge의 디렉터리 핸들에서는
  // 폴더 경로를 별도로 수집할 수 있다. 파일을 열기 전에 그 경로로 빈 그룹까지 만든다.
  folderPaths.forEach(path => parentFor(path + "/.__empty_folder__"));

  showLoading("폴더 여는 중…");
  let opened = 0;
  for (const f of openable){
    try {
      const rel = f.webkitRelativePath || (rootName + "/" + f.name);
      const parentId = parentFor(rel);
      await handleFiles([f], { parentId, bulk: true, relPath: rel, archiveCtx: folderCtx,
        nativeAbsolutePath:f.__nativeAbsolutePath || (rootGroup.nativeRootPath
          ? nativeJoinAbsolute(rootGroup.nativeRootPath, rel.split("/").slice(1).join("\\"))
          : null),
        originalSaveMode: rootGroup.originalSaveMode,
        restoreFromWorkspace: !!options.restoreFromWorkspace });   // 첫 개만 즉시 렌더, 나머지 지연
      opened++;
      // 진행 표시·양보는 묶어서 — 파일마다 하면 수천 개 폴더에서 그 비용만 수십 초가 된다.
      if (opened % 20 === 0 || opened === openable.length) updateLoading(`폴더 여는 중… (${opened}/${openable.length})`);
      await yieldToBrowserThrottled();
    } catch(e){ if (e && e.message === "operation-cancelled") throw e; console.error(e); }
  }
  hideLoading();
  if (!opened && !folderPaths.length){ await closeGroup(rootGroup.nodeId); toast("폴더를 열지 못했어요.", 3000); return null; }
  // 폴더를 열었다고 안의 파일 하나를 멋대로 띄우지 않는다(설정에서 되돌릴 수 있음).
  // 자동 복원은 예외 — 지난번에 보던 문서를 그대로 되살려야 하므로 배치 활성화를 그대로 둔다.
  if (!options.restoreFromWorkspace && !autoOpenFirstFileEnabled())
    suppressUiBatchAutoOpen(rootGroup.nodeId, { expand: options.expandRoot !== false });
  // 폴더 핸들이 있을 때만 최근 목록에 남긴다 — 없으면 다시 열어도 되살릴 수 없다.
  if (rootGroup.folderHandle && typeof MNRecent !== "undefined") MNRecent.rememberFolder(rootName);
  if (!options.silent){
    const subfolderCount = Math.max(0, folders.size);
    const summary = opened ? opened + "개 파일" : "빈 폴더";
    toast(summary + (subfolderCount ? " · 폴더 " + subfolderCount + "개" : "") + " 열기", 2800);
  }
  return rootGroup;
}

function navBranchIds(rootId){
  const ids = new Set([rootId]);
  let changed = true;
  while (changed){
    changed = false;
    navNodes.forEach(node => {
      if (!ids.has(node.nodeId) && ids.has(node.parentId)){
        ids.add(node.nodeId);
        changed = true;
      }
    });
  }
  return ids;
}
function groupStablePath(node){
  if (!node) return "";
  if (node.newPythonContext && node.newPythonContext.dir) return normalizedRunPath(node.newPythonContext.dir);
  const parts = [node.name || ""];
  let parentId = node.parentId;
  while (parentId){
    const parent = navNodes.find(item => item.nodeId === parentId && item.type === "group");
    if (!parent) break;
    parts.unshift(parent.name || "");
    parentId = parent.parentId;
  }
  return parts.filter(Boolean).join("/");
}

function physicalFolderRootForNode(node){
  if (!node || node.type !== "group" || !node.folderRefreshRootId || !node.newPythonContext) return null;
  return navNodes.find(item => item.nodeId === node.folderRefreshRootId && item.type === "group"
    && item.folderRefreshRootId === item.nodeId) || null;
}

// 실제 디렉터리 핸들이 있는 일반 폴더에서만 '새 폴더' 메뉴를 노출한다.
// webkitdirectory 폴백(읽기 전용)과 ZIP/TAR 묶음에는 핸들이 없으므로 메뉴가 나타나지 않는다.
function canCreateFolderOnDisk(node){
  const root = physicalFolderRootForNode(node);
  return !!(root && root.folderHandle && root.folderHandle.kind === "directory"
    && typeof root.folderHandle.getDirectoryHandle === "function");
}

function folderNameValidationMessage(value){
  const name = String(value == null ? "" : value).trim();
  if (!name) return "폴더 이름을 입력해 주세요.";
  if (name === "." || name === "..") return "'.' 또는 '..'은 폴더 이름으로 사용할 수 없어요.";
  if (name.charAt(0) === ".") return "점(.)으로 시작하는 숨김 폴더는 사이드바에서 지원하지 않아요.";
  if (/[\u0000-\u001f\\/:*?"<>|]/.test(name)) return "폴더 이름에 \\\\ / : * ? \" < > | 문자를 사용할 수 없어요.";
  if (/[. ]$/.test(name)) return "폴더 이름은 점(.)이나 공백으로 끝날 수 없어요.";
  if (/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(name)) return "Windows에서 사용할 수 없는 예약된 폴더 이름이에요.";
  if (name.length > 255) return "폴더 이름은 255자 이내로 입력해 주세요.";
  return "";
}

async function requestDirectoryWritePermission(handle){
  if (!handle || handle.kind !== "directory") return false;
  try {
    let permission = typeof handle.queryPermission === "function"
      ? await handle.queryPermission({ mode:"readwrite" }) : "granted";
    if (permission !== "granted" && typeof handle.requestPermission === "function")
      permission = await handle.requestPermission({ mode:"readwrite" });
    return permission === "granted";
  } catch(_){
    return false;
  }
}

async function directoryHandleForGroup(node, root){
  if (!node || !root || !root.folderHandle) return null;
  const rootPath = normalizedRunPath(root.name || "");
  const targetPath = groupStablePath(node);
  const parts = targetPath.split("/").filter(Boolean);
  if (parts.length && parts[0].toLocaleLowerCase() === rootPath.toLocaleLowerCase()) parts.shift();
  else if (node.nodeId !== root.nodeId) return null;
  let handle = root.folderHandle;
  for (const part of parts) handle = await handle.getDirectoryHandle(part);
  return handle;
}

async function directoryEntryWithName(handle, name){
  if (!handle || typeof handle.values !== "function") return null;
  const key = name.toLocaleLowerCase();
  for await (const entry of handle.values()){
    if (entry && String(entry.name || "").toLocaleLowerCase() === key) return entry;
  }
  return null;
}

async function createFolderOnDisk(node){
  if (!canCreateFolderOnDisk(node) || typeof askText !== "function"){
    toast("이 폴더에는 실제 폴더를 만들 수 없어요.", 3200);
    return false;
  }
  const translate = text => (typeof window.t === "function" ? window.t(text) : text);
  const entered = await askText({
    title: translate("새 폴더"),
    message: translate("우클릭한 폴더 안에 실제 폴더를 만듭니다."),
    value: translate("새 폴더"),
    okText: translate("만들기")
  });
  if (entered === null) return false;
  const name = String(entered).trim();
  const invalid = folderNameValidationMessage(name);
  if (invalid){ toast(invalid, 3600, { type:"error" }); return false; }

  const root = physicalFolderRootForNode(node);
  if (!root || !(await requestDirectoryWritePermission(root.folderHandle))){
    toast("실제 폴더를 만들려면 원본 폴더 쓰기 권한이 필요해요.", 4200, { type:"error" });
    return false;
  }

  let targetHandle;
  try {
    targetHandle = await directoryHandleForGroup(node, root);
    if (!targetHandle) throw new Error("folder-target-unavailable");
    const existing = await directoryEntryWithName(targetHandle, name);
    if (existing){
      toast(existing.kind === "directory"
        ? "같은 이름의 폴더가 이미 있어요."
        : "같은 이름의 파일이 이미 있어요.", 3400, { type:"error" });
      return false;
    }
    await targetHandle.getDirectoryHandle(name, { create:true });
  } catch(error){
    console.warn("physical folder create failed:", error);
    if (error && error.name === "NotAllowedError")
      toast("원본 폴더에 새 폴더를 만들 권한이 없어요.", 3600, { type:"error" });
    else
      toast("실제 폴더를 만들지 못했어요.", 3600, { type:"error" });
    return false;
  }

  // 빈 폴더도 수집하는 기존 동기화 경로를 사용해 트리·자동 복원 정보를 함께 갱신한다.
  node.expanded = true;
  await requestFolderRefresh(root.nodeId);
  const createdPath = normalizedRunPath(groupStablePath(node) + "/" + name);
  const created = navNodes.find(item => item.type === "group" && groupStablePath(item) === createdPath);
  if (created){
    sidebarCursorKey = created.nodeId;
    renderSidebar();
    const list = byId("fileList");
    const row = list && [...list.querySelectorAll("[data-node-id]")].find(item => item.dataset.nodeId === created.nodeId);
    if (row && typeof focusSidebarItem === "function") focusSidebarItem(row, { focus:true });
  }
  toast("'" + name + "' 폴더를 디스크에 만들었어요.", 3200, { type:"success" });
  return true;
}

// 권한창을 띄우지 않고 바로 다시 읽을 수 있는 폴더 핸들인지 — 시작 직후 자동 동기화에만 쓴다.
async function folderHandleReadyForSilentRefresh(handle){
  if (!handle || handle.kind !== "directory") return false;
  if (handle.__classdockNativeHandle) return true;      // 런처가 실제 경로로 읽으므로 권한창이 없다
  try {
    return typeof handle.queryPermission === "function"
      ? await handle.queryPermission({ mode:"read" }) === "granted"
      : true;
  } catch(_){ return false; }
}

async function requestFolderRefresh(rootId){
  const root = navNodes.find(node => node.nodeId === rootId && node.type === "group" && node.folderRefreshRootId === node.nodeId);
  if (!root) return;
  let handle = root.folderHandle || null;
  const reuseFiles = folderSnapshotReuseMap(rootId);
  if (handle && handle.kind === "directory" && await ensureReadPermission(handle)){
    showLoading("폴더 변경 내용 확인 중…");
    try {
      const snapshot = await collectDirectoryHandleFiles(handle, { reuseFiles });
      root.folderHandle = handle;
      reportSkippedFolderEntries(snapshot.skipped);
      return queueFolderRefresh(rootId, snapshot.files, { folderHandle: handle, folderPaths: snapshot.folderPaths,
        skipped: snapshot.skipped, originalSaveMode:!!root.originalSaveMode });
    } catch(e){
      if (e && e.message === "operation-cancelled") toast("폴더 동기화를 취소했어요.");
      else {
        console.error(e);
        // 일시적 실패(권한 만료·파일 잠김)가 흔하다 — 사이드바를 찾아가지 않고 바로 다시 시도하게 한다.
        toast("폴더를 다시 읽지 못했어요.", 5200, { type:"error",
          action:{ label:"다시 시도", onClick:() => requestFolderRefresh(rootId) } });
      }
    } finally {
      hideLoading();
    }
    return;
  }

  const supportsPicker = typeof window !== "undefined" && typeof window.showDirectoryPicker === "function";
  if (supportsPicker){
    const picked = await chooseFolderHandle(root.folderHandle || null);
    if (!picked) return;
    await ensureFolderWriteAccess(picked);
    showLoading("폴더 변경 내용 확인 중…");
    try {
      const snapshot = await collectDirectoryHandleFiles(picked, { reuseFiles });
      reportSkippedFolderEntries(snapshot.skipped);
      return queueFolderRefresh(rootId, snapshot.files, { folderHandle: picked, folderPaths: snapshot.folderPaths,
        skipped: snapshot.skipped, originalSaveMode:true });
    } catch(e){
      if (e && e.message === "operation-cancelled") toast("폴더 동기화를 취소했어요.");
      else {
        console.error(e);
        // 일시적 실패(권한 만료·파일 잠김)가 흔하다 — 사이드바를 찾아가지 않고 바로 다시 시도하게 한다.
        toast("폴더를 다시 읽지 못했어요.", 5200, { type:"error",
          action:{ label:"다시 시도", onClick:() => requestFolderRefresh(rootId) } });
      }
    } finally {
      hideLoading();
    }
    return;
  }

  const input = byId("folderInput");
  if (!input) return;
  pendingFolderRefreshId = rootId;
  toast("'" + root.name + "' 폴더를 다시 선택해 주세요.", 3200);
  input.click();
}
function queueFolderRefresh(rootId, fileList, options={}){
  const files = [...fileList];
  if (!files.length && !(options.folderPaths && options.folderPaths.length)) return fileQueue;
  fileQueue = fileQueue
    .then(() => runUiBatch(() => refreshFolderGroup(rootId, files, options)))
    .catch((e) => {
      if (e && e.message === "operation-cancelled") toast("폴더 동기화를 취소했어요.");
      else { console.error(e); toast("폴더 동기화 중 오류가 났어요.", 3200); }
    });
  return fileQueue;
}
async function refreshFolderGroup(rootId, fileList, options={}){
  const root = navNodes.find(node => node.nodeId === rootId && node.type === "group");
  if (!root) return false;
  if (options.originalSaveMode) root.originalSaveMode = true;
  const files = [...fileList];
  const openable = await folderOpenableFiles(files);
  const folderPaths = [...new Set((options.folderPaths || []).map(normalizedRunPath).filter(Boolean))];
  if (!openable.length && !folderPaths.length){ toast("동기화할 수 있는 파일이나 폴더가 없어요.", 3200); return false; }
  const selectedRootName = ((openable[0] && openable[0].webkitRelativePath || folderPaths[0] || "").split("/")[0]) || "폴더";
  if (selectedRootName !== root.name){
    toast("'" + root.name + "' 폴더를 선택해야 동기화할 수 있어요.", 3600);
    return false;
  }

  // ===== 변경분만 반영(diff) =====
  // 예전에는 하위 문서를 전부 닫고 전부 다시 열었다 — 이미지 4천 장 폴더에선 새로고침 한 번이 첫 열기만큼 걸렸다.
  // 이제 경로+크기+수정시각이 같은 문서는 그대로 두고, 추가/변경/삭제된 파일만 처리한다.
  const branchIds = navBranchIds(rootId);
  const childDocs = docs.filter(doc => branchIds.has(doc.nodeId));
  // 이미지/PDF 갤러리는 실제 파일이 아닌 파생 탭이다. 변경 없는 새로고침에서 파일 삭제로
  // 오인해 닫지 않으며, 실제 파일 목록이 달라질 때만 함께 닫아 다음에 최신 목록으로 연다.
  const galleryDocs = childDocs.filter(doc => doc.kind === "image-gallery" || doc.kind === "pdf-gallery");
  const sourceDocs = childDocs.filter(doc => doc.kind !== "image-gallery" && doc.kind !== "pdf-gallery");
  const docKeyOf = (doc) => normalizedRunPath(doc.workspacePath || doc.relPath || doc.name);
  const fileKeyOf = (file) => normalizedRunPath(file.webkitRelativePath || (selectedRootName + "/" + file.name));
  const nextByKey = new Map(openable.map(file => [fileKeyOf(file), file]));
  // 잠겨 있거나 접근이 막혀 이번 스냅샷에서 빠진 항목 — '삭제됨'이 아니므로 문서를 닫지 않는다.
  const skippedFiles = new Set((options.skipped || [])
    .filter(item => item && item.kind !== "directory").map(item => normalizedRunPath(item.path)));
  const skippedDirs = (options.skipped || [])
    .filter(item => item && item.kind === "directory").map(item => normalizedRunPath(item.path)).filter(Boolean);
  const unreadable = (key) => folderSyncUnreadablePath(key, skippedFiles, skippedDirs);
  const keptDocs = [], dropDocs = [];
  let changedCount = 0, removedCount = 0;
  for (const doc of sourceDocs){
    const key = docKeyOf(doc);
    const file = nextByKey.get(key);
    // 아직 저장하지 않은 새 문서(폴더 우클릭 '새로 만들기')는 디스크에 없는 게 정상이다.
    // 삭제된 파일로 오인해 닫으면 새 폴더를 만드는 것만으로 작성 중이던 내용이 사라진다.
    if (!file && doc.isScratch && !doc._named){ keptDocs.push(doc); continue; }
    // 자동 복원 스냅샷으로 되살린 편집본은 디스크보다 새롭다 — 동기화가 디스크 내용으로 되돌리지 않는다.
    if (doc.workspaceRecovery){ keptDocs.push(doc); nextByKey.delete(key); continue; }
    if (!file && unreadable(key)){ keptDocs.push(doc); continue; }
    const unchanged = !!file && doc.__srcMtime != null &&
      (Number(doc.size) || 0) === (Number(file.size) || 0) && doc.__srcMtime === (file.lastModified || 0);
    if (unchanged){
      doc.fsHandle = file.__fsHandle || doc.fsHandle || null;
      doc.fsDirHandle = file.__fsDirHandle || doc.fsDirHandle || null;
      doc.nativeAbsolutePath = file.__nativeAbsolutePath || doc.nativeAbsolutePath || null;
      doc.originalSaveMode = !!root.originalSaveMode;
      keptDocs.push(doc); nextByKey.delete(key);
    }
    else { dropDocs.push(doc); if (file) changedCount++; else removedCount++; }
  }
  const addFiles = openable.filter(file => nextByKey.has(fileKeyOf(file)));
  const shouldCloseGalleries = dropDocs.length > 0 || addFiles.length > 0;
  const docsToClose = shouldCloseGalleries ? dropDocs.concat(galleryDocs) : dropDocs;

  // 편집 내용 확인은 실제로 닫힐(변경·삭제) 문서만 대상으로 — 그대로 유지되는 문서의 편집은 살아남는다.
  const hasUnsaved = dropDocs.some(doc => doc.hasUnsavedEdits || (doc.isScratch && !doc._named));
  const editedPdfs = dropDocs.filter(doc => doc.kind === "pdf" && doc.elements && doc.elements.length);
  if (hasUnsaved || editedPdfs.length){
    const detail = hasUnsaved && editedPdfs.length ? "저장하지 않은 코드와 PDF 편집" : (hasUnsaved ? "저장하지 않은 코드" : "PDF 편집");
    const ok = await confirmDialog(detail + "이 있습니다. 폴더를 동기화하면 해당 내용이 사라질 수 있어요.", "동기화", "취소");
    if (!ok) return false;
  }

  // 닫히는 문서의 탭·활성·분할 참조는 경로로 기억해 두었다가, 같은 경로로 다시 열리면 이어준다.
  const droppedIds = new Set(docsToClose.map(doc => doc.id));
  const droppedPathOf = new Map(dropDocs.map(doc => [doc.id, docKeyOf(doc)]));
  const refForId = (id) => {
    if (id == null || !docs.some(doc => doc.id === id)) return null;
    if (!droppedIds.has(id)) return { id };
    const path = droppedPathOf.get(id);
    return path ? { path } : null;  // 파생 탭(갤러리)은 실제 파일로 복원하지 않는다.
  };
  const tabRefs = tabOrder.map(refForId).filter(Boolean);
  const activeRef = refForId(activeId);
  const studyRef = refForId(studyPdfId);
  const mruRefs = activeMru.map(refForId).filter(Boolean);
  const oldPaths = [...(root.workspacePaths || [])].map(normalizedRunPath);
  const retainedWorkspacePaths = retainedWorkspacePathsForSkipped(oldPaths, skippedFiles, skippedDirs);
  const previousFolderCtx = root.newPythonContext && root.newPythonContext.archiveCtx;

  for (const doc of editedPdfs){
    if (doc.recoveryKey && typeof deletePdfRecovery === "function") await deletePdfRecovery(doc.recoveryKey);
    doc.recoveryDirty = false;
  }
  docsToClose.forEach(doc => closeDoc(doc.id, { skipConfirm: true, skipPrune: true, skipUi: true }));

  // 새 옆파일 컨텍스트(파일 스냅샷이 바뀌므로 폴더 전체 기준으로 새로 만든다)
  const folderCtx = makeFileSiblingCtx(
    files
      .filter(f => !isHiddenFolderEntry(f.webkitRelativePath || f.name || ""))
      .map(f => ({ file: f, relPath: f.webkitRelativePath || (selectedRootName + "/" + f.name) })),
    selectedRootName,
    folderPaths
  );
  // 이번에 못 읽은 파일·폴더는 이전 바이트 스냅샷을 유지한다. 문서로 열지 않은 데이터 파일도
  // 포함해야 Python import와 상대경로 파일 읽기가 부분 동기화 뒤 갑자기 끊기지 않는다.
  if (previousFolderCtx && typeof previousFolderCtx.copyTo === "function")
    previousFolderCtx.copyTo(folderCtx, unreadable);

  // 기존 그룹 재사용 + 없는 폴더만 생성
  const groupByPath = new Map();
  navNodes.filter(node => branchIds.has(node.nodeId) && node.type === "group" && node.nodeId !== rootId)
    .forEach(node => groupByPath.set(groupStablePath(node), node));
  const parentFor = (relPath) => {
    const parts = String(relPath || "").split("/").filter(Boolean);
    parts.pop();
    if (parts.length) parts.shift();
    let parentId = root.nodeId, key = selectedRootName;
    for (const part of parts){
      key += "/" + part;
      let group = groupByPath.get(key);
      if (!group){
        group = makeGroup("folder", part, parentId);
        group.folderRefreshRootId = root.nodeId;
        group.newPythonContext = { parentId: group.nodeId, dir: key, archiveCtx: folderCtx, label: part };
        groupByPath.set(key, group);
      }
      parentId = group.nodeId;
    }
    return parentId;
  };
  folderPaths.forEach(path => parentFor(path + "/.__empty_folder__"));

  showLoading("폴더 동기화 중…");
  let opened = 0;
  for (const f of addFiles){
    try {
      const rel = f.webkitRelativePath || (selectedRootName + "/" + f.name);
      await handleFiles([f], { parentId: parentFor(rel), bulk: true, relPath: rel, archiveCtx: folderCtx,
        nativeAbsolutePath:f.__nativeAbsolutePath || (root.nativeRootPath
          ? nativeJoinAbsolute(root.nativeRootPath, rel.split("/").slice(1).join("\\"))
          : null),
        originalSaveMode: !!root.originalSaveMode });
      opened++;
      if (opened % 20 === 0 || opened === addFiles.length) updateLoading(`폴더 동기화 중… (${opened}/${addFiles.length})`);
      await yieldToBrowserThrottled();
    } catch(e){ if (e && e.message === "operation-cancelled") throw e; console.error(e); }
  }
  hideLoading();

  // 사라진 폴더의 빈 그룹 제거(하위가 모두 비었을 때만, 안쪽부터)
  const validFolderSet = new Set([selectedRootName, ...folderPaths]);
  let prunedGroups = true;
  while (prunedGroups){
    prunedGroups = false;
    for (const [path, node] of [...groupByPath]){
      if (validFolderSet.has(path)) continue;
      if (navNodes.some(n => n.parentId === node.nodeId)) continue;
      const idx = navNodes.findIndex(n => n.nodeId === node.nodeId);
      if (idx >= 0) navNodes.splice(idx, 1);
      groupByPath.delete(path);
      prunedGroups = true;
    }
  }
  bumpNavTree();

  // 루트·하위 그룹 메타데이터를 새 스냅샷 기준으로 갱신
  root.folderHandle = options.folderHandle || root.folderHandle || null;
  root.nativeRootPath = String(options.nativeRootPath || root.nativeRootPath ||
    (root.folderHandle && root.folderHandle.__classdockNativeHandle && root.folderHandle.nativePath) || "");
  rememberFolderHandle(root, selectedRootName);
  root.restorePendingImages = false;
  root.restorePendingSource = false;
  root.runOutputsPending = false;
  root.folderPaths = folderPaths.length ? folderPaths : [selectedRootName];
  root.workspacePaths = [
    ...files.map(f => f.webkitRelativePath || (selectedRootName + "/" + f.name)),
    ...retainedWorkspacePaths,              // 이번에 못 읽었을 뿐 이전 작업공간에 있던 파일·폴더 마커
    ...root.folderPaths.map(workspaceFolderMarkerPath),
    ...(root.originalSaveMode
      ? [workspaceOriginalSaveMarkerPath(selectedRootName), workspaceSourceSkipMarkerPath(selectedRootName)] : [])
  ];
  const workspacePathsByFolder = indexWorkspacePathsByFolder(root.workspacePaths);
  root.newPythonContext = { parentId: root.nodeId, dir: selectedRootName, archiveCtx: folderCtx, label: selectedRootName };
  groupByPath.forEach((node, path) => {
    node.workspacePaths = workspacePathsByFolder.get(path) || [];
    if (node.newPythonContext) node.newPythonContext.archiveCtx = folderCtx;
    else node.newPythonContext = { parentId: node.nodeId, dir: path, archiveCtx: folderCtx, label: node.name };
  });
  keptDocs.forEach(doc => {
    if (!doc.archiveCtx || !doc.archiveCtx.isFolderContext) return;
    doc.archiveCtx = folderCtx;
    // 새 묶음은 디스크 스냅샷으로만 만들어지므로, 아직 저장하지 않은 새 문서는 여기서 다시 등록한다
    // (등록이 끊기면 동기화 한 번으로 같은 폴더의 다른 코드가 이 파일을 import 하지 못한다).
    if (((doc.isScratch && !doc._named) || unreadable(docKeyOf(doc)))
        && doc.sourceFile && typeof folderCtx.add === "function")
      folderCtx.add(docKeyOf(doc), doc.sourceFile);
  });

  // 탭·활성·분할 참조 복원(유지된 문서는 id 그대로, 다시 열린 문서는 경로로 연결)
  const nextBranchIds = navBranchIds(rootId);
  const resolveRef = (ref) => {
    if (!ref) return null;
    if (ref.id != null) return docs.some(doc => doc.id === ref.id) ? ref.id : null;
    if (!ref.path) return null;
    const match = docs.find(doc => nextBranchIds.has(doc.nodeId) && docKeyOf(doc) === ref.path);
    return match ? match.id : null;
  };
  const restoredTabs = [], seenTabs = new Set();
  tabRefs.forEach(ref => {
    const id = resolveRef(ref);
    if (id != null && !seenTabs.has(id)){ seenTabs.add(id); restoredTabs.push(id); }
  });
  tabOrder = restoredTabs;
  activeMru = mruRefs.map(resolveRef).filter((id, index, rows) => id != null && rows.indexOf(id) === index);
  const nextStudyId = resolveRef(studyRef);
  studyPdfId = nextStudyId != null && docs.some(doc => doc.id === nextStudyId) ? nextStudyId : null;
  const nextDocs = docs.filter(doc => nextBranchIds.has(doc.nodeId));
  const wantedActive = resolveRef(activeRef);
  const fallbackActive = wantedActive || restoredTabs[0] || (nextDocs[0] && nextDocs[0].id) || (docs[0] && docs[0].id) || 0;
  sidebarCursorKey = root.nodeId;
  if (fallbackActive){
    setActiveDoc(fallbackActive);
    uiBatchActiveCandidate = fallbackActive;
  }
  else {
    activeId = 0; state = null; viewer = null;
    refreshChrome(); applyStudyLayout(); renderSidebar();
  }
  // 폴더 새로고침으로 참고 문서가 새 인스턴스로 교체되면 활성 문서가 아니어서 지연 렌더가 생략된다.
  // 분할 화면에 보일 문서는 형식과 무관하게 렌더하고, PDF에만 추가 페이지 렌더·폭 맞춤을 적용한다.
  const refreshedStudyReference = docs.find(doc => doc.id === studyPdfId);
  if (refreshedStudyReference && refreshedStudyReference.id !== activeId){
    ensureRendered(refreshedStudyReference).then(() => {
      if (refreshedStudyReference.id === studyPdfId && refreshedStudyReference.kind === "pdf"){
        startLazyRender(refreshedStudyReference);
        requestAnimationFrame(() => fitStudyPdf(refreshedStudyReference));
      }
    });
  }

  if (files.length || folderPaths.length)
    await rememberWorkspace(files, false, { silent: true, folderPaths,
      originalSaveFolderPaths:root.originalSaveMode ? [selectedRootName] : [] });
  const nextPathSet = new Set(files.map(file => normalizedRunPath(file.webkitRelativePath || (selectedRootName + "/" + file.name))));
  retainedWorkspacePaths.forEach(path => nextPathSet.add(path));
  folderPaths.forEach(path => nextPathSet.add(workspaceFolderMarkerPath(path)));
  if (root.originalSaveMode){
    nextPathSet.add(workspaceOriginalSaveMarkerPath(selectedRootName));
    nextPathSet.add(workspaceSourceSkipMarkerPath(selectedRootName));   // 표식 유지는 rememberWorkspace 가 정한다
  }
  const deleted = oldPaths.filter(path => path && !nextPathSet.has(path));
  if (deleted.length) forgetWorkspacePaths(deleted);
  const changeSummary = [];
  if (addFiles.length - changedCount > 0) changeSummary.push("추가 " + (addFiles.length - changedCount) + "개");
  if (changedCount) changeSummary.push("변경 " + changedCount + "개");
  if (removedCount) changeSummary.push("제거 " + removedCount + "개");
  toast("폴더를 동기화했어요. " + (changeSummary.length ? changeSummary.join(" · ") : "변경된 파일 없음") + " (전체 " + nextDocs.length + "개)", 3000);
  return true;
}

function readEntryFile(entry){
  return new Promise((resolve, reject) => entry.file(resolve, reject));
}

function readDirectoryEntries(reader){
  return new Promise((resolve, reject) => reader.readEntries(resolve, reject));
}

async function readAllDirectoryEntries(entry){
  const reader = entry.createReader();
  const all = [];
  for (;;){
    const batch = await readDirectoryEntries(reader);
    if (!batch.length) break;
    all.push(...batch);
  }
  return all;
}

async function handleEntry(entry, parentId=null){
  if (!entry) return;
  if (entry.isDirectory){
    const group = makeGroup("folder", entry.name, parentId);
    const entries = await readAllDirectoryEntries(entry);
    entries.sort((a, b) => Number(b.isDirectory) - Number(a.isDirectory) || a.name.localeCompare(b.name));
    for (const child of entries) await handleEntry(child, group.nodeId);
    if (!navNodes.some(n => n.parentId === group.nodeId)) await closeGroup(group.nodeId);
    return;
  }
  if (entry.isFile){
    let file;
    try { file = await readEntryFile(entry); }
    catch (e){ console.warn("파일 엔트리를 읽지 못해 건너뜀:", entry.name, e); return; }
    await handleFiles([file], { parentId, bulk: true });   // 폴더=묶음 열기: 첫 개만 즉시 렌더, 나머지는 지연(빈 화면·렌더 폭주 방지)
  }
}

async function handleDroppedItems(entries){
  let used = false;
  for (const entry of entries){
    if (!entry) continue;
    used = true;
    await handleEntry(entry, null);
  }
  return used;
}

// 폴더 드롭도 폴더 선택과 같은 File[] 형태로 모아 저장·옆파일 실행·자동복원을 모두 지원한다.
async function collectDroppedFiles(entry, prefix, out, folderPaths, onProgress=null){
  if (!entry) return;
  const rel = prefix ? prefix + "/" + entry.name : entry.name;
  if (entry.isDirectory){
    if ((entry.name || "").charAt(0) === ".") return;
    if (folderPaths) folderPaths.push(rel);
    if (onProgress) onProgress({ phase:"scanning", files:out.length, folders:folderPaths.length });
    const children = await readAllDirectoryEntries(entry);
    // 파일 엔트리는 항목당 왕복이 있어 폴더 단위로 제한 병렬 읽기(순서 보존), 하위 폴더는 순차 순회.
    const fileChildren = children.filter(child => child && child.isFile);
    const read = await mapWithConcurrency(fileChildren, 8, async (child) => {
      try {
        const file = await readEntryFile(child);
        Object.defineProperty(file, "webkitRelativePath", { value: rel + "/" + child.name });
        return file;
      } catch(e){ console.warn("파일 엔트리를 읽지 못해 건너뜀:", child.name, e); return null; }
    });
    read.forEach(file => { if (file) out.push(file); });
    if (onProgress) onProgress({ phase:"scanning", files:out.length, folders:folderPaths.length });
    for (const child of children){
      if (child && child.isDirectory) await collectDroppedFiles(child, rel, out, folderPaths, onProgress);
    }
  } else if (entry.isFile){
    try {
      const file = await readEntryFile(entry);
      Object.defineProperty(file, "webkitRelativePath", { value: rel });
      out.push(file);
      if (onProgress) onProgress({ phase:"scanning", files:out.length, folders:folderPaths.length });
    } catch(e){ console.warn("파일 엔트리를 읽지 못해 건너뜀:", entry.name, e); }
  }
}

function queueDroppedItems(dataTransfer){
  const captured = captureDroppedFileItems(dataTransfer);
  const files = captured.files;
  const entries = topLevelDroppedEntries(captured.entries);
  const handlePromises = captured.handlePromises;
  // 엔트리는 드롭 이벤트가 끝나기 전에 동기적으로 확보해야 한다(이후 item 무효화).
  // 폴더 드롭이 아니면 신뢰할 수 있는 dataTransfer.files 를 그대로 쓴다.
  // file:// 로 열면 FileSystemFileEntry.file() 이 EncodingError 로 깨지는 Chrome 버그가 있어,
  // 일반 파일은 엔트리 API 를 거치지 않는다(폴더 구조 파악이 필요할 때만 엔트리 순회).
  const hasLegacyDir = entries.some(entry => entry.isDirectory);
  if (!hasLegacyDir && !handlePromises.length){
    if (droppedTransferNeedsFolderPicker(dataTransfer, files)){
      toast("이 브라우저는 드롭한 폴더 내용을 직접 주지 않아요. 같은 폴더를 선택해 주세요.", 4500);
      const folderInput = byId("folderInput");
      if (folderInput) pickFolderOrInput(folderInput);
      return fileQueue;
    }
    if (!files.length){
      toast("브라우저가 드롭한 폴더 정보를 전달하지 않았어요.", 5200, { type:"error",
        action:{ label:"폴더 열기", onClick:() => pickFolderOrInput(byId("folderInput")) } });
      return fileQueue;
    }
    return queueFiles(files);
  }
  let deferredWorkspaceSave = null;
  let keepOpenedGroupExpanded = false;
  fileQueue = fileQueue
    .then(() => runUiBatch(async () => {
      showLoading("폴더 파일 확인 중…");
      await yieldToBrowser();
      const collected = [];
      const folderPaths = [];
      const directoryHandles = [];
      const showScanProgress = progress => updateLoading(folderScanLoadingText(progress));

      // 최신 Chromium은 기존 엔트리 대신 File System Access 핸들만 줄 수 있다.
      // 핸들 함수는 드롭 이벤트 안에서 이미 호출했으며 여기서는 결과만 기다린다.
      const resolvedHandles = handlePromises.length ? await Promise.all(handlePromises) : [];
      const handles = await topLevelDroppedHandles(resolvedHandles);
      const modernHasDir = handles.some(handle => handle && handle.kind === "directory");
      let hasDir = modernHasDir || hasLegacyDir;
      if (!modernHasDir && hasLegacyDir){
        for (const entry of entries) await collectDroppedFiles(entry, "", collected, folderPaths, showScanProgress);
      }
      for (const handle of handles){
        if (!handle) continue;
        if (handle.kind === "directory"){
          // 같은 폴더/하위 폴더를 다시 드롭하면 새 트리(중복 문서·저장 충돌) 대신 기존 트리를 새로고침한다.
          // 여기는 이미 fileQueue 배치 안이므로 queueFolderRefresh(큐 재진입=교착)가 아니라 직접 실행한다.
          const related = await classifyRelatedFolderRoots(handle);
          if (related.same || related.child){
            const root = related.same || related.child;
            if (related.same) root.folderHandle = handle;   // 드롭 핸들이 최신 권한을 갖는다
            let allowed = false;
            try { allowed = await ensureReadPermission(root.folderHandle); } catch(_){}
            if (!allowed){
              // 안내만 하고 끝내면 사용자가 사이드바에서 그 버튼을 직접 찾아야 한다 — 여기서 바로 누르게 한다.
              toast("'" + root.name + "' 폴더를 다시 읽을 권한이 없어요.", 5200, { type:"error",
                action:{ label:"동기화", onClick:() => requestFolderRefresh(root.nodeId) } });
              continue;
            }
            toast(related.same
              ? "'" + root.name + "' 폴더는 이미 열려 있어요. 새로 여는 대신 동기화합니다."
              : "드롭한 폴더는 이미 열린 '" + root.name + "' 안에 있어요. '" + root.name + "'을(를) 동기화합니다.", 3400);
            const snapshot = await collectDirectoryHandleFiles(root.folderHandle, { onProgress:showScanProgress,
              reuseFiles: folderSnapshotReuseMap(root.nodeId) });
            reportSkippedFolderEntries(snapshot.skipped);
            await refreshFolderGroup(root.nodeId, snapshot.files, { folderHandle: root.folderHandle,
              folderPaths: snapshot.folderPaths, skipped: snapshot.skipped,
              originalSaveMode: !!root.originalSaveMode });
            continue;
          }
          if (related.parents.length) absorbContainedFolderRoots(related.parents);
          directoryHandles.push(handle);
          const snapshot = await collectDirectoryHandleFiles(handle, { onProgress:showScanProgress });
          reportSkippedFolderEntries(snapshot.skipped);
          collected.push(...snapshot.files);
          folderPaths.push(...snapshot.folderPaths);
        } else if ((modernHasDir || !files.length) && handle.kind === "file" && typeof handle.getFile === "function"){
          try {
            const file = withFileHandle(await handle.getFile(), handle);
            setFileRelativePath(file, file.name);
            collected.push(file);
          } catch(e){ console.warn("드롭한 파일 핸들을 읽지 못했어요:", e); }
        }
      }

      if (!hasDir){
        const regularFiles = files.length ? files : collected;
        if (!regularFiles.length){
          toast("드롭한 폴더 정보를 읽지 못했어요.", 5200, { type:"error",
            action:{ label:"폴더 열기", onClick:() => pickFolderOrInput(byId("folderInput")) } });
          return;
        }
        let options = {};
        const loose = regularFiles.filter(file =>
          !["zip","tar","gz","tgz"].includes((file.name.split(".").pop() || "").toLowerCase()));
        if (loose.length > 1){
          options.archiveCtx = makeFileSiblingCtx(
            loose.map(file => ({ file, relPath:file.name })), "여러 파일");
        }
        const replaceWorkspace = docs.length === 0;
        await handleFiles(regularFiles, options);
        await rememberWorkspace(regularFiles, replaceWorkspace, { silent:true });
        return;
      }

      const uniqueFolderPaths = [...new Set(folderPaths)];
      if (!uniqueFolderPaths.length){
        // 드롭한 폴더 전부가 기존 트리 새로고침으로 처리됨 — 남은 건 함께 드롭한 낱개 파일뿐이라 일반 파일로 연다.
        if (!collected.length) return;
        const loose = collected.filter(file =>
          !["zip","tar","gz","tgz"].includes((file.name.split(".").pop() || "").toLowerCase()));
        const looseOptions = loose.length > 1
          ? { archiveCtx: makeFileSiblingCtx(loose.map(file => ({ file, relPath:file.name })), "여러 파일") }
          : {};
        const replaceWorkspace = docs.length === 0;
        await handleFiles(collected, looseOptions);
        await rememberWorkspace(collected, replaceWorkspace, { silent:true });
        return;
      }
      // 핸들로 드롭한 폴더는 파일 핸들 덕에 실제로 원본에 저장되므로 원본 저장 마커도 함께 기록한다.
      // 마커 없이 저장하면 '폴더 열기'가 남긴 마커가 stale 정리에 지워져, 재시작 후 원본 저장이 조용히 풀렸다.
      const originalSaveFolderPaths = modernHasDir
        ? [...new Set(uniqueFolderPaths.map(path => normalizedRunPath(path).split("/")[0]).filter(Boolean))]
        : [];
      const replaceWorkspace = navNodes.length === 0;
      const rootGroup = await openFolderFiles(collected, {
        folderPaths:uniqueFolderPaths,
        folderHandle:directoryHandles.length === 1 ? directoryHandles[0] : null,
        originalSaveMode: modernHasDir,
        expandRoot:false
      });
      keepOpenedGroupExpanded = !!rootGroup && !autoOpenFirstFileEnabled();
      // 화면과 사이드바를 먼저 표시한 뒤 자동 복원용 바이트 복사를 수행한다.
      // 이 저장은 아래 후속 then 에서 UI 배치를 끝낸 다음 헤더 상태로 조용히 진행한다.
      if (rootGroup) deferredWorkspaceSave = {
        files:collected, replaceWorkspace, folderPaths:uniqueFolderPaths, originalSaveFolderPaths
      };
    }))
    .then(async () => {
      if (!keepOpenedGroupExpanded) collapseToActiveBranch(); // 자동 선택했다면 활성 파일의 분기만 남긴다
      if (!deferredWorkspaceSave) return;
      await yieldToBrowser();               // 활성 문서가 실제로 한 프레임 그려진 뒤 저장 복사를 시작한다
      const pending = deferredWorkspaceSave;
      deferredWorkspaceSave = null;
      await rememberWorkspace(pending.files, pending.replaceWorkspace, {
        silent:true, folderPaths:pending.folderPaths, originalSaveFolderPaths:pending.originalSaveFolderPaths
      });
    })
    .catch((e) => {
      if (e && e.message === "operation-cancelled") toast("폴더 열기를 취소했어요.");
      else {
        console.error(e);
        const reason = e && (e.name || e.message) ? " (" + (e.name || e.message) + ")" : "";
        toast("드롭한 폴더를 읽지 못했어요" + reason, 5600, { type:"error",
          action:{ label:"폴더 열기", onClick:() => pickFolderOrInput(byId("folderInput")) } });
      }
    });
  return fileQueue;
}
