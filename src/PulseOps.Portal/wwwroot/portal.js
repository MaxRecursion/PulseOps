(() => {
    const key = "pulseops.theme";
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const valid = (value) => value === "light" || value === "dark";
    let preference = null;
    try { preference = localStorage.getItem(key); } catch {}

    function apply() {
        const theme = valid(preference) ? preference : (media.matches ? "dark" : "light");
        document.documentElement.dataset.theme = theme;
        document.querySelector('meta[name="theme-color"]').content = theme === "dark" ? "#0d0e0c" : "#f5f1e8";
    }

    apply();
    media.addEventListener("change", apply);
    window.addEventListener("storage", (event) => {
        if (event.key === key || event.key === null) {
            preference = event.newValue;
            apply();
        }
    });
    window.pulsePortal = {
        toggleTheme() {
            preference = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
            apply();
            try { localStorage.setItem(key, preference); } catch {}
        }
    };
})();
