/**
 * chemistry.js  —  Chemical Reactions page scripts (jQuery)
 * WetScrubber Pro | ASP.NET Core MVC
 */

$(document).ready(function () {

    // ─────────────────────────────────────────────────────────────
    // 1. POLLUTANT TAB SWITCHING
    // ─────────────────────────────────────────────────────────────
    $('.tab-btn').on('click', function () {
        var panel = $(this).data('panel');

        // Update tabs
        $('.tab-btn').removeClass('active');
        $(this).addClass('active');

        // Show correct panel
        $('.reaction-panel').removeClass('show');
        $('#panel-' + panel).addClass('show');
    });


    // ─────────────────────────────────────────────────────────────
    // 2. REACTION CARD EXPAND / COLLAPSE
    // ─────────────────────────────────────────────────────────────
    $(document).on('click', '.rxn-header', function () {
        var $body    = $(this).next('.rxn-body');
        var $chevron = $(this).find('.rxn-chevron');
        var isOpen   = $body.hasClass('open');

        $body.toggleClass('open', !isOpen);
        $chevron.toggleClass('open', !isOpen);
    });


    // ─────────────────────────────────────────────────────────────
    // 3. HATTA NUMBER INTERACTIVE CALCULATOR
    // ─────────────────────────────────────────────────────────────
    function updateHatta() {
        var k = parseInt($('#sliderK').val());
        var c = parseInt($('#sliderC').val());

        $('#valK').text(k);
        $('#valC').text(c);

        // Ha = sqrt(k2 * Cb * DL) / kL
        // Simplified: Ha = sqrt(k * c / 100) for slider demo
        var Ha     = Math.sqrt(k * c / 100);
        var pct    = Math.min((Ha / 10) * 100, 100);

        $('#haVal').text(Ha.toFixed(2));
        $('#regimeFill').css('width', pct + '%');

        var name, desc, color, borderColor;

        if (Ha < 0.3) {
            name        = 'Slow reaction regime';
            desc        = 'Ha < 0.3 — The reaction is too slow to enhance absorption. ' +
                          'The scrubber behaves as if using pure physical absorption (plain water). ' +
                          'Enhancement factor E ≈ 1. ' +
                          'Action: increase reagent concentration or raise temperature.';
            color       = '#d97706';
            borderColor = '#f59e0b';

        } else if (Ha < 3) {
            name        = 'Intermediate regime';
            desc        = '0.3 < Ha < 3 — Partial chemical enhancement. ' +
                          'Enhancement factor E = Ha / tanh(Ha). ' +
                          'Both liquid flow rate AND reagent concentration affect performance. ' +
                          'This is the most common operating regime for SO₂ / HCl scrubbing.';
            color       = '#2563eb';
            borderColor = '#3b82f6';

        } else {
            name        = 'Fast reaction regime';
            desc        = 'Ha > 3 — Reaction occurs entirely within the liquid film. ' +
                          'Enhancement factor E ≈ Ha. ' +
                          'Increasing liquid flow rate has little benefit — the reaction is already instantaneous. ' +
                          'Focus on maintaining reagent concentration and good gas-liquid contact.';
            color       = '#059669';
            borderColor = '#00d28c';
        }

        $('#regimeName').text(name).css('color', color);
        $('#regimeDesc').text(desc);
        $('#regimeFill').css('background', borderColor);
        $('#haVal').css('color', color);
        $('.hatta-regime-box').css('border-left-color', borderColor);
    }

    // Bind sliders
    $('#sliderK, #sliderC').on('input', updateHatta);

    // Run on page load
    updateHatta();


    // ─────────────────────────────────────────────────────────────
    // 4. AUTO-OPEN FIRST REACTION CARD IN EACH PANEL
    // ─────────────────────────────────────────────────────────────
    $('.reaction-panel').each(function () {
        var $first = $(this).find('.rxn-header').first();
        if ($first.length) {
            $first.next('.rxn-body').addClass('open');
            $first.find('.rxn-chevron').addClass('open');
        }
    });

});
