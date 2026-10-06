/*
 * Arranque de la aplicación: router por hash (#/ruta), navegación, sesión del personal y datos de contacto.
 * Con rutas por hash funcionan el botón "Atrás", F5 y los enlaces directos (ej. #/consultar?codigo=UTD-1001),
 * incluso abriendo index.html desde el disco o en cualquier hosting estático.
 */
(function (UTD) {
  'use strict';

  var h = UTD.utils.h;
  var $ = UTD.utils.$;
  var $$ = UTD.utils.$$;
  var ui = UTD.ui;

  UTD.views = UTD.views || {};
  UTD.views.home = UTD.views.home || { title: 'Inicio' };
  UTD.views.privacy = UTD.views.privacy || { title: 'Privacidad y condiciones' };
  UTD.views.notfound = UTD.views.notfound || { title: 'Página no encontrada' };

  var ROUTES = {
    '/': { view: 'home' },
    '/nuevo-ticket': { view: 'new-ticket' },
    '/consultar': { view: 'track' },
    '/privacidad': { view: 'privacy' },
    '/admin': { view: 'login', guestOnly: true },
    '/admin/panel': { view: 'dashboard', auth: true }
  };

  var current = null;

  function parseHash() {
    var raw = location.hash || '#/';
    if (raw.indexOf('#/') !== 0) return null; // anclas como #main no son rutas
    var rest = raw.slice(1);
    var q = rest.indexOf('?');
    var path = q === -1 ? rest : rest.slice(0, q);
    if (path.length > 1) path = path.replace(/\/+$/, '');
    var query = {};
    if (q !== -1) new URLSearchParams(rest.slice(q + 1)).forEach(function (v, k) { query[k] = v; });
    return { path: path, query: query };
  }

  function go(path) { location.hash = '#' + path; }

  function updateAuthUi() {
    var active = UTD.session.isActive();
    $('#navPanelItem').hidden = !active;
    $('#navLogoutItem').hidden = !active;
  }

  function render(initial) {
    var parsed = parseHash();
    if (!parsed) return;
    var route = ROUTES[parsed.path] || { view: 'notfound' };

    // Guardas de acceso
    if (route.auth && !UTD.session.isActive()) { location.replace('#/admin'); return; }
    if (route.guestOnly && UTD.session.isActive()) { location.replace('#/admin/panel'); return; }

    var name = route.view;
    if (current && current !== name && UTD.views[current] && UTD.views[current].leave) UTD.views[current].leave();

    $$('[data-view]').forEach(function (s) { s.hidden = s.getAttribute('data-view') !== name; });
    current = name;

    var section = $('[data-view="' + name + '"]');
    var title = section.getAttribute('data-title');
    document.title = (title ? title + ' · ' : '') + UTD.config.appName;

    $$('#navList [data-route]').forEach(function (a) {
      if (a.getAttribute('data-route') === parsed.path) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current');
    });
    updateAuthUi();

    var view = UTD.views[name];
    if (view && view.enter) view.enter(parsed.query);

    if (!initial) {
      window.scrollTo(0, 0);
      var heading = section.querySelector('h1');
      if (heading && name !== 'track') heading.focus(); // en "consultar" el foco va al campo que prepara la vista
    }
  }

  UTD.router = { go: go, render: render };

  // ---------- Detalles de la página ----------
  function hydrateIcons(root) {
    $$('[data-icon]', root).forEach(function (span) {
      var extra = [span.getAttribute('class'), span.getAttribute('data-icon-class')].filter(Boolean).join(' ');
      span.parentNode.replaceChild(UTD.icon(span.getAttribute('data-icon'), { class: extra }), span);
    });
  }

  function hydrateConfigText() {
    $$('[data-config]').forEach(function (el) {
      var val = el.getAttribute('data-config').split('.').reduce(function (o, k) { return o && o[k]; }, UTD.config);
      if (val !== undefined && val !== null) el.textContent = String(val);
    });
  }

  function formatWhatsapp(n) {
    var d = UTD.utils.digits(n);
    if (d.length === 11 && d.indexOf('505') === 0) return '+505 ' + d.slice(3, 7) + '-' + d.slice(7);
    return '+' + d;
  }

  function renderContact() {
    var c = UTD.config.contact;
    var ul = $('#contactList');
    UTD.utils.clear(ul);
    function item(icon, content) {
      ul.appendChild(h('li', { class: 'flex items-start gap-3' }, UTD.icon(icon, { class: 'mt-0.5 text-university-700' }), h('span', null, content)));
    }
    if (c.location) item('map-pin', c.location);
    if (c.hours) item('clock', c.hours);
    if (c.whatsapp) item('phone', 'WhatsApp: ' + formatWhatsapp(c.whatsapp));
    if (c.email) item('mail', h('a', { href: 'mailto:' + c.email, class: 'text-university-700 underline hover:no-underline' }, c.email));
    var wa = $('#homeWhatsapp');
    if (c.whatsapp) wa.setAttribute('href', UTD.utils.waLink(c.whatsapp, c.whatsappText)); else wa.hidden = true;
  }

  function setupDemoBanner() {
    if (!UTD.config.useMock) return;
    $('#demoBanner').hidden = false;
    $('#resetDemoBtn').addEventListener('click', async function () {
      var ok = await ui.confirm({
        title: '¿Restablecer los datos de ejemplo?',
        message: 'Se borrarán los tickets creados en este navegador y volverán los datos de ejemplo.',
        confirmText: 'Sí, restablecer', tone: 'danger'
      });
      if (!ok) return;
      UTD.mockApi.reset();
      UTD.session.end('logout');
      location.hash = '#/';
      location.reload();
    });
  }

  function start() {
    hydrateIcons(document);
    hydrateConfigText();
    renderContact();
    $('#year').textContent = String(new Date().getFullYear());
    ui.initDialogs();
    setupDemoBanner();

    $('#skipLink').addEventListener('click', function (e) {
      e.preventDefault();
      var main = $('#main');
      main.focus();
      main.scrollIntoView();
    });

    $('#logoutBtn').addEventListener('click', function () { UTD.session.end('logout'); });
    document.addEventListener('utd:session-ended', function (e) {
      if (UTD.admin) UTD.admin.closeDialogs();
      updateAuthUi();
      if (e.detail && e.detail.reason === 'expired') {
        ui.toast('Tu sesión expiró. Inicia sesión de nuevo.', 'info');
        go('/admin');
      } else {
        ui.toast('Cerraste sesión.', 'success', 3000);
        go('/');
      }
    });

    window.addEventListener('hashchange', function () { render(false); });
    render(true);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})(window.UTD = window.UTD || {});
