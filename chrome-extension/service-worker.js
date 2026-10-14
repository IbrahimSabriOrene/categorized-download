const downloadableExtensions = /\.(?:pdf|xlsx?|csv|ods|docx?|pptx?|txt|jpe?g|png|gif|webp|svg|mp4|mkv|mov|avi|webm|mp3|wav|flac|m4a|ogg|zip|rar|7z|tar|gz|exe|msi|msix|dmg|apk)$/i;

function fileNameFromUrl(url) {
  try {
    return decodeURIComponent(new URL(url).pathname.split("/").pop() || "");
  } catch {
    return "";
  }
}

function isDownloadableNavigation(url) {
  return downloadableExtensions.test(fileNameFromUrl(url));
}

async function handOff(url, filename) {
  const response = await fetch("http://127.0.0.1:17852/download", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-CategorizedDownload-Key": "c794607f-9571-41ea-9f83-383f75ee2ff6"
    },
    body: JSON.stringify({ url, filename })
  });

  if (!response.ok) throw new Error(`Desktop bridge returned HTTP ${response.status}.`);
  return response.json();
}

async function resumeIfPossible(id) {
  const [item] = await chrome.downloads.search({ id });
  if (item?.canResume) await chrome.downloads.resume(id);
}

if (globalThis.chrome?.downloads) {
  chrome.downloads.onCreated.addListener(async item => {
    if (!item.url.startsWith("http://") && !item.url.startsWith("https://")) return;

    try {
      await chrome.downloads.pause(item.id).catch(() => {});
      const response = await handOff(item.finalUrl || item.url, item.filename);

      if (response?.ok) {
        const [current] = await chrome.downloads.search({ id: item.id });
        if (current?.state === "in_progress") await chrome.downloads.cancel(item.id);
        await chrome.downloads.erase({ id: item.id });
      } else if (response?.blocked) {
        const [current] = await chrome.downloads.search({ id: item.id });
        if (current?.state === "in_progress") await chrome.downloads.cancel(item.id);
        await chrome.downloads.erase({ id: item.id });
      } else {
        await resumeIfPossible(item.id);
        console.error(response?.error || "Desktop app did not accept the download.");
      }
    } catch (error) {
      await resumeIfPossible(item.id).catch(() => {});
      console.error("Categorized Download bridge failed:", error);
    }
  });

  chrome.webNavigation.onBeforeNavigate.addListener(async details => {
    if (details.frameId !== 0 || !isDownloadableNavigation(details.url)) return;

    try {
      await handOff(details.url, fileNameFromUrl(details.url));
    } catch (error) {
      console.error("Categorized Download navigation handoff failed:", error);
    }
  });
}

if (globalThis.process?.argv.includes("--self-test")) {
  console.assert(isDownloadableNavigation("https://example.test/report.PDF?token=1"));
  console.assert(!isDownloadableNavigation("https://example.test/report"));
  console.assert(fileNameFromUrl("https://example.test/files/budget.xlsx") === "budget.xlsx");
}
