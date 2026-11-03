// 서버가 준 xlsx 바이트를 브라우저 저장 대화상자로 내려준다(인증 헤더 때문에 직접 링크 대신 JS로 저장).
export function saveAs(fileName, bytes) {
	const buffer = new Uint8Array(bytes);
	const blob = new Blob([buffer], {
		type: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
	});
	const url = URL.createObjectURL(blob);
	const anchor = document.createElement("a");
	anchor.href = url;
	anchor.download = fileName;
	document.body.appendChild(anchor);
	anchor.click();
	anchor.remove();
	URL.revokeObjectURL(url);
}
