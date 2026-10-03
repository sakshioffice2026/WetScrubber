/**
 * auth.js  —  Login & Register page scripts (jQuery)
 * WetScrubber Pro | ASP.NET Core MVC
 *
 * Requires jQuery 3.x:
 *   <script src="https://code.jquery.com/jquery-3.7.1.min.js"></script>
 *   <script src="~/js/auth.js"></script>
 */

$(document).ready(function () {


    // ─────────────────────────────────────────────────────────────
    // 1. SHOW / HIDE PASSWORD TOGGLE
    //    Toggles between password and text on the eye icon click
    // ─────────────────────────────────────────────────────────────
    $('#togglePass').on('click', function () {
        var $input = $('#lp-pass');

        if ($input.attr('type') === 'password') {
            $input.attr('type', 'text');
            $(this).css('color', '#00d28c');
            $(this).attr('title', 'Hide password');
        } else {
            $input.attr('type', 'password');
            $(this).css('color', '#bbb');
            $(this).attr('title', 'Show password');
        }
    });

    // Also handle register page password toggle if present
    $('#togglePassReg').on('click', function () {
        var $input = $('#passwordInput');

        if ($input.attr('type') === 'password') {
            $input.attr('type', 'text');
            $(this).css('color', '#00d28c');
        } else {
            $input.attr('type', 'password');
            $(this).css('color', '#bbb');
        }
    });


    // ─────────────────────────────────────────────────────────────
    // 2. CLIENT-SIDE LOGIN VALIDATION
    //    Attached to FORM SUBMIT — not button click
    //    This ensures the form actually posts to the server
    // ─────────────────────────────────────────────────────────────
    $('#loginForm').on('submit', function (e) {
        var email    = $('#lp-email').val().trim();
        var pass     = $('#lp-pass').val();
        var $err     = $('#errBox');
        var $errText = $('#errText');

        // Clear previous errors
        $err.removeClass('show');
        $('#lp-email, #lp-pass').removeClass('is-invalid');

        var hasError = false;

        // Check empty fields
        if (!email) {
            $('#lp-email').addClass('is-invalid');
            hasError = true;
        }

        if (!pass) {
            $('#lp-pass').addClass('is-invalid');
            hasError = true;
        }

        if (hasError) {
            $errText.text('Please fill in all required fields.');
            $err.addClass('show');
            e.preventDefault();   // ← only stop submit when there ARE errors
            return;
        }

        // Basic email format check
        var emailReg = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
        if (!emailReg.test(email)) {
            $('#lp-email').addClass('is-invalid');
            $errText.text('Please enter a valid email address.');
            $err.addClass('show');
            e.preventDefault();   // ← only stop submit when invalid
            return;
        }

        // ✅ Validation passed — show loading state then LET FORM SUBMIT
        $('#lp-submit')
            .prop('disabled', true)
            .html('Signing in… <div class="lp-btn-arrow">↻</div>');

        // Form submits naturally to POST /Account/Login
    });


    // ─────────────────────────────────────────────────────────────
    // 3. CLEAR ERROR STATE ON INPUT
    //    Removes red border as soon as user starts typing
    // ─────────────────────────────────────────────────────────────
    $('#lp-email, #lp-pass').on('input', function () {
        $(this).removeClass('is-invalid');

        // Hide error box if both fields are being corrected
        if ($('.is-invalid').length === 0) {
            $('#errBox').removeClass('show');
        }
    });


    // ─────────────────────────────────────────────────────────────
    // 4. SERVER-SIDE ERROR — shake animation on page load
    //    Triggers if server returned validation errors
    // ─────────────────────────────────────────────────────────────
    if ($('.lp-error.show').length > 0 || $('.is-invalid').length > 0) {
        var $panel = $('.lp-right');
        $panel.css('animation', 'none');
        setTimeout(function () {
            $panel.css('animation', 'authShake 0.4s ease');
        }, 50);
    }

    // Inject shake keyframes once
    if ($('#authShakeStyle').length === 0) {
        $('head').append(
            '<style id="authShakeStyle">' +
            '@keyframes authShake {' +
            '0%,100% { transform: translateX(0); }' +
            '20%      { transform: translateX(-5px); }' +
            '40%      { transform: translateX(5px); }' +
            '60%      { transform: translateX(-3px); }' +
            '80%      { transform: translateX(3px); }' +
            '}' +
            '.field-wrap input.is-valid {' +
            '  border-color: #00d28c !important;' +
            '  box-shadow: 0 0 0 3px rgba(0,210,140,0.12) !important;' +
            '}' +
            '</style>'
        );
    }


    // ─────────────────────────────────────────────────────────────
    // 5. PASSWORD STRENGTH METER  (Register page)
    // ─────────────────────────────────────────────────────────────
    var $passwordInput = $('#passwordInput');
    var $strengthFill  = $('#strengthFill');

    if ($passwordInput.length && $strengthFill.length) {
        $passwordInput.on('input', function () {
            var v     = $(this).val();
            var score = 0;

            if (v.length >= 6)           score++;
            if (v.length >= 10)          score++;
            if (/[0-9]/.test(v))         score++;
            if (/[A-Z]/.test(v))         score++;
            if (/[^A-Za-z0-9]/.test(v))  score++;

            var pct    = (score / 5) * 100;
            var colors = ['#ef4444', '#f97316', '#f59e0b', '#84cc16', '#00d28c'];
            var labels = ['Very weak', 'Weak', 'Fair', 'Strong', 'Very strong'];

            $strengthFill.css({
                width:      pct + '%',
                background: colors[Math.max(0, score - 1)]
            });

            $strengthFill.attr('title', v.length > 0 ? labels[Math.max(0, score - 1)] : '');
        });
    }


    // ─────────────────────────────────────────────────────────────
    // 6. CONFIRM PASSWORD MATCH  (Register page)
    // ─────────────────────────────────────────────────────────────
    var $confirmInput = $('#ConfirmPassword');

    if ($passwordInput.length && $confirmInput.length) {
        function checkMatch() {
            var val  = $confirmInput.val();
            if (val.length === 0) {
                $confirmInput.removeClass('is-invalid is-valid');
                return;
            }
            if (val === $passwordInput.val()) {
                $confirmInput.removeClass('is-invalid').addClass('is-valid');
            } else {
                $confirmInput.removeClass('is-valid').addClass('is-invalid');
            }
        }

        $confirmInput.on('input', checkMatch);
        $passwordInput.on('input', checkMatch);
    }


    // ─────────────────────────────────────────────────────────────
    // 7. EMAIL FORMAT CHECK ON BLUR  (Login & Register)
    // ─────────────────────────────────────────────────────────────
    $('input[type="email"]').on('blur', function () {
        var val      = $(this).val().trim();
        var emailReg = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

        if (val.length === 0) {
            $(this).removeClass('is-invalid is-valid');
            return;
        }

        if (emailReg.test(val)) {
            $(this).removeClass('is-invalid').addClass('is-valid');
        } else {
            $(this).removeClass('is-valid').addClass('is-invalid');
        }
    });

    // Clear on focus
    $('input[type="email"]').on('focus', function () {
        $(this).removeClass('is-invalid is-valid');
    });


    // ─────────────────────────────────────────────────────────────
    // 8. AUTO-DISMISS FLASH TOAST  (TempData success messages)
    // ─────────────────────────────────────────────────────────────
    $('.flash-toast').each(function () {
        var $toast = $(this);
        setTimeout(function () {
            $toast.css({ transition: 'opacity 0.4s, transform 0.4s', opacity: 0, transform: 'translateY(10px)' });
            setTimeout(function () { $toast.remove(); }, 420);
        }, 4000);
    });


    // ─────────────────────────────────────────────────────────────
    // 9. PREVENT DOUBLE SUBMIT — Register form only
    //    Login form is handled by #loginForm submit above
    // ─────────────────────────────────────────────────────────────
    $('#registerForm').on('submit', function () {
        var $btn     = $(this).find('button[type="submit"]');
        var invalids = $(this).find('.is-invalid').length;

        if (invalids === 0 && $btn.length) {
            $btn.prop('disabled', true)
                .css('opacity', '0.7')
                .html('Creating account… <div class="lp-btn-arrow">↻</div>');
        }
    });

});
