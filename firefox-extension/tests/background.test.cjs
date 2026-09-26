const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { webcrypto } = require("node:crypto");

function loadBackground() {
  const posted = [];
  const port = {
    error: null,
    onMessage: { addListener() {} },
    onDisconnect: { addListener() {} },
    postMessage(message) { posted.push(message); }
  };
  const browser = {
    runtime: { connectNative: () => port },
    browserAction: { onClicked: { addListener() {} } },
    storage: {
      local: {
        get: async () => ({ windowAnchorProfileKey: "profile-a" }),
        set: async () => {}
      }
    },
    windows: {
      getAll: async () => [],
      update: async () => {}
    },
    tabs: {
      update: async () => {},
      query: async () => [],
      create: async () => ({ id: 1, windowId: 1 }),
      group: async () => 1
    },
    tabGroups: {
      TAB_GROUP_ID_NONE: -1,
      query: async () => [],
      update: async () => {}
    }
  };
  const context = vm.createContext({ browser, console, crypto: webcrypto });
  const source = fs.readFileSync(path.join(__dirname, "..", "background.js"), "utf8");
  vm.runInContext(source, context);
  return { context, posted };
}

test("only explicitly supported URL schemes are restorable", () => {
  const { context } = loadBackground();

  assert.equal(vm.runInContext('isRestorableUrl("https://example.com")', context), true);
  assert.equal(vm.runInContext('isRestorableUrl("file:///C:/notes.txt")', context), true);
  assert.equal(vm.runInContext('isRestorableUrl("about:config")', context), false);
  assert.equal(vm.runInContext('isRestorableUrl("moz-extension://internal")', context), false);
  assert.equal(vm.runInContext('isRestorableUrl("javascript:alert(1)")', context), false);
});

test("exact reuse preserves multiplicity and never consumes file URLs", () => {
  const { context } = loadBackground();
  const result = vm.runInContext(`consumeMatchingTabs(
    [
      { url: "https://example.com/a" },
      { url: "https://example.com/a" },
      { url: "file:///C:/notes.txt" }
    ],
    [
      { id: 1, url: "https://example.com/a" },
      { id: 2, url: "file:///C:/notes.txt" }
    ])`, context);

  assert.equal(result.reused.length, 1);
  assert.equal(result.reused[0].live.id, 1);
  assert.equal(result.missing.length, 2);
  assert.equal(result.missing[1].url, "file:///C:/notes.txt");
});

test("one failed tab produces a successful partial session result", async () => {
  const { context } = loadBackground();
  vm.runInContext(`openTabsInWindow = async () => ({
    pairs: [{ saved: { url: "https://example.com/ok" }, live: { id: 7, windowId: 3 } }],
    failures: [{ saved: { url: "https://example.com/fail" }, error: "blocked" }],
    groupFailures: 0
  })`, context);

  const results = await vm.runInContext(`restoreSessions([{
    browserSessionId: "session-a",
    profileKey: "profile-a",
    restorePolicy: "OpenDuplicate",
    tabs: [
      { url: "https://example.com/ok", index: 0, active: true },
      { url: "https://example.com/fail", index: 1 }
    ],
    groups: []
  }])`, context);

  assert.equal(results.length, 1);
  assert.equal(results[0].ok, true);
  assert.equal(results[0].status, "partiallyOpened");
  assert.equal(results[0].tabs, 1);
  assert.equal(results[0].failedTabs, 1);
});

test("manifest excludes sensitive browser stores and declares native-message data", () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "manifest.json"), "utf8"));
  const permissions = new Set(manifest.permissions);

  assert.equal(manifest.incognito, "not_allowed");
  assert.equal(permissions.has("cookies"), false);
  assert.equal(permissions.has("history"), false);
  assert.equal(permissions.has("contextualIdentities"), false);
  assert.deepEqual(
    manifest.browser_specific_settings.gecko.data_collection_permissions.required,
    ["browsingActivity", "websiteContent", "personallyIdentifyingInfo"]);
});
