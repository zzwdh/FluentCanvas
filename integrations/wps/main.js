"use strict";

// WPS desktop presentation add-in. See README.md for the supported installation path.
var fluentCanvasWpsBridge;

function OnFluentCanvasLoaded() {
    if (!fluentCanvasWpsBridge && typeof Application !== "undefined") {
        try {
            fluentCanvasWpsBridge = createFluentCanvasWpsBridge(Application);
        } catch (error) {
            console.warn("Fluent Canvas: WPS desktop bridge unavailable.", error);
        }
    }
    return true;
}

function createFluentCanvasWpsBridge(app) {
    const version = 1;
    const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)),
        byte => byte.toString(16).padStart(2, "0")).join("")
        .replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/, "$1-$2-$3-$4-$5");
    const instanceId = newId();
    const mailboxKey = instanceId.replace(/-/g, "");
    const base = app.Env.GetAppDataPath().replace(/[\\/]+$/, "");
    if (!base) throw new Error("WPS did not return a user AppData path.");
    const parent = base + "\\Fluent Canvas WPS Bridge";
    const directory = parent + "\\v1";
    const stateFile = directory + "\\state-" + mailboxKey + ".json";
    const commandFile = directory + "\\command-" + mailboxKey + ".json";
    const fs = app.FileSystem;
    const events = [];
    const usedCommands = new Map();
    let sessionId;
    let presentationIdentity;
    let acknowledged;
    let resetSession = false;
    let statePublished = false;
    let timer;
    let updateScheduled;
    let closed = false;

    function createMailbox() {
        for (const path of [parent, directory]) {
            try {
                if (!fs.existsSync(path)) fs.mkdirSync(path);
            } catch (error) {
                if (!fs.existsSync(path)) throw error;
            }
        }
        if (!fs.existsSync(commandFile)) fs.writeFileString(commandFile, "{}");
    }

    function sampleShow() {
        const shows = app.SlideShowWindows;
        // A command is safe only when this client has a single identifiable show.
        if (shows.Count !== 1) return undefined;
        const show = shows.Item(1);
        const deck = show.Presentation;
        let deckIdentity = String(deck.Name || "");
        try { deckIdentity = String(deck.FullName || deckIdentity); } catch (error) { }
        const view = show.View;
        const slide = view.Slide;
        const slideId = Number(slide.SlideID);
        const slideIndex = Number(slide.SlideIndex);
        const slideCount = Number(deck.Slides.Count);
        if (![slideId, slideIndex, slideCount].every(Number.isInteger) ||
            slideId < 1 || slideIndex < 1 || slideIndex > slideCount)
            return undefined;
        if (!sessionId || resetSession || deckIdentity !== presentationIdentity) {
            sessionId = newId();
            presentationIdentity = deckIdentity;
            resetSession = false;
        }
        return { view, sessionId, slideId, slideIndex, slideCount, active: show.Active === true ||
            show.Active === -1 || show.Active === 1 };
    }

    function readCommand(show) {
        const text = fs.readFileString(commandFile);
        if (typeof text !== "string" || text.length > 2048) return;
        const command = JSON.parse(text);
        const now = Date.now();
        if (!command || command.version !== version || command.instanceId !== instanceId ||
            command.sessionId !== show.sessionId ||
            !/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/.test(command.id) ||
            !Number.isSafeInteger(command.expiresAt) ||
            command.expiresAt < now || command.expiresAt > now + 2100 || usedCommands.has(command.id))
            return;
        const method = { next: "Next", previous: "Previous", exit: "Exit" }[command.action];
        if (typeof method !== "string") return;
        usedCommands.set(command.id, command.expiresAt);
        acknowledged = { id: command.id, succeeded: false };
        if (typeof show.view[method] !== "function") return;
        try {
            show.view[method]();
            acknowledged.succeeded = true;
        } catch (error) {
            console.warn("Fluent Canvas: WPS rejected a slide show command.", error);
        }
    }

    function update() {
        if (closed) return;
        try {
            const now = Date.now();
            for (const [id, expiresAt] of usedCommands)
                if (expiresAt < now) usedCommands.delete(id);
            if (app.SlideShowWindows.Count !== 1) {
                if (statePublished && fs.existsSync(stateFile)) fs.unlinkSync(stateFile);
                statePublished = false;
                resetSession = true;
                return;
            }
            let show = sampleShow();
            if (!show) return;
            try { readCommand(show); } catch (error) {
                // A command replacement may be in progress. Read it on the next tick.
            }
            show = sampleShow();
            if (!show) {
                if (fs.existsSync(stateFile)) fs.unlinkSync(stateFile);
                statePublished = false;
                resetSession = true;
                return;
            }
            fs.writeFileString(stateFile, JSON.stringify({
                version, instanceId, sessionId: show.sessionId, slideId: show.slideId,
                slideIndex: show.slideIndex, slideCount: show.slideCount, active: show.active,
                canNext: typeof show.view.Next === "function",
                canPrevious: typeof show.view.Previous === "function",
                canExit: typeof show.view.Exit === "function",
                ackId: acknowledged?.id || "00000000-0000-0000-0000-000000000000",
                ackSucceeded: acknowledged?.succeeded === true
            }));
            statePublished = true;
        } catch (error) {
            // Slide transitions and modal UI can make the WPS object model temporarily unavailable.
        }
    }

    function scheduleUpdate() {
        if (closed || updateScheduled !== undefined) return;
        updateScheduled = setTimeout(() => {
            updateScheduled = undefined;
            update();
        }, 0);
    }

    function subscribe(event, listener) {
        try {
            app.ApiEvent.AddApiEventListener(event, listener);
            events.push([event, listener]);
        } catch (error) {
            // Some builds expose the object model without supporting all add-in events.
        }
    }

    function close() {
        if (closed) return;
        closed = true;
        clearInterval(timer);
        clearTimeout(updateScheduled);
        for (const [event, listener] of events) {
            try { app.ApiEvent.RemoveApiEventListener(event, listener); } catch (error) { }
        }
        for (const file of [stateFile, commandFile]) {
            try { if (fs.existsSync(file)) fs.unlinkSync(file); } catch (error) { }
        }
    }

    createMailbox();
    for (const event of ["SlideShowBegin", "SlideShowEnd"]) {
        subscribe(event, () => {
            resetSession = true;
            scheduleUpdate();
        });
    }
    subscribe("SlideShowNextSlide", scheduleUpdate);
    subscribe("Quit", close);
    window.addEventListener("beforeunload", close, { once: true });
    timer = setInterval(update, 150);
    update();
    return { close };
}
