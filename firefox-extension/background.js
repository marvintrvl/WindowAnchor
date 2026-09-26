const HOST_NAME = "com.windowanchor.browser";
const PROTOCOL_VERSION = 2;
let nativePort = null;
let hostUnavailable = false;

function connectHost() {
  if (nativePort || hostUnavailable) return;
  try {
    nativePort = browser.runtime.connectNative(HOST_NAME);
    nativePort.onMessage.addListener(handleMessage);
    nativePort.onDisconnect.addListener(port => {
      const error = port.error;
      nativePort = null;
      if (error) {
        hostUnavailable = true;
        console.info("WindowAnchor browser integration is not enabled: " + error.message);
      }
    });
  } catch (error) {
    hostUnavailable = true;
    console.warn("WindowAnchor native host unavailable", error);
  }
}

async function captureSessions(selectedTitles) {
  const titles = Array.isArray(selectedTitles)
    ? selectedTitles
      .filter(title => typeof title === "string" && title.length > 0)
      .map(title => title.toLocaleLowerCase())
    : [];
  const windows = await browser.windows.getAll({
    populate: true,
    windowTypes: ["normal"]
  });
  const profileKey = await getProfileKey();
  const sessions = [];

  for (let windowIndex = 0; windowIndex < windows.length; windowIndex += 1) {
    const browserWindow = windows[windowIndex];
    if (browserWindow.incognito) continue;

    const groups = await browser.tabGroups.query({ windowId: browserWindow.id });
    const groupIndexById = new Map(groups.map((group, index) => [group.id, index]));
    const tabs = (browserWindow.tabs || [])
      .filter(tab => !tab.incognito && isRestorableUrl(tab.url))
      .map(tab => ({
        url: tab.url,
        title: tab.title || "",
        index: tab.index,
        active: Boolean(tab.active),
        pinned: Boolean(tab.pinned),
        groupIndex: tab.groupId === browser.tabGroups.TAB_GROUP_ID_NONE
          ? -1
          : groupIndexById.get(tab.groupId) ?? -1
      }));
    if (tabs.length === 0) continue;

    const activeTitle = tabs.find(tab => tab.active)?.title || "";
    const comparableTitle = activeTitle.toLocaleLowerCase();
    if (titles.length > 0 &&
        !titles.some(title => comparableTitle.includes(title) || title.includes(comparableTitle)))
      continue;

    sessions.push({
      profileKey,
      profileLabel: "",
      browserWindowId: String(browserWindow.id),
      browser: "firefox",
      activeTitle,
      windowIndex,
      left: browserWindow.left ?? 0,
      top: browserWindow.top ?? 0,
      width: browserWindow.width ?? 1200,
      height: browserWindow.height ?? 800,
      state: normalizeWindowState(browserWindow.state),
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
  const stored = await browser.storage.local.get("windowAnchorProfileKey");
  if (typeof stored.windowAnchorProfileKey === "string" &&
      stored.windowAnchorProfileKey.length > 0)
    return stored.windowAnchorProfileKey;

  const profileKey = crypto.randomUUID();
  await browser.storage.local.set({ windowAnchorProfileKey: profileKey });
  return profileKey;
}

function isRestorableUrl(url) {
  return typeof url === "string" &&
    (url.startsWith("http://") || url.startsWith("https://") || url.startsWith("file://"));
}

function normalizeWindowState(state) {
  return ["normal", "minimized", "maximized", "fullscreen"].includes(state)
    ? state
    : "normal";
}

async function restoreSessions(sessions) {
  const results = [];
  const profileKey = await getProfileKey();
  for (const session of sessions || []) {
    const tabs = (session.tabs || [])
      .filter(tab => isRestorableUrl(tab.url))
      .sort((a, b) => a.index - b.index);
    if (tabs.length === 0) {
      results.push({
        ok: true,
        status: "skipped",
        sessionId: session.browserSessionId || "",
        tabs: 0
      });
      continue;
    }

    if (typeof session.profileKey === "string" && session.profileKey.length > 0 &&
        session.profileKey !== profileKey) {
      results.push({
        ok: true,
        status: "profileUnavailable",
        sessionId: session.browserSessionId || ""
      });
      continue;
    }

    const policy = restorePolicy(session.restorePolicy, Boolean(session.profileKey));
    const matchingTabs = policy === "OpenDuplicate"
      ? []
      : await findMatchingTabs(tabs.map(tab => tab.url));
    if (policy === "Ask" && matchingTabs.length > 0) {
      results.push({
        ok: true,
        status: "conflict",
        sessionId: session.browserSessionId || ""
      });
      continue;
    }

    const reuse = consumeMatchingTabs(tabs, matchingTabs);
    const missingTabs = reuse.missing;
    if (reuse.reused.length > 0) {
      const activeSavedUrl = tabs.find(tab => tab.active)?.url;
      const active = reuse.reused.find(pair => pair.saved.url === activeSavedUrl)?.live ||
        reuse.reused[0].live;
      await browser.tabs.update(active.id, { active: true });
      await browser.windows.update(active.windowId, { focused: true });
    }

    if (missingTabs.length === 0) {
      results.push({
        ok: true,
        status: "reused",
        sessionId: session.browserSessionId || "",
        tabs: reuse.reused.length
      });
      continue;
    }

    const opened = await openTabsInWindow(session, missingTabs);
    const openedCount = opened.pairs.length;
    const restoredCount = reuse.reused.length + openedCount;
    const ok = restoredCount > 0;
    results.push({
      ok,
      status: !ok
        ? "failed"
        : opened.failures.length > 0 || opened.groupFailures > 0
          ? "partiallyOpened"
          : reuse.reused.length > 0 ? "partiallyReused" : "opened",
      sessionId: session.browserSessionId || "",
      tabs: restoredCount,
      failedTabs: opened.failures.length,
      failedGroups: opened.groupFailures,
      error: ok ? undefined : opened.failures.map(failure => failure.error).join("; ")
    });
  }
  return results;
}

function restorePolicy(policy, hasProfileIdentity) {
  if (!hasProfileIdentity) return "OpenDuplicate";
  if (policy === "ReuseMatchingTab" || policy === 0) return "ReuseMatchingTab";
  if (policy === "OpenDuplicate" || policy === 1) return "OpenDuplicate";
  if (policy === "Ask" || policy === 2) return "Ask";
  return "ReuseMatchingTab";
}

async function findMatchingTabs(urls) {
  const wanted = new Set(urls.filter(url => !url.startsWith("file://")));
  if (wanted.size === 0) return [];
  const windows = await browser.windows.getAll({
    populate: true,
    windowTypes: ["normal"]
  });
  return windows.flatMap(browserWindow => browserWindow.tabs || [])
    .filter(tab => !tab.incognito && typeof tab.url === "string" && wanted.has(tab.url));
}

function consumeMatchingTabs(savedTabs, matchingTabs) {
  const liveByUrl = new Map();
  for (const tab of matchingTabs) {
    const queue = liveByUrl.get(tab.url) || [];
    queue.push(tab);
    liveByUrl.set(tab.url, queue);
  }

  const reused = [];
  const missing = [];
  for (const saved of savedTabs) {
    const queue = saved.url.startsWith("file://") ? [] : liveByUrl.get(saved.url) || [];
    const live = queue.shift();
    if (live) reused.push({ saved, live });
    else missing.push(saved);
  }
  return { reused, missing };
}

async function openTabsInWindow(session, savedTabs) {
  const pairs = [];
  const failures = [];
  let createdWindow = null;

  for (const saved of savedTabs) {
    try {
      let liveTab;
      if (!createdWindow) {
        createdWindow = await browser.windows.create({
          url: saved.url,
          left: session.left,
          top: session.top,
          width: session.width,
          height: session.height,
          focused: false,
          state: "normal"
        });
        const createdTabs = await browser.tabs.query({ windowId: createdWindow.id });
        liveTab = createdTabs[0];
        if (!liveTab) throw new Error("Firefox created a window without a tab.");
        await browser.tabs.update(liveTab.id, { pinned: Boolean(saved.pinned) });
      } else {
        liveTab = await browser.tabs.create({
          windowId: createdWindow.id,
          url: saved.url,
          active: false,
          pinned: Boolean(saved.pinned)
        });
      }
      pairs.push({ saved, live: liveTab });
    } catch (error) {
      failures.push({ saved, error: String(error) });
    }
  }

  if (!createdWindow || pairs.length === 0)
    return { pairs, failures, groupFailures: 0 };

  const activePair = pairs.find(pair => pair.saved.active) || pairs[0];
  await browser.tabs.update(activePair.live.id, { active: true });

  let groupFailures = 0;
  for (const group of session.groups || []) {
    const tabIds = pairs
      .filter(pair => pair.saved.groupIndex === group.index)
      .map(pair => pair.live.id);
    if (tabIds.length === 0) continue;
    try {
      const groupId = await browser.tabs.group({ tabIds });
      await browser.tabGroups.update(groupId, {
        title: group.title || undefined,
        color: group.color || "grey",
        collapsed: Boolean(group.collapsed)
      });
    } catch (error) {
      groupFailures += 1;
      console.warn("WindowAnchor could not restore a Firefox tab group", error);
    }
  }

  if (session.state && session.state !== "normal") {
    try {
      await browser.windows.update(createdWindow.id, {
        state: normalizeWindowState(session.state)
      });
    } catch (error) {
      console.warn("WindowAnchor could not restore the Firefox window state", error);
    }
  }
  return { pairs, failures, groupFailures };
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
  if (!message) return;
  if (message.protocolVersion !== PROTOCOL_VERSION) {
    sendResponse(message.requestId, {
      ok: false,
      error: "Browser extension protocol version is incompatible."
    });
    return;
  }

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

browser.browserAction.onClicked.addListener(() => {
  hostUnavailable = false;
  connectHost();
});

connectHost();
