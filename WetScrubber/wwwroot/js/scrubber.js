/**
 * scrubber.js  —  Scrubber Design form scripts (jQuery)
 * WetScrubber Pro | ASP.NET Core MVC
 */

$(document).ready(function () {


    // ─────────────────────────────────────────────────────────────
    // 1. SCRUBBER TYPE CARD SELECTOR
    //    Clicking a card sets the hidden radio input value
    // ─────────────────────────────────────────────────────────────
    $('.type-card').on('click', function () {
        $('.type-card').removeClass('selected');
        $(this).addClass('selected');

        var val = $(this).data('value');
        $('#ScrubberType').val(val);
    });

    // Mark selected on page load (e.g. after validation fail)
    var currentType = $('#ScrubberType').val();
    if (currentType) {
        $('.type-card[data-value="' + currentType + '"]').addClass('selected');
    }


    // ─────────────────────────────────────────────────────────────
    // 2. SHELL MATERIAL CARD SELECTOR
    // ─────────────────────────────────────────────────────────────
    $(document).on('click', '.shell-card', function () {
        $('.shell-card').removeClass('selected');
        $(this).addClass('selected');
        $('#ShellMaterial').val($(this).data('value'));
    });

    var currentShell = $('#ShellMaterial').val();
    if (currentShell) {
        $('.shell-card[data-value="' + currentShell + '"]').addClass('selected');
    }


    // ─────────────────────────────────────────────────────────────
    // 3. INTERNAL MATERIAL CARD SELECTOR
    // ─────────────────────────────────────────────────────────────
    $(document).on('click', '.internal-card', function () {
        $('.internal-card').removeClass('selected');
        $(this).addClass('selected');
        $('#InternalMaterial').val($(this).data('value'));
    });

    var currentInternal = $('#InternalMaterial').val();
    if (currentInternal) {
        $('.internal-card[data-value="' + currentInternal + '"]').addClass('selected');
    }


    // ─────────────────────────────────────────────────────────────
    // 4. AUTO-CALCULATE REMOVAL EFFICIENCY
    //    When inlet or outlet concentration changes,
    //    auto-compute removal % = (inlet - outlet) / inlet * 100
    // ─────────────────────────────────────────────────────────────
    $(document).on('input', '.inlet-conc, .outlet-conc', function () {
        var $row    = $(this).closest('.pollutant-row');
        var inlet   = parseFloat($row.find('.inlet-conc').val())  || 0;
        var outlet  = parseFloat($row.find('.outlet-conc').val()) || 0;

        if (inlet > 0 && outlet >= 0 && outlet < inlet) {
            var removal = ((inlet - outlet) / inlet * 100).toFixed(1);
            $row.find('.removal-eff').val(removal);
        }
    });

    // When removal % changes, auto-compute outlet concentration
    $(document).on('input', '.removal-eff', function () {
        var $row    = $(this).closest('.pollutant-row');
        var inlet   = parseFloat($row.find('.inlet-conc').val()) || 0;
        var removal = parseFloat($(this).val()) || 0;

        if (inlet > 0 && removal > 0 && removal < 100) {
            var outlet = (inlet * (1 - removal / 100)).toFixed(2);
            $row.find('.outlet-conc').val(outlet);
        }
    });


    // ─────────────────────────────────────────────────────────────
    // 5. ADD POLLUTANT ROW
    // ─────────────────────────────────────────────────────────────
    var pollutantIndex = $('.pollutant-row').length;

    $('#btnAddPollutant').on('click', function () {
        var template = $('#pollutantRowTemplate').html();

        // Replace INDEX placeholder with actual index
        var newRow = template.replace(/\[INDEX\]/g, '[' + pollutantIndex + ']');

        $('#pollutantContainer').append(newRow);
        pollutantIndex++;

        // Focus first input of new row
        $('#pollutantContainer .pollutant-row:last select').focus();
    });


    // ─────────────────────────────────────────────────────────────
    // 6. REMOVE POLLUTANT ROW
    //    At least 1 row must remain
    // ─────────────────────────────────────────────────────────────
    $(document).on('click', '.btn-remove-pollutant', function () {
        var rowCount = $('.pollutant-row').length;
        if (rowCount <= 1) {
            alert('At least one pollutant is required.');
            return;
        }
        $(this).closest('.pollutant-row').remove();

        // Re-index remaining rows so model binding works
        reindexPollutants();
    });

    function reindexPollutants() {
        $('.pollutant-row').each(function (i) {
            $(this).find('[name]').each(function () {
                var name = $(this).attr('name');
                // Replace Pollutants[N] with Pollutants[i]
                name = name.replace(/Pollutants\[\d+\]/, 'Pollutants[' + i + ']');
                $(this).attr('name', name);
            });
        });
        pollutantIndex = $('.pollutant-row').length;
    }


    // ─────────────────────────────────────────────────────────────
    // 7. FORM VALIDATION
    // ─────────────────────────────────────────────────────────────
    $('#designForm').on('submit', function (e) {
        var isValid = true;

        // Clear old errors
        $('.form-group input, .form-group select').removeClass('is-invalid');
        $('.field-error').text('');

        // Design name
        var $name = $('#DesignName');
        if (!$name.val().trim()) {
            $name.addClass('is-invalid');
            $name.closest('.form-group').find('.field-error').text('Design name is required.');
            isValid = false;
        }

        // Normal flow rate
        var $nfr = $('#NormalFlowRate');
        if (!$nfr.val() || parseFloat($nfr.val()) <= 0) {
            $nfr.addClass('is-invalid');
            $nfr.closest('.form-group').find('.field-error').text('Required — must be greater than 0.');
            isValid = false;
        }

        // Actual flow rate
        var $afr = $('#ActualFlowRate');
        if (!$afr.val() || parseFloat($afr.val()) <= 0) {
            $afr.addClass('is-invalid');
            $afr.closest('.form-group').find('.field-error').text('Required — must be greater than 0.');
            isValid = false;
        }

        // Inlet temperature
        var $temp = $('#InletTemperature');
        if ($temp.val() === '' || $temp.val() === null) {
            $temp.addClass('is-invalid');
            $temp.closest('.form-group').find('.field-error').text('Inlet temperature is required.');
            isValid = false;
        }

        // Each pollutant row must have inlet > 0
        $('.pollutant-row').each(function (i) {
            var $inlet = $(this).find('.inlet-conc');
            if (!$inlet.val() || parseFloat($inlet.val()) <= 0) {
                $inlet.addClass('is-invalid');
                isValid = false;
            }
        });

        if (!isValid) {
            e.preventDefault();
            // Scroll to first error
            var $first = $('.is-invalid').first();
            if ($first.length) {
                $('html, body').animate(
                    { scrollTop: $first.offset().top - 100 }, 250
                );
            }
            return;
        }

        // Disable submit to prevent double click
        $(this).find('button[type="submit"]')
               .prop('disabled', true)
               .html('<span>Saving design…</span>');
    });


    // ─────────────────────────────────────────────────────────────
    // 8. CLEAR INVALID STATE ON INPUT
    // ─────────────────────────────────────────────────────────────
    $(document).on('input change', '.form-group input, .form-group select', function () {
        $(this).removeClass('is-invalid');
        $(this).closest('.form-group').find('.field-error').text('');
    });


    // ─────────────────────────────────────────────────────────────
    // 9. AUTO-DISMISS FLASH TOAST
    // ─────────────────────────────────────────────────────────────
    $('.flash-toast').each(function () {
        var $t = $(this);
        setTimeout(function () {
            $t.css({ transition: 'opacity 0.4s, transform 0.4s', opacity: 0, transform: 'translateY(-8px)' });
            setTimeout(function () { $t.remove(); }, 420);
        }, 4000);
    });


    // ─────────────────────────────────────────────────────────────
    // 10. L/G RATIO HELPER — show recommended range based on type
    // ─────────────────────────────────────────────────────────────
    $('.type-card').on('click', function () {
        var type = $(this).data('value');
        var hint = '';

        if      (type === '1') hint = 'Packed Tower: recommended 2–10 L/m³';
        else if (type === '2') hint = 'Venturi: recommended 0.5–3 L/m³';
        else if (type === '3') hint = 'Spray Tower: recommended 1–3 L/m³';
        else                   hint = 'Typical range: 1–5 L/m³';

        $('#lgRatioHint').text(hint);
    });

});
