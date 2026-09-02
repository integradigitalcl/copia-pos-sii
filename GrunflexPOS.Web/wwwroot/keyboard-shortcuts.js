window.grunflexKeyboard = (() => {
    let handler = null;
    let dotnetRef = null;

    const navByKey = {
        F1: { section: "Ventas", selector: ".nav-item.nav-card.sales" },
        F3: { section: "Productos", selector: ".nav-item.nav-card.products" },
        F4: { section: "Inventario", selector: ".nav-item.nav-card.inventory" }
    };

    function isCheckoutOpen() {
        return !!document.querySelector(".checkout-modal-backdrop .checkout-modal");
    }

    function isProductSearchOpen() {
        return !!document.querySelector(".product-search-modal") || !!document.querySelector(".product-editor-overlay");
    }

    function applySectionInstant(section) {
        document.querySelectorAll(".nav-item.nav-card.active").forEach(el => el.classList.remove("active"));
        const meta = Object.values(navByKey).find(x => x.section === section);
        if (meta) {
            const btn = document.querySelector(meta.selector);
            if (btn)
                btn.classList.add("active");
        }

        document.querySelectorAll(".pos-section").forEach(el => {
            const match = el.getAttribute("data-section") === section;
            if (match)
                el.removeAttribute("hidden");
            else
                el.setAttribute("hidden", "");
        });
    }

    return {
        register(dotnet) {
            dotnetRef = dotnet;
            if (handler)
                document.removeEventListener("keydown", handler, true);

            handler = event => {
                if (event.repeat)
                    return;

                const key = event.key;
                const shortcut = event.code === "NumpadAdd" ? "Add" :
                    event.code === "NumpadSubtract" ? "Subtract" : key;
                const active = document.activeElement;
                const isTextInput = active &&
                    (active.tagName === "INPUT" || active.tagName === "TEXTAREA" ||
                        active.isContentEditable);

                if (key === "Enter")
                    return;

                if (isTextInput && (shortcut === "+" || shortcut === "-" || shortcut === "Add" || shortcut === "Subtract") &&
                    active.value)
                    return;

                const supported = ["F1", "F2", "F3", "F4", "F7", "F8", "F10", "F11", "F12",
                    "Escape", "Delete", "+", "-", "Add", "Subtract"];
                if (!supported.includes(shortcut))
                    return;

                event.preventDefault();
                event.stopPropagation();

                // Instant navigation for F1/F3/F4 (avoid waiting on Blazor Server RTT).
                const nav = navByKey[shortcut];
                if (nav && !isCheckoutOpen() && !isProductSearchOpen()) {
                    const btn = document.querySelector(nav.selector);
                    if (btn && btn.disabled)
                        return;
                    applySectionInstant(nav.section);
                    // Also hide the "Other" panel instantly when leaving Reportes/Config/etc.
                    const other = document.querySelector('.pos-section[data-section="Other"]');
                    if (other)
                        other.setAttribute("hidden", "");
                    if (dotnetRef)
                        dotnetRef.invokeMethodAsync("HandleKeyboardShortcut", shortcut);
                    return;
                }

                if (dotnetRef)
                    dotnetRef.invokeMethodAsync("HandleKeyboardShortcut", shortcut);
            };

            document.addEventListener("keydown", handler, true);
        },
        unregister() {
            if (handler)
                document.removeEventListener("keydown", handler, true);
            handler = null;
            dotnetRef = null;
        }
    };
})();
