document.addEventListener("DOMContentLoaded", function () {
    var form = document.getElementById("recordOutcomeForm");
    if (!form) return;

    form.addEventListener("submit", async function (e) {
        e.preventDefault();

        var submitBtn = form.querySelector("button[type=submit]");
        if (submitBtn) submitBtn.disabled = true;

        var params = new URLSearchParams({
            designId: document.querySelector('input[name="designId"]').value,
            measuredRemovalEfficiency: document.querySelector('input[name="measuredRemovalEfficiency"]').value,
            measuredPressureDrop: document.querySelector('input[name="measuredPressureDrop"]').value || "",
            measuredGasFlowRate: document.querySelector('input[name="measuredGasFlowRate"]').value || "",
            measuredLiquidToGasRatio: document.querySelector('input[name="measuredLiquidToGasRatio"]').value || "",
            fieldNotes: document.querySelector('textarea[name="fieldNotes"]').value || ""
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
            alert("Network error while recording outcome: " + err.message);
        } finally {
            if (submitBtn) submitBtn.disabled = false;
        }
    });
});
