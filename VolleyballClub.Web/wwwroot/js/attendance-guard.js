// 출석 관리 페이지 — 미저장 변경이 있을 때 새로고침·탭 닫기를 막는 가드.
// 내비게이션(링크 클릭)은 Blazor NavigationManager location-changing에서 별도 처리.
export function enable() {
	window.onbeforeunload = preventUnload;
}
export function disable() {
	delete window.onbeforeunload;
}
function preventUnload(e) {
	e.preventDefault();
	e.returnValue = "";
}
