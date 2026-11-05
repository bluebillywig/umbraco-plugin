/**
 * Blue Billywig - call to action form overlay (frontend).
 *
 * Runs on pages that embed BB videos. Watches for timeline widgets with
 * the `bb-cta-form` CSS class and swaps their content for an email-capture
 * form. On submit:
 *   1. Stores `{email, submittedAt}` in localStorage under `bbtl_{clipId}`
 *      — the player uses the key to avoid re-showing the form to a visitor
 *      who already engaged. Same shape as the reference plugin. Deviation:
 *      an entry older than a year no longer counts and is removed when it is
 *      read (the reference keeps it for ever). Only the key's presence is
 *      used, so storing a bare flag instead of the email would not change
 *      what the player does; that is a one-line change in remember().
 *   2. If the widget carries `bb-cta-form-config-*` (base64url JSON with
 *      a `submitEndpoint`), POSTs `{email, clipId, consent, submittedAt}` as
 *      JSON to that URL. The customer implements the handler (CRM sync,
 *      email, logging).
 *
 * No CRM, no server-side lead collection on the BB side.
 *
 * Trust. The renderer prints a random per-request key on this script tag
 * (data-bb-key) and on the player container it makes (data-bb-key plus
 * data-bb-clip-id). Only a `.bb-cta-form` element inside such a container
 * becomes a form: a page, an editor or an embed body cannot turn an element
 * of its own into an email form that posts to an endpoint of its choosing.
 */
(function () {
    'use strict';

    var PREFIX = 'bbtl_';
    var SELECTOR = '.bb-cta-form';
    // How long "already submitted" is remembered.
    var REMEMBER_MS = 365 * 24 * 60 * 60 * 1000;

    var ownScript = document.currentScript;
    var KEY = (ownScript && ownScript.getAttribute('data-bb-key')) || '';
    // The visitor-facing defaults in the request's language, printed by the renderer on this script tag (data-bb-text, a JSON
    // object). Without it, or with a key it lacks, the English text below is used. Labels the editor typed always win.
    var TEXT = (function () {
        try {
            var parsed = JSON.parse((ownScript && ownScript.getAttribute('data-bb-text')) || '{}');
            return parsed && typeof parsed === 'object' ? parsed : {};
        } catch (e) { return {}; }
    })();
    function t(key, fallback) {
        return typeof TEXT[key] === 'string' && TEXT[key] ? TEXT[key] : fallback;
    }
    var CONTAINER = KEY ? '[data-bb-key="' + KEY.replace(/[^A-Za-z0-9_-]/g, '') + '"][data-bb-clip-id]' : '';

    function decodeConfig(el) {
        var m = el.className.match(/bb-cta-form-config-([A-Za-z0-9_-]+)/);
        if (!m) { return {}; }
        var pad = '='.repeat((4 - m[1].length % 4) % 4);
        try {
            var json = atob(m[1].replace(/-/g, '+').replace(/_/g, '/') + pad);
            var cfg = JSON.parse(json);
            // Defence-in-depth: re-validate URL fields at decode time so a
            // future code path that reads cfg without re-checking can't be
            // tricked into using javascript: / file: / non-local http: endpoints.
            if (cfg && typeof cfg === 'object') {
                if (cfg.submitEndpoint && !isEndpointUrl(cfg.submitEndpoint)) {
                    delete cfg.submitEndpoint;
                }
                if (cfg.privacyPolicyUrl && !isHttpUrl(cfg.privacyPolicyUrl)) {
                    delete cfg.privacyPolicyUrl;
                }
            }
            return cfg;
        } catch (e) {
            // Corrupt payload — mark the widget and log so it's visible in
            // ops instead of silently falling back to default labels.
            el.dataset.bbCtaFormError = 'config-decode-failed';
            if (window.console) {
                console.error('bb-cta-form: failed to decode config from class', m[0], e);
            }
            return null;
        }
    }

    // Only accept https URLs: http would send the visitor's email in cleartext,
    // and javascript:, data:, file: and the like must never be followed.
    // Deviation from the reference plugin: a submit endpoint on "localhost" (or
    // a *.localhost name, 127.0.0.1, [::1]) may be plain http, but only when
    // the page itself is on such a host too. That traffic never leaves the
    // visitor's machine, which is what lets an editor test a form endpoint
    // locally; a production page can never be made to post to it.
    function isHttpUrl(u) {
        try {
            var parsed = new URL(u);
            return parsed.protocol === 'https:' && !parsed.username && !parsed.password;
        } catch (_) { return false; }
    }

    function isLocalHost(host) {
        host = String(host || '').toLowerCase();
        return host === 'localhost' || /\.localhost$/.test(host) || host === '127.0.0.1' || host === '[::1]' || host === '::1';
    }

    function isEndpointUrl(u, pageHost) {
        if (isHttpUrl(u)) { return true; }
        try {
            var parsed = new URL(u);
            var page = pageHost === undefined ? window.location.hostname : pageHost;
            return parsed.protocol === 'http:' && !parsed.username && !parsed.password && isLocalHost(parsed.hostname) && isLocalHost(page);
        } catch (_) { return false; }
    }

    // The player container the renderer made. Its clip id is the only one trusted.
    function findContainer(el) {
        return CONTAINER ? el.closest(CONTAINER) : null;
    }

    function findClipId(el) {
        var host = findContainer(el);
        return host ? (host.getAttribute('data-bb-clip-id') || '') : '';
    }

    // Only the key's presence matters to the player. An entry older than a year is dropped, so "already submitted" does
    // not last for ever.
    function alreadySubmitted(clipId) {
        if (!clipId) { return false; }
        try {
            var raw = localStorage.getItem(PREFIX + clipId);
            if (!raw) { return false; }
            var entry;
            try { entry = JSON.parse(raw); } catch (e) { entry = null; }
            var when = entry && typeof entry === 'object' ? Date.parse(entry.submittedAt) : NaN;
            if (!isNaN(when) && Date.now() - when > REMEMBER_MS) {
                localStorage.removeItem(PREFIX + clipId);
                return false;
            }
            return true;
        }
        catch (e) { return false; }
    }

    function remember(clipId, email) {
        if (!clipId) { return; }
        try {
            localStorage.setItem(PREFIX + clipId, JSON.stringify({
                email: email,
                submittedAt: new Date().toISOString()
            }));
        } catch (e) { /* private-mode browsers — ignore */ }
    }

    function renderForm(el, cfg, clipId) {
        // Stable IDs for label / error-message association. Generated per
        // widget instance so multiple forms on a page don't conflict.
        var uid = 'bb-cta-form-' + Math.random().toString(36).slice(2, 10);
        var emailId = uid + '-email';
        var consentId = uid + '-consent';
        var errorId = uid + '-error';

        var heading = document.createElement('h3');
        heading.className = 'bb-cta-form__heading';
        heading.textContent = cfg.heading || t('ctaSubscribe', 'Subscribe');

        var emailLabel = document.createElement('label');
        emailLabel.className = 'bb-cta-form__email-label';
        emailLabel.htmlFor = emailId;
        emailLabel.textContent = cfg.emailLabel || t('ctaEmail', 'Email address');

        var email = document.createElement('input');
        email.type = 'email';
        email.id = emailId;
        email.required = true;
        email.placeholder = cfg.emailLabel || t('ctaEmail', 'Email address');
        email.className = 'bb-cta-form__email';
        email.autocomplete = 'email';
        email.setAttribute('aria-required', 'true');
        email.setAttribute('aria-describedby', errorId);

        // Consent checkbox + privacy policy link. Required under GDPR Art. 7
        // for any email capture that doesn't sit on another lawful basis.
        // The label + link are configurable so the customer's legal team
        // can word it to match their privacy notice.
        var consentWrap = document.createElement('label');
        consentWrap.className = 'bb-cta-form__consent';
        consentWrap.htmlFor = consentId;

        var consent = document.createElement('input');
        consent.type = 'checkbox';
        consent.id = consentId;
        consent.required = true;
        consent.className = 'bb-cta-form__consent-checkbox';
        consent.setAttribute('aria-required', 'true');

        var consentText = document.createElement('span');
        consentText.className = 'bb-cta-form__consent-text';
        consentText.textContent = cfg.consentLabel || t('ctaConsent', 'I agree to be contacted by email.');

        consentWrap.appendChild(consent);
        consentWrap.appendChild(consentText);
        if (cfg.privacyPolicyUrl && isHttpUrl(cfg.privacyPolicyUrl)) {
            var policyLink = document.createElement('a');
            policyLink.href = cfg.privacyPolicyUrl;
            policyLink.target = '_blank';
            policyLink.rel = 'noopener noreferrer';
            policyLink.className = 'bb-cta-form__privacy-link';
            policyLink.textContent = cfg.privacyPolicyLabel || t('ctaPrivacy', 'Privacy policy');
            consentWrap.appendChild(document.createTextNode(' '));
            consentWrap.appendChild(policyLink);
        }

        var submit = document.createElement('button');
        submit.type = 'submit';
        submit.className = 'bb-cta-form__submit';
        submit.textContent = cfg.submitLabel || t('ctaSubmit', 'Submit');

        var form = document.createElement('form');
        form.className = 'bb-cta-form__form';
        form.setAttribute('aria-label', cfg.ariaLabel || (cfg.heading || t('ctaSubscribe', 'Subscribe')));
        form.noValidate = false;
        form.appendChild(heading);
        form.appendChild(emailLabel);
        form.appendChild(email);
        form.appendChild(consentWrap);
        form.appendChild(submit);

        // Announce live errors to screen-readers without stealing focus.
        var errHost = document.createElement('div');
        errHost.id = errorId;
        errHost.setAttribute('role', 'alert');
        errHost.setAttribute('aria-live', 'polite');
        errHost.className = 'bb-cta-form__error-host';
        form.appendChild(errHost);

        el.innerHTML = '';
        el.appendChild(form);

        form.addEventListener('submit', function (e) {
            e.preventDefault();
            errHost.textContent = '';
            if (!email.value || !email.checkValidity()) {
                email.focus();
                errHost.textContent = t('ctaErrEmail', 'Please enter a valid email address.');
                return;
            }
            if (!consent.checked) {
                consent.focus();
                errHost.textContent = t('ctaErrConsent', 'Please accept the consent statement.');
                return;
            }
            submit.disabled = true;

            // Minimal payload — don't auto-leak the full page URL (which may
            // contain UTM/session tokens) to the customer's endpoint. Site
            // owners who want the referring URL can add `includePostUrl: true`
            // to their CTA config and acknowledge the disclosure explicitly.
            var payload = {
                email: email.value,
                clipId: clipId,
                consent: true,
                submittedAt: new Date().toISOString()
            };
            if (cfg.includePostUrl === true) {
                payload.postUrl = window.location.href;
            }

            var markDone = function () {
                remember(clipId, email.value);
                el.innerHTML = '<div class="bb-cta-form__thanks" role="status">' +
                    escapeHtml(cfg.thankYou || t('ctaThanks', 'Thanks!')) + '</div>';
            };
            var markFailed = function (reason) {
                submit.disabled = false;
                if (window.console) {
                    console.error('bb-cta-form: submit failed', reason);
                }
                errHost.textContent = t('ctaErrSubmit', 'Could not submit — please try again.');
            };

            if (cfg.submitEndpoint) {
                if (!isEndpointUrl(cfg.submitEndpoint)) {
                    // Bad endpoint baked into the saved config — don't fire
                    // the fetch, but still record so the visitor isn't
                    // re-gated by a broken CTA.
                    if (window.console) {
                        console.warn('bb-cta-form: refusing to POST to an endpoint that is not https (or http on a local page and host)', cfg.submitEndpoint);
                    }
                    markDone();
                    return;
                }
                fetch(cfg.submitEndpoint, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(payload),
                    mode: 'cors',
                    credentials: 'omit'
                }).then(function (r) {
                    if (!r.ok) { throw new Error('HTTP ' + r.status); }
                    markDone();
                }).catch(markFailed);
            } else {
                markDone();
            }
        });
    }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, function (c) {
            return ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' })[c];
        });
    }

    function upgrade(el) {
        if (el.dataset.bbCtaFormReady === '1') { return; }
        // Not inside a player container the renderer made for this page: not ours, leave it alone.
        if (!findContainer(el)) { return; }
        el.dataset.bbCtaFormReady = '1';
        var cfg = decodeConfig(el);
        if (!cfg) {
            // decodeConfig already logged — leave the widget alone so the
            // underlying textLabel stays visible to the viewer rather than
            // turning into a broken empty form.
            return;
        }
        var clipId = findClipId(el);
        if (alreadySubmitted(clipId)) {
            el.style.display = 'none';
            return;
        }
        renderForm(el, cfg, clipId);
    }

    function scan(root) {
        var els = (root || document).querySelectorAll(SELECTOR);
        for (var i = 0; i < els.length; i++) { upgrade(els[i]); }
    }

    // Timeline widgets are rendered lazily by the BB playout. Watch for them.
    var mo = new MutationObserver(function (mutations) {
        for (var i = 0; i < mutations.length; i++) {
            var added = mutations[i].addedNodes || [];
            for (var j = 0; j < added.length; j++) {
                if (added[j].nodeType === 1) {
                    if (added[j].matches && added[j].matches(SELECTOR)) {
                        upgrade(added[j]);
                    } else {
                        scan(added[j]);
                    }
                }
            }
        }
    });

    if (document.body) {
        mo.observe(document.body, { childList: true, subtree: true });
        scan(document);
    } else {
        document.addEventListener('DOMContentLoaded', function () {
            mo.observe(document.body, { childList: true, subtree: true });
            scan(document);
        });
    }
})();
