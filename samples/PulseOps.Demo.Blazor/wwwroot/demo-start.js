"use strict";
window.PulseOpsDemo = Object.freeze({
    raiseJavaScriptError() {
        setTimeout(() => { throw new Error("Simulated JavaScript exception"); }, 0);
    }
});

const start = window.PulseOpsBootstrap
    ? window.PulseOpsBootstrap.start()
    : window.Blazor?.start();
start?.catch(() => {
    const app = document.getElementById("app");
    if (app)
        app.textContent = "The reporting workspace could not start.";
});
