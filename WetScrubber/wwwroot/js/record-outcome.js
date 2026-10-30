document.addEventListener("DOMContentLoaded", function () {
    var form = document.getElementById("recordOutcomeForm");
    if (!form) return;

    function notify(message, type) {
        if (window.WS && typeof WS.toast === "function") {
            WS.toast(message, type);
        } else {
            window.alert(message);
        }
    }

    function valueOf(selector) {
        var el = form.querySelector(selector);
        return el ? el.value : "";
    }

    form.addEventListener("submit", async function (e) {
        e.preventDefault();

        if (form.checkValidity && !form.checkValidity()) {
            if (form.reportValidity) form.reportValidity();
            notify("Please correct the highlighted fields.", "warning");
            return;
        }

        var submitBtn = form.querySelector("button[type=submit]");
        var originalLabel = submitBtn ? submitBtn.textContent : "";
        if (submitBtn) {
            submitBtn.disabled = true;
            submitBtn.textContent = "Saving…";
            submitBtn.setAttribute("aria-busy", "true");
        }

        var params = new URLSearchParams({
            designId: valueOf('input[name="designId"]'),
            measuredRemovalEfficiency: valueOf('input[name="measuredRemovalEfficiency"]'),
            measuredPressureDrop: valueOf('input[name="measuredPressureDrop"]'),
            measuredGasFlowRate: valueOf('input[name="measuredGasFlowRate"]'),
            measuredLiquidToGasRatio: valueOf('input[name="measuredLiquidToGasRatio"]'),
            fieldNotes: valueOf('textarea[name="fieldNotes"]')
        });

        try {
            var res = await fetch("/Scrubber/RecordDesignOutcome?" + params.toString(), {
                method: "POST"
            });

            var data;
            try {
                data = await res.json();
            } catch (parseErr) {
                notify("Unexpected response from the server (HTTP " + res.status + ").", "error");
                return;
            }

            if (data.status === "recorded") {
                notify("Outcome recorded successfully (ID " + data.outcomeId + ").", "success");
                form.reset();
            } else if (data.status === "unauthorized") {
                notify("Your session has expired. Please sign in again.", "warning");
            } else if (data.status === "unknown_design") {
                notify("Design geometry was not found for this design.", "error");
            } else {
                notify("Could not record the outcome: " + data.status, "error");
            }
        } catch (err) {
            notify("Network error while recording the outcome. Please check your connection and try again.", "error");
        } finally {
            if (submitBtn) {
                submitBtn.disabled = false;
                submitBtn.textContent = originalLabel;
                submitBtn.removeAttribute("aria-busy");
            }
        }
    });
});