/* PulseOps browser bridge. No event includes untrusted text, URLs, stack traces, or DOM data. */
(() => {
    "use strict";

    const settings = document.currentScript?.dataset ?? {};
    const enabled = settings.enabled !== "false";
    const clock = () => performance.now();
    const slug = /^[a-z0-9][a-z0-9-]{0,39}$/;
    const version = /^[a-z0-9][a-z0-9._-]{0,31}$/i;
    const sessionKey = "pulseops.session";
    const scope = [settings.application, settings.environment, settings.version];
    let sessionId;
    try {
        if (enabled)
            sessionId = sessionStorage.getItem(sessionKey);
    } catch { /* Storage can be blocked by browser policy. */ }

    function uuid() {
        const bytes = crypto.getRandomValues(new Uint8Array(16));
        bytes[6] = (bytes[6] & 15) | 64;
        bytes[8] = (bytes[8] & 63) | 128;
        const parts = Array.from(bytes, value => value.toString(16).padStart(2, "0"));
        return [parts.slice(0, 4), parts.slice(4, 6), parts.slice(6, 8), parts.slice(8, 10), parts.slice(10)].map(
            part => part.join("")).join("-");
    }

    try {
        if (enabled) {
            if (!/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(sessionId ?? ""))
                sessionId = uuid();
            sessionStorage.setItem(sessionKey, sessionId);
        }
    } catch { /* An in-memory session remains usable. */ }

    let endpoint;
    try {
        const url = new URL(settings.startupEndpoint);
        const approved = url.protocol === "https:" ||
            (url.protocol === "http:" && ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname));
        if (approved && !url.username && !url.password && !url.search && !url.hash &&
            scope.every((value, index) => value && (index === 2 ? version : slug).test(value))) {
            endpoint = url.href;
        }
    } catch { /* Anonymous export is opt-in with a valid endpoint. */ }

    let connector;
    let errorTracking = false;
    let startupTracking = enabled;
    let startupDeadline = 0;
    let applicationReady = false;
    let startupSending = false;
    let startupScheduled = false;
    let firstHidden = document.hidden ? 0 : Infinity;
    let errorWindow = 0;
    let errorCount = 0;
    const startupEvents = [];
    const milestones = new Set();

    function mark(name, outcome = "success", category) {
        if (!startupTracking || !endpoint || !sessionId || milestones.has(name))
            return;

        milestones.add(name);
        startupEvents.push({
            id: uuid(),
            occurredAt: new Date().toISOString(),
            version: settings.version,
            sessionId,
            kind: "startup",
            name,
            outcome,
            durationMs: Math.max(0, clock()),
            category
        });
        scheduleStartup();
    }

    function scheduleStartup() {
        if (startupSending || startupScheduled || startupEvents.length === 0)
            return;

        startupScheduled = true;
        setTimeout(() => {
            startupScheduled = false;
            void sendStartup();
        }, 80);
    }

    async function sendStartup() {
        if (startupSending || startupEvents.length === 0)
            return;

        startupSending = true;
        const events = startupEvents.splice(0, 12);
        const body = JSON.stringify({
            schemaVersion: 1,
            application: settings.application,
            environment: settings.environment,
            events
        });
        try {
            for (let attempt = 0; attempt < 2; attempt++) {
                let retryDelay = 500 + Math.random() * 400;
                const controller = new AbortController();
                const abort = setTimeout(() => controller.abort(), 5000);
                try {
                    const response = await fetch(endpoint, {
                        method: "POST",
                        credentials: "omit",
                        mode: "cors",
                        redirect: "error",
                        headers: { "Content-Type": "application/json" },
                        body,
                        keepalive: true,
                        signal: controller.signal
                    });
                    if (response.status === 410) {
                        // Operator kill switch: intake for this application is suspended. Stop for this page.
                        startupTracking = false;
                        startupEvents.length = 0;
                        break;
                    }
                    if (response.status === 202 ||
                        (response.status !== 429 && response.status !== 408 && response.status < 500))
                        break;

                    const seconds = Number(response.headers.get("Retry-After"));
                    if (seconds > 0)
                        retryDelay = Math.min(seconds * 1000, 30_000);
                } catch { /* Browser shutdown, network failure, or policy can prevent delivery. */ }
                finally {
                    clearTimeout(abort);
                }

                if (attempt === 0)
                    await new Promise(resolve => setTimeout(resolve, retryDelay));
            }
        } finally {
            startupSending = false;
            scheduleStartup();
        }
    }

    function report(kind, name, value = null) {
        connector?.invokeMethodAsync("RecordBrowserEvent", kind, name, value).catch(() => {});
    }

    function reportError(startupName, runtimeName) {
        if (!applicationReady) {
            mark(startupName, "failure", "javascript");
            return;
        }
        if (!errorTracking)
            return;

        const now = clock();
        if (now - errorWindow >= 60_000) {
            errorWindow = now;
            errorCount = 0;
        }
        if (++errorCount <= 20)
            report("error", runtimeName);
    }

    if (enabled) {
        mark("PageLoaded");
        mark("BootstrapStarted");
        if (!globalThis.WebAssembly)
            mark("UnsupportedBrowser", "failure", "unsupported");

        window.addEventListener("error", event => {
            if (event instanceof ErrorEvent) {
                reportError("JavaScriptError", "javascript-error");
            } else if (!applicationReady && event.target instanceof HTMLScriptElement &&
                event.target.src.includes("/_framework/")) {
                mark("AssetLoadFailure", "failure", "resource");
            }
        }, true);
        window.addEventListener("unhandledrejection", () => reportError("PromiseRejection", "promise-rejection"));
        navigator.serviceWorker?.addEventListener("messageerror", () => {
            if (!applicationReady)
                mark("ServiceWorkerFailure", "failure", "service-worker");
        });

        document.addEventListener("visibilitychange", () => {
            if (document.hidden)
                firstHidden = Math.min(firstHidden, clock());
        }, { passive: true });
        const waitMs = Number(settings.startupTimeoutMs || "30000");
        startupDeadline = setTimeout(() => {
            if (applicationReady)
                return;

            const root = document.getElementById(settings.appRootId || "app");
            mark(!root || root.childNodes.length === 0 ? "BlankScreen" : "StartupTimeout", "failure", "timeout");
        }, Number.isFinite(waitMs) ? Math.max(1000, Math.min(waitMs, 120000)) : 30000);
    }

    function collectPerformance() {
        if (!globalThis.PerformanceObserver?.supportedEntryTypes)
            return;

        const types = new Set(PerformanceObserver.supportedEntryTypes);
        const observers = [];
        const activeTypes = new Set();
        const metrics = new Map();
        const sent = new Map();
        const interactionLatencies = new Map();
        let layoutWindow;
        let longTaskCount = 0;
        let restored = false;

        function observe(type, read, extra) {
            if (!types.has(type))
                return;

            try {
                const observer = new PerformanceObserver(list => list.getEntries().forEach(read));
                observer.observe({ type, buffered: true, ...extra });
                observers.push([observer, read]);
                activeTypes.add(type);
            } catch { /* Some engines advertise entries but reject buffered observation. */ }
        }

        observe("largest-contentful-paint", entry => {
            if (entry.startTime < firstHidden)
                metrics.set("lcp", entry.startTime);
        });
        observe("paint", entry => {
            if (entry.name === "first-contentful-paint" && entry.startTime < firstHidden)
                metrics.set("fcp", entry.startTime);
        });
        observe("layout-shift", entry => {
            if (entry.hadRecentInput || entry.startTime >= firstHidden)
                return;

            if (!layoutWindow || entry.startTime - layoutWindow.last >= 1000 || entry.startTime - layoutWindow.first >= 5000)
                layoutWindow = { first: entry.startTime, last: entry.startTime, value: 0 };
            layoutWindow.last = entry.startTime;
            layoutWindow.value += entry.value;
            metrics.set("cls", Math.max(metrics.get("cls") ?? 0, layoutWindow.value));
        });

        function recordInteraction(entry) {
            if (!entry.interactionId || entry.startTime >= firstHidden)
                return;

            const previous = interactionLatencies.get(entry.interactionId);
            if (previous !== undefined) {
                interactionLatencies.set(entry.interactionId, Math.max(previous, entry.duration));
            } else if (interactionLatencies.size < 100) {
                interactionLatencies.set(entry.interactionId, entry.duration);
            } else {
                let lowest = [0, Infinity];
                for (const item of interactionLatencies) {
                    if (item[1] < lowest[1])
                        lowest = item;
                }
                if (entry.duration > lowest[1]) {
                    interactionLatencies.delete(lowest[0]);
                    interactionLatencies.set(entry.interactionId, entry.duration);
                }
            }
        }

        if (typeof performance.interactionCount === "number") {
            observe("event", recordInteraction, { durationThreshold: 16 });
            observe("first-input", recordInteraction);
        }

        observe("longtask", entry => {
            if (entry.startTime < firstHidden)
                metrics.set("long-task-count", ++longTaskCount);
        });

        function emit() {
            if (restored || firstHidden === 0)
                return;

            for (const [observer, read] of observers)
                observer.takeRecords().forEach(read);

            const nav = performance.getEntriesByType("navigation")[0];
            if (nav?.responseStart > 0)
                metrics.set("ttfb", nav.responseStart);
            if (nav?.domComplete > 0)
                metrics.set("navigation-duration", nav.domComplete);
            if (activeTypes.has("layout-shift") && !metrics.has("cls"))
                metrics.set("cls", 0);
            if (activeTypes.has("longtask") && !metrics.has("long-task-count"))
                metrics.set("long-task-count", 0);

            const rank = Math.floor(performance.interactionCount / 50);
            if (rank < interactionLatencies.size) {
                const top = [...interactionLatencies.values()].sort((left, right) => right - left);
                metrics.set("inp", top[rank]);
            }

            for (const [name, value] of metrics) {
                const rounded = Math.round(value * 1000) / 1000;
                if (sent.get(name) === rounded)
                    continue;

                sent.set(name, rounded);
                report("metric", name, rounded);
            }
        }

        document.addEventListener("visibilitychange", () => {
            if (document.hidden)
                emit();
        }, { passive: true });
        window.addEventListener("pagehide", emit, { passive: true });
        window.addEventListener("pageshow", event => {
            if (event.persisted) {
                restored = true;
                for (const [observer] of observers)
                    observer.disconnect();
            }
        });
        setTimeout(emit, 10_000);
    }

    window.PulseOpsBootstrap = Object.freeze({
        start(options = {}) {
            if (!globalThis.Blazor) {
                mark("AssetLoadFailure", "failure", "resource");
                return Promise.resolve();
            }

            const existing = options.loadBootResource;
            const observed = {
                ...options,
                loadBootResource(type, ...args) {
                    mark("RuntimeLoading");
                    const result = existing?.(type, ...args);
                    if (typeof result?.then !== "function")
                        return result;

                    return result.catch(error => {
                        mark("AssetLoadFailure", "failure", "resource");
                        throw error;
                    });
                }
            };
            return Blazor.start(enabled ? observed : options).catch(error => {
                mark("AssetLoadFailure", "failure", "runtime");
                throw error;
            });
        },

        attach(dotnet, errors, performanceEnabled, sessions, startup) {
            if (!enabled || connector)
                return sessionId;

            connector = dotnet;
            errorTracking = errors;
            startupTracking = startup;
            mark("RuntimeStarted");
            mark("BlazorInitialised");

            if (performanceEnabled)
                collectPerformance();
            if (sessions) {
                window.addEventListener("online", () => report("session", "online"), { passive: true });
                window.addEventListener("offline", () => report("session", "offline"), { passive: true });
                document.addEventListener("visibilitychange", () => {
                    report("session", document.hidden ? "hidden" : "visible");
                }, { passive: true });
                window.addEventListener("pagehide", event => {
                    if (!event.persisted)
                        report("session", "ended");
                }, { passive: true });
            }

            return sessionId;
        },

        ready() {
            if (!applicationReady) {
                applicationReady = true;
                clearTimeout(startupDeadline);
                mark("ApplicationReady");
            }
        }
    });
})();
