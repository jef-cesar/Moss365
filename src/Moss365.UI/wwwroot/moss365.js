// Kleine hulpfuncties voor Moss365 (aangeroepen via IJSRuntime)
window.moss365 = {
    selecteer: function (element) { if (element) { element.focus(); element.select(); } },
    kopieer: function (tekst) { return navigator.clipboard.writeText(tekst); },
    naarOnder: function (element) { if (element) { element.scrollTop = element.scrollHeight; } },
    naar: function (selector) { var el = document.querySelector(selector); if (el) { el.scrollIntoView({ behavior: 'smooth', block: 'start' }); } },

    // Terug en Vooruit: de muisknoppen en Alt+pijl gaan langs de knoppen van de app (eigen geschiedenis), en de
    // scrollpositie van elke pagina wordt onthouden om ze terug te zetten na Terug of Vooruit.
    navigatie: function (ref) {
        if (window.mossNavigatie) { window.mossNavigatie.ref = ref; return; }
        var staat = { ref: ref, scroll: {} };
        window.mossNavigatie = staat;
        var stap = function (e, terug) { e.preventDefault(); staat.ref.invokeMethodAsync(terug ? 'Back' : 'Forward'); };
        document.addEventListener('keydown', function (e) {
            if (!e.altKey || e.ctrlKey || e.shiftKey || e.metaKey) { return; }
            if (e.target && e.target.closest && e.target.closest('.monaco-editor')) { return; }
            if (e.key === 'ArrowLeft') { stap(e, true); } else if (e.key === 'ArrowRight') { stap(e, false); }
        }, true);
        var muis = function (e) {
            if (e.button === 3 || e.button === 4) {
                e.preventDefault();
                if (e.type === 'mouseup') { stap(e, e.button === 3); }
            }
        };
        document.addEventListener('mousedown', muis, true);
        document.addEventListener('mouseup', muis, true);
        // Een veld veranderde: de app kijkt of er nu iets openstaat (tint, balk, bolletje in het menu)
        var wacht = null;
        document.addEventListener('change', function () {
            if (wacht) { clearTimeout(wacht); }
            wacht = setTimeout(function () { wacht = null; staat.ref.invokeMethodAsync('FieldChanged'); }, 150);
        }, true);
        var vorige = location.href;
        window.addEventListener('scroll', function () {
            if (location.href === vorige) { staat.scroll[vorige] = window.scrollY; } else { vorige = location.href; }
        }, { passive: true });
    },
    herstelScroll: function (adres) {
        var staat = window.mossNavigatie;
        var y = staat && staat.scroll[adres];
        if (!y) { return; }
        var poging = 0;
        var zet = function () {
            window.scrollTo(0, y);
            if (Math.abs(window.scrollY - y) > 2 && ++poging < 10) { setTimeout(zet, 50); }
        };
        setTimeout(zet, 30);
    }
};

// Code-editor (Monaco, de editor van VS Code), ingebouwd in de app: geen internet nodig
window.mossEditor = (function () {
    var basis = '_content/Moss365.UI/lib/monaco/vs';
    var laden = null;

    function laad() {
        if (laden) { return laden; }
        laden = new Promise(function (ok, fout) {
            window.MonacoEnvironment = { getWorkerUrl: function () { return basis + '/base/worker/workerMain.js'; } };
            var s = document.createElement('script');
            s.src = basis + '/loader.js';
            s.onload = function () {
                window.require.config({ paths: { vs: basis } });
                window.require(['vs/editor/editor.main'], function () { ok(); }, fout);
            };
            s.onerror = fout;
            document.head.appendChild(s);
        });
        return laden;
    }

    return {
        maak: async function (element, tekst, dotnet, alleenLezen) {
            await laad();
            var editor = monaco.editor.create(element, {
                value: tekst, language: 'powershell', automaticLayout: true, minimap: { enabled: false },
                fontSize: 13, readOnly: !!alleenLezen, scrollBeyondLastLine: false, tabSize: 4, insertSpaces: true,
                renderWhitespace: 'selection', wordWrap: 'off'
            });
            var wacht = null;
            editor.onDidChangeModelContent(function () {
                clearTimeout(wacht);
                wacht = setTimeout(function () { dotnet.invokeMethodAsync('Changed', editor.getValue()); }, 250);
            });
            element._mossEditor = editor;
            return true;
        },
        zet: function (element, tekst) {
            var e = element && element._mossEditor;
            if (e && e.getValue() !== tekst) { e.setValue(tekst); }
        },
        markeer: function (element, fouten) {
            var e = element && element._mossEditor;
            if (!e) { return; }
            monaco.editor.setModelMarkers(e.getModel(), 'moss', (fouten || []).map(function (f) {
                return { startLineNumber: f.regel, endLineNumber: f.regel, startColumn: f.kolom || 1, endColumn: 1000, message: f.tekst, severity: 8 };
            }));
        },
        weg: function (element) {
            var e = element && element._mossEditor;
            if (e) { e.dispose(); element._mossEditor = null; }
        }
    };
})();
