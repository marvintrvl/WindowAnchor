const HOST_NAME = "com.windowanchor.browser";
const PROTOCOL_VERSION = 1;
let nativePort = null;
let reconnectTimer = null;
let hostUnavailable = false;

function connectHost() {
  if (nativePort || hostUnavailable) return;
  try {
    nativePort = chrome.runtime.connectNative(HOST_NAME);
    nativePort.onMessage.addListener(handleMessage);
    nativePort.onDisconnect.addListener(() => {
      // Reading lastError prevents Chrome from reporting an unchecked runtime error
      // when the user has not installed or registered the native host yet.
      const error = chrome.runtime.lastError;
      nativePort = null;
      if (error) {
        hostUnavailable = true;
        console.info("WindowAnchor browser integration is not enabled: " + error.message);
        return;
      }
      hostUnavailable = false;
    });
  } catch (error) {
    hostUnavailable = true;
    console.warn("WindowAnchor native host unavailable", error);
  }
}

async function captureSessions(selectedTitles) {
  selectedTitles = Array.isArray(selectedTitles)
    ? selectedTitles.filter(title => typeof title === "string" && title.length > 0)
    : [];
  const windows = await chrome.windows.getAll({
    populate: true,
    windowTypes: ["normal"]
  });
  const sessions = [];
  for (let windowIndex = 0; windowIndex < windows.length; windowIndex += 1) {
    const browserWindow = windows[windowIndex];
    if (browserWindow.incognito) continue;
    const groups = await chrome.tabGroups.query({ windowId: browserWindow.id });
    const groupIndexById = new Map(groups.map((group, index) => [group.id, index]));
    const tabs = (browserWindow.tabs || [])
      .filter(tab => !tab.incognito && isRestorableUrl(tab.url))
      .map(tab => ({
        url: tab.url,
        title: tab.title || "",
        index: tab.index,
        active: Boolean(tab.active),
        pinned: Boolean(tab.pinned),
        groupIndex: tab.groupId === chrome.tabGroups.TAB_GROUP_ID_NONE
          ? -1 : groupIndexById.get(tab.groupId) ?? -1
      }));
    if (tabs.length === 0) continue;
    const activeTitle = tabs.find(tab => tab.active)?.title || "";
    if (selectedTitles.length === 0 ||
        !selectedTitles.some(title => activeTitle.includes(title) || title.includes(activeTitle)))
      continue;
    sessions.push({
      browser: browserName(),
      activeTitle,
      windowIndex,
      left: browserWindow.left ?? 0,
      top: browserWindow.top ?? 0,
      width: browserWindow.width ?? 1200,
      height: browserWindow.height ?? 800,
      state: browserWindow.state === "normal" ? "normal" : browserWindow.state,
      tabs,
      groups: groups.map((group, index) => ({
        index,
        title: group.title || "",
        color: group.color,
        collapsed: Boolean(group.collapsed)
      }))
    });
  }
  return sessions;
}

function isRestorableUrl(url) {
  return typeof url === "string" && (url.startsWith("http://") ||
    url.startsWith("https://") || url.startsWith("file://"));
}

function browserName() {
  const userAgent = navigator.userAgent;
  if (userAgent.includes("Edg/")) return "edge";
  if (userAgent.includes("OPR/")) return "opera";
  if (userAgent.includes("Brave")) return "brave";
  return "chrome";
}

async function restoreSessions(sessions) {
  const results = [];
  for (const session of sessions || []) {
    const tabs = (session.tabs || []).sort((a, b) => a.index - b.index);
    const urls = tabs.map(tab => tab.url).filter(isRestorableUrl);
    if (urls.length === 0) continue;
    try {
      const created = await chrome.windows.create({
        url: urls,
        left: session.left,
        top: session.top,
        width: session.width,
        height: session.height,
        focused: false,
        state: session.state === "normal" ? "normal" : undefined
      });
      const createdTabs = created.tabs || [];
      for (let index = 0; index < Math.min(createdTabs.length, tabs.length); index += 1) {
        await chrome.tabs.update(createdTabs[index].id, {
          pinned: Boolean(tabs[index].pinned),
          active: Boolean(tabs[index].active)
        });
      }
      for (const group of session.groups || []) {
        const tabIds = tabs
          .map((tab, index) => tab.groupIndex === group.index ? createdTabs[index]?.id : null)
          .filter(id => Number.isInteger(id));
        if (tabIds.length === 0) continue;
        const groupId = await chrome.tabs.group({ tabIds });
        await chrome.tabGroups.update(groupId, {
          title: group.title || undefined,
          color: group.color || "grey",
          collapsed: Boolean(group.collapsed)
        });
      }
      if (session.state && session.state !== "normal")
        await chrome.windows.update(created.id, { state: session.state });
      results.push({ ok: true, tabs: urls.length });
    } catch (error) {
      results.push({ ok: false, error: String(error) });
    }
  }
  return results;
}

function sendResponse(requestId, payload) {
  nativePort?.postMessage({
    type: "response",
    requestId,
    protocolVersion: PROTOCOL_VERSION,
    ...payload
  });
}

async function handleMessage(message) {
  if (!message || message.protocolVersion > PROTOCOL_VERSION) return;
  try {
    if (message.type === "capture") {
      sendResponse(message.requestId, {
        ok: true,
        sessions: await captureSessions(message.selectedBrowserTitles || [])
      });
    } else if (message.type === "restore") {
      const results = await restoreSessions(message.sessions);
      sendResponse(message.requestId, {
        ok: results.every(result => result.ok),
        results
      });
    } else if (message.type === "ping") {
      sendResponse(message.requestId, { ok: true });
    }
  } catch (error) {
    sendResponse(message.requestId, { ok: false, error: String(error), sessions: [] });
  }
}

connectHost();
