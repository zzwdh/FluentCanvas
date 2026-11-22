const assert = require("node:assert/strict");
const fs = require("node:fs");
const { resolve } = require("node:path");
const test = require("node:test");
const { runInNewContext } = require("node:vm");
const { randomUUID, webcrypto } = require("node:crypto");

const source = fs.readFileSync(resolve(__dirname, "../integrations/wps/main.js"), "utf8");

function fixture() {
    const files = new Map();
    const listeners = new Map();
    let poll;
    const context = {
        crypto: webcrypto,
        console: { warn() {} },
        setTimeout: () => 1,
        clearTimeout: () => {},
        setInterval: callback => { poll = callback; return 1; },
        clearInterval: () => {},
        window: { addEventListener() {} }
    };
    runInNewContext(source, context);
    const slides = [{ SlideID: 258, SlideIndex: 1 }, { SlideID: 741, SlideIndex: 2 }];
    let index = 0;
    const view = {
        get Slide() { return slides[index]; },
        Next() { index = Math.min(index + 1, slides.length - 1); },
        Previous() { index = Math.max(index - 1, 0); },
        Exit() { shows.Count = 0; }
    };
    const shows = { Count: 1, Item: () => ({
        Active: true, View: view, Presentation: { FullName: "C:\\lesson.dps", Slides: { Count: slides.length } }
    }) };
    const app = {
        Env: { GetAppDataPath: () => "X:\\Roaming\\" },
        FileSystem: {
            existsSync: path => files.has(path),
            mkdirSync: path => files.set(path, ""),
            writeFileString: (path, text) => files.set(path, text),
            readFileString: path => files.get(path),
            unlinkSync: path => files.delete(path)
        },
        ApiEvent: {
            AddApiEventListener: (name, callback) => listeners.set(name, callback),
            RemoveApiEventListener: name => listeners.delete(name)
        },
        SlideShowWindows: shows
    };
    const bridge = context.createFluentCanvasWpsBridge(app);
    function pathFor(kind) {
        return [...files.keys()].find(path => path.includes("\\" + kind + "-"));
    }
    return {
        bridge, shows, listeners, view,
        poll: () => poll(),
        state: () => files.has(pathFor("state")) ? JSON.parse(files.get(pathFor("state"))) : undefined,
        command: message => files.set(pathFor("command"), JSON.stringify(message))
    };
}

test("attaches to an already open show and renews its identity after stop", () => {
    const client = fixture();
    const first = client.state();
    assert.equal(first.slideId, 258);
    assert.equal(first.canNext, true);
    client.view.Next();
    client.poll();
    assert.equal(client.state().slideId, 741);
    assert.equal(client.state().sessionId, first.sessionId);
    client.shows.Count = 0;
    client.listeners.get("SlideShowEnd")();
    client.poll();
    assert.equal(client.state(), undefined);
    client.shows.Count = 1;
    client.poll();
    assert.notEqual(client.state().sessionId, first.sessionId);
    client.bridge.close();
    assert.equal(client.state(), undefined);
    assert.equal(client.listeners.size, 0);
});

test("executes only fresh whitelisted commands for the active session", () => {
    const client = fixture();
    const first = client.state();
    let nextCalls = 0;
    client.view.Next = () => { nextCalls++; };
    const valid = {
        version: 1, instanceId: first.instanceId, sessionId: first.sessionId,
        id: randomUUID(), action: "next", expiresAt: Date.now() + 1000
    };
    for (const change of [
        { instanceId: randomUUID() }, { sessionId: randomUUID() }, { expiresAt: Date.now() - 100 },
        { action: "Open" }
    ]) {
        client.command({ ...valid, ...change });
        client.poll();
    }
    assert.equal(nextCalls, 0);
    client.command(valid);
    client.poll();
    client.poll();
    assert.equal(nextCalls, 1);
    assert.equal(client.state().ackId, valid.id);
    assert.equal(client.state().ackSucceeded, true);
    client.view.Previous = () => { throw new Error("WPS busy"); };
    const rejected = { ...valid, id: randomUUID(), action: "previous" };
    client.command(rejected);
    client.poll();
    assert.equal(client.state().ackId, rejected.id);
    assert.equal(client.state().ackSucceeded, false);
    client.command(valid);
    client.poll();
    assert.equal(nextCalls, 1);
    client.shows.Count = 2;
    client.poll();
    assert.equal(client.state(), undefined);
    client.bridge.close();
});
