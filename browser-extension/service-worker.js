const HOST_NAME = "com.windowanchor.browser";
const PROTOCOL_VERSION = 2;
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
  const profileKey = await getProfileKey();
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
      profileKey,
      // Chromium does not expose a non-sensitive browser-profile display name.
      profileLabel: "",
      browserWindowId: String(browserWindow.id),
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

async function getProfileKey() {
  const stored = await chrome.storage.local.get("windowAnchorProfileKey");
  if (typeof stored.windowAnchorProfileKey === "string" && stored.windowAnchorProfileKey.length > 0)
    return stored.windowAnchorProfileKey;

  const profileKey = crypto.randomUUID();
  await chrome.storage.local.set({ windowAnchorProfileKey: profileKey });
  return profileKey;
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
  const profileKey = await getProfileKey();
  for (const session of sessions || []) {
    const tabs = (session.tabs || []).sort((a, b) => a.index - b.index);
    const urls = tabs.map(tab => tab.url).filter(isRestorableUrl);
    if (urls.length === 0) continue;

    if (typeof session.profileKey === "string" && session.profileKey.length > 0 &&
        session.profileKey !== profileKey) {
      results.push({ ok: true, status: "profileUnavailable", sessionId: session.browserSessionId || "" });
      continue;
    }

    const policy = restorePolicy(session.restorePolicy, Boolean(session.profileKey));
    const matchingTabs = policy === "OpenDuplicate"
      ? []
      : await findMatchingTabs(urls);
    if (policy === "Ask" && matchingTabs.length > 0) {
      results.push({ ok: true, status: "conflict", sessionId: session.browserSessionId || "" });
      continue;
    }

    const reusedByUrl = new Map(matchingTabs.map(tab => [tab.url, tab]));
    const missingTabs = tabs.filter(tab => !reusedByUrl.has(tab.url));
    if (matchingTabs.length > 0) {
      const active = matchingTabs.find(tab => tab.url === tabs.find(saved => saved.active)?.url) || matchingTabs[0];
      await chrome.tabs.update(active.id, { active: true });
      await chrome.windows.update(active.windowId, { focused: true });
    }
    if (missingTabs.length === 0) {
      results.push({ ok: true, status: "reused", sessionId: session.browserSessionId || "", tabs: matchingTabs.length });
      continue;
    }

    try {
      const created = await chrome.windows.create({
        url: missingTabs.map(tab => tab.url),
        left: session.left,
        top: session.top,
        width: session.width,
        height: session.height,
        focused: false,
        state: session.state === "normal" ? "normal" : undefined
      });
      const createdTabs = await chrome.tabs.query({ windowId: created.id });
      for (let index = 0; index < Math.min(createdTabs.length, missingTabs.length); index += 1) {
        await chrome.tabs.update(createdTabs[index].id, {
          pinned: Boolean(missingTabs[index].pinned),
          active: Boolean(missingTabs[index].active)
        });
      }
      for (const group of session.groups || []) {
        const tabIds = missingTabs
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
      results.push({ ok: true, status: matchingTabs.length > 0 ? "partiallyReused" : "opened", sessionId: session.browserSessionId || "", tabs: missingTabs.length });
    } catch (error) {
      results.push({ ok: false, sessionId: session.browserSessionId || "", error: String(error) });
    }
  }
  return results;
}

function restorePolicy(policy, hasProfileIdentity) {
  if (!hasProfileIdentity) return "OpenDuplicate";
  if (policy === "ReuseMatchingTab" || policy === 0) return "ReuseMatchingTab";
  if (policy === "OpenDuplicate" || policy === 1) return "OpenDuplicate";
  if (policy === "Ask" || policy === 2) return "Ask";
  return hasProfileIdentity ? "ReuseMatchingTab" : "OpenDuplicate";
}

async function findMatchingTabs(urls) {
  const wanted = new Set(urls.filter(url => !url.startsWith("file://")));
  if (wanted.size === 0) return [];
  const windows = await chrome.windows.getAll({
    populate: true,
    windowTypes: ["normal"]
  });
  return windows.flatMap(browserWindow => browserWindow.tabs || [])
    .filter(tab => !tab.incognito && typeof tab.url === "string" && wanted.has(tab.url));
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
