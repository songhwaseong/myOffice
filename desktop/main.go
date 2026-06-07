// PDF 서명 & 편집기 - 로컬 서버 런처
//
// 오프라인 HTML(app.html)을 바이너리에 내장하여,
// 실행 시 로컬(127.0.0.1)에 작은 웹서버를 띄우고 기본 브라우저를 자동으로 연다.
// 인터넷 연결이나 별도 설치 없이 .exe 더블클릭만으로 동작한다.
package main

import (
	"embed"
	"fmt"
	"io/fs"
	"log"
	"net"
	"net/http"
	"os/exec"
	"runtime"
	"time"
)

//go:embed app.html
var content embed.FS

// 기본 브라우저로 url 열기 (OS별 처리)
func openBrowser(url string) {
	var err error
	switch runtime.GOOS {
	case "windows":
		err = exec.Command("rundll32", "url.dll,FileProtocolHandler", url).Start()
	case "darwin":
		err = exec.Command("open", url).Start()
	default:
		err = exec.Command("xdg-open", url).Start()
	}
	if err != nil {
		log.Println("브라우저 자동 실행 실패:", err)
	}
}

func main() {
	page, err := fs.ReadFile(content, "app.html")
	if err != nil {
		log.Fatal("내장 페이지를 읽지 못했습니다:", err)
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/" {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.Write(page)
	})
	// 헬스체크/종료 신호 등 확장 여지
	mux.HandleFunc("/ping", func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte("ok"))
	})

	// 127.0.0.1 의 빈 포트에 바인딩 (외부에는 노출되지 않음)
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		log.Fatal("포트 바인딩 실패:", err)
	}
	port := ln.Addr().(*net.TCPAddr).Port
	url := fmt.Sprintf("http://127.0.0.1:%d", port)

	fmt.Println("============================================")
	fmt.Println("  PDF 서명 & 편집기 실행 중")
	fmt.Println("============================================")
	fmt.Println("  주소 :", url)
	fmt.Println("  브라우저가 자동으로 열립니다.")
	fmt.Println("  종료하려면 이 창을 닫으세요. (Ctrl+C)")
	fmt.Println("============================================")

	// 서버가 뜬 직후 브라우저 열기
	go func() {
		time.Sleep(400 * time.Millisecond)
		openBrowser(url)
	}()

	if err := http.Serve(ln, mux); err != nil {
		log.Fatal(err)
	}
}
