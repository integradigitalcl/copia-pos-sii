window.grunflexKeyboard = (() => {
    let handler = null;
    let dotnetRef = null;
    let saleScanQueue = [];
    let saleScanBusy = false;
    let lastSubmittedCode = "";
    let lastSubmittedAt = 0;

    const navByKey = {
        F1: { section: "Ventas", selector: ".nav-item.nav-card.sales" },
        F3: { section: "Productos", selector: ".nav-item.nav-card.products" },
        F4: { section: "Inventario", selector: ".nav-item.nav-card.inventory" }
    };

    function takeAndClearSaleSearch() {
        const el = document.getElementById("venta-code-input");
        if (!el)
            return "";
        const value = (el.value || "").trim();
        if (el.value) {
            el.value = "";
            // Sincroniza SearchText en Blazor; el input ya no usa value= controlado.
            el.dispatchEvent(new Event("input", { bubbles: true }));
        }
        return value;
    }

    function enqueueSaleScan(code) {
        const normalized = (code || "").trim();
        if (!normalized)
            return;
        const now = Date.now();
        // Evita Enter duplicado del mismo pistoleo (keydown doble / fallback Blazor).
        if (normalized === lastSubmittedCode && (now - lastSubmittedAt) < 120)
            return;
        lastSubmittedCode = normalized;
        lastSubmittedAt = now;
        saleScanQueue.push(normalized);
        flushSaleScanQueue();
    }

    async function flushSaleScanQueue() {
        if (saleScanBusy || !dotnetRef)
            return;
        saleScanBusy = true;
        try {
            while (saleScanQueue.length > 0) {
                const code = saleScanQueue.shift();
                try {
                    await dotnetRef.invokeMethodAsync("SubmitSaleSearchCode", code);
                } catch (_) {
                    /* circuito ocupado: reencolar al frente y salir */
                    saleScanQueue.unshift(code);
                    break;
                }
            }
        } finally {
            saleScanBusy = false;
            if (saleScanQueue.length > 0)
                setTimeout(flushSaleScanQueue, 0);
        }
    }

    function isCheckoutOpen() {
        return !!document.querySelector(".checkout-modal-backdrop .checkout-modal");
    }

    function isProductSearchOpen() {
        return !!document.querySelector(".product-search-modal") || !!document.querySelector(".product-editor-overlay");
    }

    function isProductosSectionVisible() {
        const section = document.querySelector('.pos-section[data-section="Productos"]');
        return !!section && !section.hasAttribute("hidden");
    }

    function isVentasSectionVisible() {
        const section = document.querySelector('.pos-section[data-section="Ventas"]');
        return !!section && !section.hasAttribute("hidden");
    }

    function isInventarioSectionVisible() {
        const section = document.querySelector('.pos-section[data-section="Inventario"]');
        return !!section && !section.hasAttribute("hidden");
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
            saleScanQueue = [];
            saleScanBusy = false;
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

                if (key === "Enter") {
                    // Pistoleo rápido: limpiar el input YA (antes del round-trip Blazor)
                    // para que el siguiente código no se concatene.
                    if (active && active.id === "venta-code-input") {
                        event.preventDefault();
                        event.stopImmediatePropagation();
                        const selected = document.querySelector(".ventas-search-block .sales-suggestions button.selected")
                            || document.querySelector('.ventas-search-block .sales-suggestions button[aria-selected="true"]');
                        if (selected && dotnetRef) {
                            const productId = parseInt(selected.getAttribute("data-product-id") || "0", 10);
                            takeAndClearSaleSearch();
                            if (!Number.isNaN(productId) && productId > 0) {
                                dotnetRef.invokeMethodAsync("SubmitSaleProductId", productId);
                                return;
                            }
                            const idx = parseInt(selected.getAttribute("data-suggestion-index") || "-1", 10);
                            if (!Number.isNaN(idx) && idx >= 0)
                                dotnetRef.invokeMethodAsync("SubmitSaleSuggestion", idx);
                            return;
                        }
                        const code = takeAndClearSaleSearch();
                        enqueueSaleScan(code);
                    }
                    return;
                }

                if (isTextInput && (shortcut === "+" || shortcut === "-" || shortcut === "Add" || shortcut === "Subtract") &&
                    active.value)
                    return;

                function isSaleSearchFocused() {
                    return !!active && active.id === "venta-code-input";
                }

                function hasSalesSuggestions() {
                    return !!document.querySelector(".ventas-search-block .sales-suggestions");
                }

                function isProductEditorFieldFocused() {
                    return !!active && (
                        active.id === "product-code-input" ||
                        active.id === "product-name-input" ||
                        active.id === "product-code-input-overlay" ||
                        active.id === "product-name-input-overlay" ||
                        active.id === "products-catalog-search" ||
                        active.id === "product-search-modal-input" ||
                        active.id === "promo-product-input" ||
                        active.id === "promo-edit-search-input"
                    );
                }

                function isInventorySearchFocused() {
                    return !!active && active.id === "inventory-product-search";
                }

                const arrowKeys = ["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"];
                const supported = ["F1", "F2", "F3", "F4", "F7", "F8", "F10", "F11", "F12",
                    "Escape", "Delete", "+", "-", "Add", "Subtract",
                    "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"];
                if (!supported.includes(shortcut))
                    return;

                if (arrowKeys.includes(shortcut)) {
                    if (isCheckoutOpen()) {
                        // cobro: métodos de pago
                    } else if ((shortcut === "ArrowUp" || shortcut === "ArrowDown")
                        && isProductSearchOpen()) {
                        // Modal F10 / overlay: deja el @onkeydown local y asegura preventDefault + bridge.
                        event.preventDefault();
                        if (isProductEditorFieldFocused())
                            return;
                        if (dotnetRef)
                            dotnetRef.invokeMethodAsync("HandleKeyboardShortcut", shortcut);
                        return;
                    } else if ((shortcut === "ArrowUp" || shortcut === "ArrowDown")
                        && isProductosSectionVisible()
                        && !isProductSearchOpen()) {
                        // Navegación independiente por buscador activo.
                        if (active && active.id === "promo-product-input") {
                            event.preventDefault();
                            event.stopPropagation();
                            if (dotnetRef)
                                dotnetRef.invokeMethodAsync("NavigatePromoSuggestionsFromKeyboard", shortcut);
                            return;
                        }
                        if (active && active.id === "promo-edit-search-input") {
                            event.preventDefault();
                            event.stopPropagation();
                            return;
                        }
                        if (active && active.id === "products-catalog-search") {
                            event.preventDefault();
                            event.stopPropagation();
                            if (dotnetRef)
                                dotnetRef.invokeMethodAsync("NavigateProductCatalogFromKeyboard", shortcut);
                            return;
                        }
                        event.preventDefault();
                        if (isProductEditorFieldFocused())
                            return;
                        if (dotnetRef)
                            dotnetRef.invokeMethodAsync("HandleKeyboardShortcut", shortcut);
                        return;
                    } else if ((shortcut === "ArrowUp" || shortcut === "ArrowDown")
                        && isInventarioSectionVisible()) {
                        if (isInventorySearchFocused()) {
                            event.preventDefault();
                            event.stopPropagation();
                            if (dotnetRef)
                                dotnetRef.invokeMethodAsync("NavigateInventorySuggestionsFromKeyboard", shortcut);
                            return;
                        }
                        event.preventDefault();
                        if (dotnetRef)
                            dotnetRef.invokeMethodAsync("HandleKeyboardShortcut", shortcut);
                        return;
                    } else if ((shortcut === "ArrowUp" || shortcut === "ArrowDown")
                        && isVentasSectionVisible()
                        && !isProductSearchOpen()) {
                        if (isSaleSearchFocused() && hasSalesSuggestions())
                            return;
                    } else {
                        return;
                    }
                }

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
            saleScanQueue = [];
            saleScanBusy = false;
        },
        takeAndClearSaleSearch,
        getSelectedSaleSuggestionProductId() {
            const selected = document.querySelector(".ventas-search-block .sales-suggestions button.selected")
                || document.querySelector('.ventas-search-block .sales-suggestions button[aria-selected="true"]');
            if (!selected)
                return 0;
            const id = parseInt(selected.getAttribute("data-product-id") || "0", 10);
            return Number.isNaN(id) ? 0 : id;
        },
        focusSaleSearch() {
            const attempt = (left) => {
                const el = document.getElementById("venta-code-input");
                const ventas = document.querySelector('.pos-section[data-section="Ventas"]');
                const visible = !!ventas && !ventas.hasAttribute("hidden");
                if (el && visible) {
                    try {
                        el.focus({ preventScroll: true });
                        // No usar select(): en pistoleo rápido borra/mezcla el siguiente código.
                    } catch (_) {
                        el.focus();
                    }
                    return document.activeElement === el;
                }
                if (left > 0)
                    setTimeout(() => attempt(left - 1), 40);
                return false;
            };
            return attempt(12);
        },
        scrollProductSearchRow(index) {
            const row = document.querySelector(`.product-search-catalog tr[data-search-index="${index}"]`);
            if (!row)
                return;
            row.scrollIntoView({ block: "nearest", inline: "nearest" });
        },
        scrollProductsCatalogRow(index) {
            const row = document.querySelector(`.products-workspace .products-table tr[data-catalog-index="${index}"]`);
            if (!row)
                return;
            row.scrollIntoView({ block: "nearest", inline: "nearest" });
        },
        scrollInventoryListRow(index) {
            const row = document.querySelector(`.inventory-list-card tr[data-inventory-index="${index}"]`);
            if (!row)
                return;
            row.scrollIntoView({ block: "nearest", inline: "nearest" });
        },
        scrollSalesSuggestion(index) {
            const list = document.querySelector(".ventas-search-block .sales-suggestions");
            if (!list)
                return;
            const items = list.querySelectorAll("button[role='option']");
            const row = items && items[index];
            if (!row)
                return;
            row.scrollIntoView({ block: "nearest", inline: "nearest" });
        },
        scrollPromoSuggestion(index) {
            const row = document.querySelector(`.promo-product-suggestions button[data-promo-suggestion-index="${index}"]`);
            if (!row)
                return;
            row.scrollIntoView({ block: "nearest", inline: "nearest" });
        },
        scrollInventorySuggestion(index) {
            const row = document.querySelector(`.inventory-product-suggestions button[data-inventory-suggestion-index="${index}"]`);
            if (!row)
                return;
            row.scrollIntoView({ block: "nearest", inline: "nearest" });
        }
    };
})();
