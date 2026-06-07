/*
 * office-decrypt.js — ECMA-376 Agile 암호화(.xlsx/.docx/.pptx 열기 암호) 복호화
 * 의존: 전역 CryptoJS, 그리고 CFB(= 전역 XLSX.CFB 또는 전역 CFB)
 * 브라우저와 Node 양쪽에서 동일하게 동작하도록 작성(테스트 가능).
 * 노출: window.OfficeDecrypt = { decrypt(bytesU8, password) -> Uint8Array, isEncrypted(bytesU8) }
 */
(function (global) {
  "use strict";
  var C = global.CryptoJS;

  function getCFB() { return (global.XLSX && global.XLSX.CFB) || global.CFB; }
  function toU8(c) { return c instanceof Uint8Array ? c : new Uint8Array(c); }

  // Uint8Array <-> CryptoJS WordArray
  function u8ToWA(u8) {
    var words = [], i;
    for (i = 0; i < u8.length; i++) words[i >>> 2] |= u8[i] << (24 - (i % 4) * 8);
    return C.lib.WordArray.create(words, u8.length);
  }
  function waToU8(wa) {
    var u8 = new Uint8Array(wa.sigBytes), i;
    for (i = 0; i < wa.sigBytes; i++) u8[i] = (wa.words[i >>> 2] >>> (24 - (i % 4) * 8)) & 0xff;
    return u8;
  }
  function concatWA(a, b) { return a.clone().concat(b); }
  function le32(n) { return u8ToWA(new Uint8Array([n & 0xff, (n >>> 8) & 0xff, (n >>> 16) & 0xff, (n >>> 24) & 0xff])); }
  function utf16le(str) {
    var u8 = new Uint8Array(str.length * 2), i, c;
    for (i = 0; i < str.length; i++) { c = str.charCodeAt(i); u8[i * 2] = c & 0xff; u8[i * 2 + 1] = (c >>> 8) & 0xff; }
    return u8ToWA(u8);
  }
  function b64(s) { return C.enc.Base64.parse(s); }
  function hashFn(name) {
    name = (name || "SHA512").toUpperCase().replace(/-/g, "");
    return ({ SHA1: C.SHA1, SHA256: C.SHA256, SHA384: C.SHA384, SHA512: C.SHA512 })[name] || C.SHA512;
  }
  function aesDec(cipherWA, keyWA, ivWA) {
    return C.AES.decrypt({ ciphertext: cipherWA }, keyWA, { iv: ivWA, mode: C.mode.CBC, padding: C.pad.NoPadding });
  }
  // 길이 맞추기: 길면 자르고, 짧으면 0x36 으로 채움(스펙)
  function truncPad(wa, len) {
    var u8 = waToU8(wa), out = new Uint8Array(len);
    out.fill(0x36);
    out.set(u8.subarray(0, Math.min(len, u8.length)));
    return u8ToWA(out);
  }
  // <tag .../> 의 속성 추출(네임스페이스 접두사 무시)
  function getAttrs(xml, tag) {
    var m = xml.match(new RegExp("<([a-zA-Z0-9]+:)?" + tag + "\\b([^>]*)>"));
    if (!m) return null;
    var attrs = {}, re = /([a-zA-Z0-9]+)\s*=\s*"([^"]*)"/g, am;
    while ((am = re.exec(m[2]))) attrs[am[1]] = am[2];
    return attrs;
  }

  function decryptAgile(encInfoU8, encPkgU8, password) {
    var xml = new TextDecoder("utf-8").decode(encInfoU8.subarray(8));
    var kd = getAttrs(xml, "keyData");
    var ek = getAttrs(xml, "encryptedKey");
    if (!kd || !ek) throw new Error("not-agile");

    var blockSize = +kd.blockSize || 16;
    var ksalt = b64(kd.saltValue);
    var kdHash = hashFn(kd.hashAlgorithm);
    var psalt = b64(ek.saltValue);
    var spin = +ek.spinCount;
    var peKeyBytes = (+ek.keyBits) / 8;
    var ekHash = hashFn(ek.hashAlgorithm);
    var hashSize = +ek.hashSize || 64;

    // 비밀번호 해시: H0 = hash(salt + UTF16LE(pw)); Hi+1 = hash(LE32(i) + Hi)
    var H = ekHash(concatWA(psalt, utf16le(password)));
    for (var i = 0; i < spin; i++) H = ekHash(concatWA(le32(i), H));

    function deriveKey(block) { return truncPad(ekHash(concatWA(H, u8ToWA(new Uint8Array(block)))), peKeyBytes); }
    var iv = truncPad(psalt, blockSize);

    // 암호 검증 (틀리면 throw)
    var vIn = waToU8(aesDec(b64(ek.encryptedVerifierHashInput),
      deriveKey([0xfe, 0xa7, 0xd2, 0x76, 0x3b, 0x4b, 0x9e, 0x79]), iv));
    var vHash = waToU8(aesDec(b64(ek.encryptedVerifierHashValue),
      deriveKey([0xd7, 0xaa, 0x0f, 0x6d, 0x30, 0x61, 0x34, 0x4e]), iv));
    var calc = waToU8(ekHash(u8ToWA(vIn)));
    for (var k = 0; k < hashSize; k++) if (calc[k] !== vHash[k]) throw new Error("wrong-password");

    // 패키지 비밀키
    var secret = aesDec(b64(ek.encryptedKeyValue),
      deriveKey([0x14, 0x6e, 0x0b, 0xe7, 0xab, 0xac, 0xd0, 0xd6]), iv);

    // EncryptedPackage: 앞 8바이트 = 평문 전체 길이(LE64), 이후 4096 세그먼트 단위
    var total = 0;
    for (var t = 7; t >= 0; t--) total = total * 256 + encPkgU8[t];
    var data = encPkgU8.subarray(8);
    var out = new Uint8Array(Math.ceil(data.length / 4096) * 4096);
    var seg = 0;
    for (var off = 0; off < data.length; off += 4096, seg++) {
      var chunk = data.subarray(off, Math.min(off + 4096, data.length));
      var ivSeg = truncPad(kdHash(concatWA(ksalt, le32(seg))), blockSize);
      out.set(waToU8(aesDec(u8ToWA(chunk), secret, ivSeg)), off);
    }
    return out.subarray(0, total);
  }

  function readStreams(bytesU8) {
    var CFB = getCFB();
    if (!CFB) throw new Error("no-cfb");
    var cfb = CFB.read(bytesU8, { type: "array" });
    var info = CFB.find(cfb, "/EncryptionInfo") || CFB.find(cfb, "EncryptionInfo");
    var pkg = CFB.find(cfb, "/EncryptedPackage") || CFB.find(cfb, "EncryptedPackage");
    return { info: info, pkg: pkg };
  }

  global.OfficeDecrypt = {
    isEncrypted: function (bytesU8) {
      try { var s = readStreams(toU8(bytesU8)); return !!(s.info && s.pkg); } catch (e) { return false; }
    },
    decrypt: function (bytesU8, password) {
      var s = readStreams(toU8(bytesU8));
      if (!s.info || !s.pkg) throw new Error("not-encrypted");
      var infoU8 = toU8(s.info.content);
      var major = infoU8[0] | (infoU8[1] << 8);
      var minor = infoU8[2] | (infoU8[3] << 8);
      if (!(major === 4 && minor === 4)) throw new Error("unsupported-encryption"); // standard/extensible 등 미지원
      return decryptAgile(infoU8, toU8(s.pkg.content), password);
    }
  };
})(typeof globalThis !== "undefined" ? globalThis : this);
