//go:build windows

package main

import "syscall"

// Windows 콘솔 출력 코드페이지를 UTF-8(65001)로 설정해 한글이 깨지지 않도록 한다.
func init() {
	kernel32 := syscall.NewLazyDLL("kernel32.dll")
	setCP := kernel32.NewProc("SetConsoleOutputCP")
	setCP.Call(uintptr(65001))
}
