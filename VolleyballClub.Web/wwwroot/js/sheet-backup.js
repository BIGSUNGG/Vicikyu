export function show(dialog, ref) {
	if (dialog && !dialog.open) {
		if (ref) {
			const notify = () => ref.invokeMethodAsync("OnDialogClosedAsync");
			dialog.addEventListener("close", notify, { once: true });
		}
		dialog.showModal();
	}
}

export function close(dialog) {
	if (dialog?.open) dialog.close();
}

export async function download() {
	const response = await fetch("/api/admin/database/export", {
		credentials: "same-origin",
		cache: "no-store",
	});
	if (!response.ok)
		throw new Error(`Backup download failed (${response.status})`);
	const blob = await response.blob();
	const url = URL.createObjectURL(blob);
	const link = document.createElement("a");
	link.href = url;
	link.download = `vicikyu-unsynced-${new Date().toISOString().replace(/[:.]/g, "-")}.db`;
	document.body.append(link);
	link.click();
	link.remove();
	setTimeout(() => URL.revokeObjectURL(url), 1000);
}
