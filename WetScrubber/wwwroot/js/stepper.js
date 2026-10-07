/**
 * stepper.js — Active step indicator for the Create Design form
 * Click a step to scroll to its section; scrolling updates the active step.
 */
(function () {
    'use strict';

    var steps = Array.prototype.slice.call(document.querySelectorAll('.steps-bar .step-item[data-target]'));
    if (!steps.length) return;

    var sections = steps.map(function (s) {
        return document.querySelector(s.getAttribute('data-target'));
    });

    var lockUntil = 0;

    function setActive(index) {
        steps.forEach(function (s, i) {
            s.classList.toggle('active', i === index);
            if (i === index) s.setAttribute('aria-current', 'step');
            else s.removeAttribute('aria-current');
        });
    }

    function goTo(index) {
        var target = sections[index];
        if (!target) return;
        setActive(index);
        lockUntil = Date.now() + 800;
        target.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }

    steps.forEach(function (step, i) {
        step.addEventListener('click', function () { goTo(i); });
        step.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                goTo(i);
            }
        });
    });

    function currentFromScroll() {
        var line = 160; // px from viewport top (below topbar + stepper)
        var idx = 0;
        sections.forEach(function (sec, i) {
            if (sec && sec.getBoundingClientRect().top <= line) idx = i;
        });
        if (window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 4) {
            idx = steps.length - 1;
        }
        return idx;
    }

    var ticking = false;
    function onScroll() {
        if (Date.now() < lockUntil || ticking) return;
        ticking = true;
        window.requestAnimationFrame(function () {
            setActive(currentFromScroll());
            ticking = false;
        });
    }

    window.addEventListener('scroll', onScroll, { passive: true });
    document.addEventListener('scroll', onScroll, { passive: true, capture: true });
    window.addEventListener('resize', onScroll);

    setActive(currentFromScroll());
})();
