document.addEventListener("DOMContentLoaded", function () {
    var form = document.getElementById("recordOutcomeForm");
    if (!form) return;

    form.addEventListener("submit", async function (e) {
        e.preventDefault();

        var submitBtn = form.querySelector("button[type=submit]");
        if (submitBtn) submitBtn.disabled = true;

        var params = new URLSearchParams({
            designId: document.getElementById("outcomeDesignId").value,
            measuredRemovalEfficiency: document.getElementById("measuredRemovalEfficiency").value,
            measuredPressureDrop: document.getElementById("measuredPressureDrop").value || "",
            measuredGasFlowRate: document.getElementById("measuredGasFlowRate").value || "",
            measuredLiquidToGasRatio: document.getElementById("measuredLiquidToGasRatio").value || "",
            fieldNotes: document.getElementById("fieldNotes").value || ""
        });

        try {
            var res = await fetch("/Scrubber/RecordDesignOutcome?" + params.toString(), {
                method: "POST"
            });
            var data = await res.json();

            if (data.status === "recorded") {
                alert("Outcome recorded (ID " + data.outcomeId + ").");
                form.reset();
            } else if (data.status === "unauthorized") {
                alert("Session expired. Please log in again.");
            } else if (data.status === "unknown_design") {
                alert("Design geometry not found for this design.");
            } else {
                alert("Failed to record outcome: " + data.status);
            }
        } catch (err) {
            alert("Network error while recording outcome.");
        } finally {
            if (submitBtn) submitBtn.disabled = false;
        }
    });
});
