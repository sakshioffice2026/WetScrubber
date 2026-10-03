/**
 * project.js  —  Project pages scripts (jQuery)
 * WetScrubber Pro | ASP.NET Core MVC
 *
 * Requires jQuery 3.x + dashboard.js already loaded
 */

$(document).ready(function () {


    // ─────────────────────────────────────────────────────────────
    // 1. TABLE ROW CLICK — navigate to project detail
    // ─────────────────────────────────────────────────────────────
    $('.proj-table tbody tr[data-href]').on('click', function (e) {
        // Don't navigate if user clicked an action button
        if ($(e.target).closest('.action-btns').length) return;
        window.location.href = $(this).data('href');
    });

    $('.designs-table tbody tr[data-href]').on('click', function (e) {
        // Don't navigate if the click landed on a row action (e.g. Edit)
        if ($(e.target).closest('.design-actions').length) return;
        window.location.href = $(this).data('href');
    });


    // ─────────────────────────────────────────────────────────────
    // 2. DELETE CONFIRM MODAL
    // ─────────────────────────────────────────────────────────────
    var deleteProjectId = null;
    var deleteProjectName = '';

    // Open modal
    $(document).on('click', '.btn-open-delete', function (e) {
        e.stopPropagation();   // prevent row click firing
        deleteProjectId = $(this).data('id');
        deleteProjectName = $(this).data('name');

        $('#deleteProjectName').text(deleteProjectName);
        $('#deleteModal').addClass('show');
    });

    // Cancel modal
    $('#btnModalCancel, #deleteModal').on('click', function (e) {
        if (e.target === this) {
            $('#deleteModal').removeClass('show');
            deleteProjectId = null;
        }
    });

    // Confirm delete — submit hidden form
    $('#btnModalDelete').on('click', function () {
        if (!deleteProjectId) return;
        $('#deleteProjectIdInput').val(deleteProjectId);
        $('#deleteForm').submit();
    });

    // ESC key closes modal
    $(document).on('keydown', function (e) {
        if (e.key === 'Escape') {
            $('#deleteModal').removeClass('show');
            deleteProjectId = null;
        }
    });


    // ─────────────────────────────────────────────────────────────
    // 3. LIVE SEARCH — filter table rows client-side
    //    Works alongside server-side search for instant feedback
    // ─────────────────────────────────────────────────────────────
    $('#liveSearch').on('input', function () {
        var term = $(this).val().toLowerCase().trim();

        $('.proj-table tbody tr').each(function () {
            var text = $(this).text().toLowerCase();
            $(this).toggle(term === '' || text.indexOf(term) !== -1);
        });

        // Update visible count
        var visible = $('.proj-table tbody tr:visible').length;
        $('#visibleCount').text(visible);
    });


    // ─────────────────────────────────────────────────────────────
    // 4. STATUS FILTER — filter via form submit
    // ─────────────────────────────────────────────────────────────
    $('#statusFilter').on('change', function () {
        $('#filterForm').submit();
    });


    // ─────────────────────────────────────────────────────────────
    // 5. FORM VALIDATION — Create / Edit project
    // ─────────────────────────────────────────────────────────────
    $('#projectForm').on('submit', function (e) {
        var isValid = true;
        var $projNum = $('#ProjectNumber');
        var $projName = $('#ProjectName');

        // Clear previous errors
        $('.form-group input, .form-group textarea').removeClass('is-invalid');
        $('.field-error').text('');

        // Project number required
        if (!$projNum.val().trim()) {
            $projNum.addClass('is-invalid');
            $projNum.closest('.form-group').find('.field-error')
                .text('Project number is required.');
            isValid = false;
        }

        // Project name required
        if (!$projName.val().trim()) {
            $projName.addClass('is-invalid');
            $projName.closest('.form-group').find('.field-error')
                .text('Project name is required.');
            isValid = false;
        }

        if (!isValid) {
            e.preventDefault();
            // Scroll to first error
            var $first = $('.is-invalid').first();
            if ($first.length) {
                $('html, body').animate({ scrollTop: $first.offset().top - 100 }, 200);
            }
            return;
        }

        // Disable submit to prevent double click
        $(this).find('button[type="submit"]')
            .prop('disabled', true)
            .html('<span>Saving…</span>');
    });


    // ─────────────────────────────────────────────────────────────
    // 6. CLEAR INVALID STATE ON INPUT
    // ─────────────────────────────────────────────────────────────
    $('.form-group input, .form-group select, .form-group textarea').on('input change', function () {
        $(this).removeClass('is-invalid');
        $(this).closest('.form-group').find('.field-error').text('');
    });


    // ─────────────────────────────────────────────────────────────
    // 7. AUTO-DISMISS FLASH TOAST
    // ─────────────────────────────────────────────────────────────
    $('.flash-toast').each(function () {
        var $toast = $(this);
        setTimeout(function () {
            $toast.css({ transition: 'opacity 0.4s, transform 0.4s', opacity: 0, transform: 'translateY(-8px)' });
            setTimeout(function () { $toast.remove(); }, 420);
        }, 4000);
    });


    // ─────────────────────────────────────────────────────────────
    // 8. PROJECT NUMBER AUTO-FORMAT
    //    Converts input to uppercase and replaces spaces with dashes
    //    e.g. "ws 2025 001" → "WS-2025-001"
    // ─────────────────────────────────────────────────────────────
    $('#ProjectNumber').on('input', function () {
        var val = $(this).val()
            .toUpperCase()
            .replace(/\s+/g, '-')
            .replace(/[^A-Z0-9\-]/g, '');
        $(this).val(val);
    });


});