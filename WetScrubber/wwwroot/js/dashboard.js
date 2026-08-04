/**
 * dashboard.js  —  Dashboard page scripts (jQuery)
 * WetScrubber Pro | ASP.NET Core MVC
 *
 * Requires jQuery 3.x:
 *   <script src="https://code.jquery.com/jquery-3.7.1.min.js"></script>
 *   <script src="~/js/dashboard.js"></script>
 */

$(document).ready(function () {


    // ─────────────────────────────────────────────────────────────
    // 1. SIDEBAR TOGGLE — collapse / expand
    // ─────────────────────────────────────────────────────────────
    $('#sbToggle').on('click', function () {
        $('#sidebar').toggleClass('collapsed');
        $('#mainContent').toggleClass('expanded');

        // Save state in localStorage so it persists on page reload
        var isCollapsed = $('#sidebar').hasClass('collapsed');
        localStorage.setItem('sbCollapsed', isCollapsed);
    });

    // Restore sidebar state on page load
    if (localStorage.getItem('sbCollapsed') === 'true') {
        $('#sidebar').addClass('collapsed');
        $('#mainContent').addClass('expanded');
    }


    // ─────────────────────────────────────────────────────────────
    // 2. ACTIVE NAV ITEM — highlight current page
    // ─────────────────────────────────────────────────────────────
    var currentPath = window.location.pathname.toLowerCase();

    $('.sb-item').each(function () {
        var href = $(this).attr('href');
        if (href && currentPath.indexOf(href.toLowerCase()) !== -1) {
            $('.sb-item').removeClass('active');
            $(this).addClass('active');
        }
    });


    // ─────────────────────────────────────────────────────────────
    // 3. BAR CHART — designs per month
    //    Data is injected from the View via data attributes
    // ─────────────────────────────────────────────────────────────
    var $chart = $('#barChart');

    if ($chart.length) {
        var months = $chart.data('months')
            ? $chart.data('months').split(',')
            : ['Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr'];

        var values = $chart.data('values')
            ? $chart.data('values').toString().split(',').map(Number)
            : [3, 5, 4, 7, 6, 9];

        var maxVal = Math.max.apply(null, values);
        var maxHeight = 120; // px

        var html = '';
        $.each(values, function (i, val) {
            var barH    = maxVal > 0 ? Math.round((val / maxVal) * maxHeight) : 4;
            var isLast  = (i === values.length - 1);
            var active  = isLast ? ' active' : '';

            html += '<div class="bar-col">' +
                        '<div class="bar-rect' + active + '" style="height:' + barH + 'px" title="' + val + ' designs"></div>' +
                        '<div class="bar-month">' + months[i] + '</div>' +
                    '</div>';
        });

        $chart.html(html);

        // Hover highlight
        $chart.on('mouseenter', '.bar-rect', function () {
            $(this).css('background', '#00d28c');
        }).on('mouseleave', '.bar-rect', function () {
            if (!$(this).hasClass('active')) {
                $(this).css('background', '#e8f8f1');
            }
        });
    }


    // ─────────────────────────────────────────────────────────────
    // 4. AUTO-DISMISS FLASH TOAST (TempData success messages)
    // ─────────────────────────────────────────────────────────────
    var $toast = $('.flash-toast');

    if ($toast.length) {
        setTimeout(function () {
            $toast.css({
                transition: 'opacity 0.4s ease, transform 0.4s ease',
                opacity: '0',
                transform: 'translateY(10px)'
            });
            setTimeout(function () { $toast.remove(); }, 420);
        }, 4000);
    }


    // ─────────────────────────────────────────────────────────────
    // 5. TABLE ROW CLICK — navigate to project detail
    // ─────────────────────────────────────────────────────────────
    $('.projects-table tbody tr[data-href]').on('click', function () {
        window.location.href = $(this).data('href');
    }).css('cursor', 'pointer');


    // ─────────────────────────────────────────────────────────────
    // 6. LOGOUT CONFIRM
    // ─────────────────────────────────────────────────────────────
    $('#logoutBtn').on('click', function (e) {
        if (!confirm('Are you sure you want to sign out?')) {
            e.preventDefault();
        }
    });

});
