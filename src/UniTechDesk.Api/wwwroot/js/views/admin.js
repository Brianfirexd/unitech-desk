/*
 * Vistas del personal: inicio de sesión y panel de tickets (búsqueda, filtros, paginación, exportación)
 * con el diálogo de gestión de cada ticket (estado, cotización, pago, asignación, notas e historial).
 * IMPORTANTE: todo lo que se pinta pasa por h() (textContent); nunca se arma HTML con datos del cliente.
 */
(function (UTD) {
  'use strict';

  var h = UTD.utils.h;
  var $ = UTD.utils.$;
  var $$ = UTD.utils.$$;
  var V = UTD.validation;
  var C = UTD.constants;
  var R = UTD.rules;
  var ui = UTD.ui;
  var U = UTD.utils;

  // =====================================================================
  //  INICIO DE SESIÓN
  // =====================================================================
  var login = { attempts: 0, lockTimer: null, initialized: false };

  function initLogin() {
    if (login.initialized) return;
    login.initialized = true;
    var form = $('#loginForm');
    var err = $('#loginError');
    var btn = $('#loginBtn');
    var pass = $('#loginPass');

    function showError(msg) {
      err.textContent = msg;
      err.hidden = false;
      form.classList.remove('shake');
      void form.offsetWidth;
      form.classList.add('shake');
    }

    function lock(seconds) {
      var left = seconds;
      btn.disabled = true;
      function tick() {
        if (left <= 0) {
          clearInterval(login.lockTimer);
          btn.disabled = false;
          UTD.utils.clear(btn);
          btn.appendChild(UTD.icon('log-in'));
          btn.appendChild(document.createTextNode(' Iniciar sesión'));
          login.attempts = 0;
          return;
        }
        UTD.utils.clear(btn);
        btn.appendChild(document.createTextNode('Espera ' + left + ' s para reintentar'));
        left--;
      }
      tick();
      login.lockTimer = setInterval(tick, 1000);
    }

    form.addEventListener('submit', async function (e) {
      e.preventDefault();
      if (btn.disabled) return;
      err.hidden = true;
      var user = $('#loginUser').value.trim();
      var password = pass.value;
      if (!user || !password) { showError('Escribe tu usuario y tu contraseña.'); return; }
      ui.setBusy(btn, true, 'Ingresando…');
      try {
        var res = await UTD.api.login(user, password);
        UTD.session.start(res);
        form.reset();
        login.attempts = 0;
        ui.setBusy(btn, false);
        UTD.router.go('/admin/panel');
      } catch (e2) {
        ui.setBusy(btn, false);
        login.attempts++;
        var info = ui.describeError(e2);
        // Mensaje genérico: no se revela si falló el usuario o la contraseña.
        // En 401 se muestra el mensaje del servidor (también explica el bloqueo temporal); si no viene, uno genérico.
        showError(e2 && e2.status === 401 ? (e2.message || 'Usuario o contraseña incorrectos.') : info.message);
        pass.value = '';
        pass.focus();
        // Solo mejora de experiencia: el límite real de intentos debe aplicarlo el backend.
        if (login.attempts >= UTD.config.login.maxAttempts) lock(UTD.config.login.lockSeconds);
      }
    });

    $('#togglePass').addEventListener('click', function (e) {
      var b = e.currentTarget;
      var show = pass.type === 'password';
      pass.type = show ? 'text' : 'password';
      b.setAttribute('aria-pressed', show ? 'true' : 'false');
      b.setAttribute('aria-label', show ? 'Ocultar contraseña' : 'Mostrar contraseña');
      UTD.utils.clear(b);
      b.appendChild(UTD.icon(show ? 'eye-off' : 'eye'));
    });
  }

  UTD.views = UTD.views || {};
  UTD.views.login = {
    title: 'Acceso del personal',
    enter: function () { initLogin(); $('#loginError').hidden = true; },
    leave: function () { $('#loginPass').value = ''; }
  };

  // =====================================================================
  //  PANEL DE TICKETS
  // =====================================================================
  var dash = {
    initialized: false,
    filters: { q: '', status: '', service: '', urgency: '', payment: '', sort: 'date_desc' },
    page: 1, total: 0, items: [], seq: 0, staff: null
  };
  var els = {};

  function detailText(t) {
    if (t.serviceType === 'HARDWARE' && t.hardware) {
      return (C.equipmentTypes[t.hardware.equipmentType] || '').replace(/ \(.*\)/, '').replace(' / portátil', '') + ' · ' + t.hardware.brand + ' ' + t.hardware.model;
    }
    if (t.serviceType === 'SOFTWARE' && t.software) return C.softwareKinds[t.software.kind] || '';
    return '';
  }

  function fillFilterSelects() {
    function fill(sel, allLabel, map, order) {
      UTD.utils.clear(sel);
      sel.appendChild(h('option', { value: '' }, allLabel));
      (order || Object.keys(map)).forEach(function (k) {
        var label = typeof map[k] === 'string' ? map[k] : map[k].label;
        sel.appendChild(h('option', { value: k }, label));
      });
    }
    fill($('#filterStatus'), 'Todos los estados', C.statuses, C.statusOrder);
    fill($('#filterService'), 'Todos los servicios', C.serviceTypes);
    fill($('#filterUrgency'), 'Todas las urgencias', C.urgencies);
    fill($('#filterPayment'), 'Todos los pagos', C.paymentStatuses);
  }

  function listParams(extra) {
    var f = dash.filters;
    return Object.assign({ q: f.q, status: f.status, service: f.service, urgency: f.urgency, payment: f.payment, sort: f.sort, page: dash.page, pageSize: UTD.config.limits.pageSize }, extra || {});
  }

  function setRegionState(kind, title, message, action) {
    var box = els.state;
    UTD.utils.clear(box);
    if (!kind) { box.classList.add('hidden'); box.classList.remove('flex'); els.body.hidden = false; return; }
    els.body.hidden = true;
    box.classList.remove('hidden');
    box.classList.add('flex');
    var icon = kind === 'loading' ? ui.spinner() : UTD.icon(kind === 'error' ? 'circle-alert' : 'inbox', { class: 'text-5xl text-slate-500' });
    if (kind === 'loading') icon.setAttribute('class', icon.getAttribute('class') + ' text-4xl text-slate-500');
    box.appendChild(icon);
    box.appendChild(h('p', { class: 'mt-3 text-lg font-semibold text-slate-800' }, title));
    if (message) box.appendChild(h('p', { class: 'mt-1 text-sm' }, message));
    if (action) box.appendChild(action);
  }

  async function loadList(opts) {
    opts = opts || {};
    var seq = ++dash.seq; // si el usuario cambia filtros rápido, se ignoran respuestas viejas
    var region = $('#ticketsRegion');
    region.setAttribute('aria-busy', 'true');
    if (!dash.items.length || opts.showLoader) setRegionState('loading', 'Cargando tickets…');
    try {
      var res = await UTD.api.listTickets(listParams());
      if (seq !== dash.seq) return;
      dash.items = res.items;
      dash.total = res.total;
      dash.page = res.page;
      renderRows();
      renderPagination();
    } catch (err) {
      if (seq !== dash.seq) return;
      if (err && err.status === 401) return; // la sesión terminó: app.js redirige al login
      dash.items = [];
      var retry = h('button', { type: 'button', class: 'btn btn-secondary mt-4', onclick: function () { loadList({ showLoader: true }); } }, UTD.icon('refresh-cw'), 'Reintentar');
      setRegionState('error', 'No se pudieron cargar los tickets', ui.describeError(err).message, retry);
      renderPagination();
    } finally {
      if (seq === dash.seq) region.removeAttribute('aria-busy');
    }
  }

  async function loadStats() {
    try {
      var s = await UTD.api.getStats();
      var cards = [
        { icon: 'inbox', label: 'Abiertos', value: s.open, tone: 'text-yellow-800 bg-yellow-100' },
        { icon: 'clock', label: 'En curso', value: s.active, tone: 'text-blue-800 bg-blue-100' },
        { icon: 'banknote', label: 'Pagos pendientes', value: s.pendingPayment, tone: 'text-amber-800 bg-amber-100' },
        { icon: 'flag', label: 'Urgentes activos', value: s.urgentActive, tone: 'text-red-800 bg-red-100' }
      ];
      var grid = $('#statsGrid');
      UTD.utils.clear(grid);
      cards.forEach(function (c) {
        grid.appendChild(h('div', { class: 'card flex items-center gap-4 p-4' },
          h('span', { class: 'flex h-11 w-11 shrink-0 items-center justify-center rounded-full text-xl ' + c.tone }, UTD.icon(c.icon)),
          h('div', null, h('p', { class: 'text-2xl font-extrabold text-slate-900' }, String(c.value)), h('p', { class: 'text-sm text-slate-700' }, c.label))));
      });
    } catch (err) { /* los contadores son accesorios: si fallan, la lista sigue funcionando */ }
  }

  function td(label, children) {
    // El contenedor interno permite que en móvil (tarjetas) el valor ocupe la columna derecha sin partirse en varias.
    return h('td', { class: 'px-5 py-4 align-top', 'data-label': label }, h('div', { class: 'td-value' }, children));
  }

  function renderRows() {
    UTD.utils.clear(els.body);
    if (!dash.items.length) {
      var hasFilters = Object.keys(dash.filters).some(function (k) { return k !== 'sort' && dash.filters[k]; });
      setRegionState('empty', 'No hay tickets para mostrar', hasFilters ? 'Prueba quitando algunos filtros.' : 'Cuando lleguen solicitudes aparecerán aquí.',
        hasFilters ? h('button', { type: 'button', class: 'btn btn-secondary mt-4', onclick: clearFilters }, 'Quitar filtros') : null);
      return;
    }
    setRegionState(null);
    dash.items.forEach(function (t) {
      var who = t.requesterType === 'STUDENT' ? 'Carnet ' + t.studentId : t.company;
      var tr = h('tr', { class: 'hover:bg-slate-50' },
        td('Código', [
          h('div', { class: 'font-semibold text-university-800' }, t.code),
          h('div', { class: 'mt-0.5 text-xs text-slate-600' }, U.formatDateTime(t.createdAt))]),
        td('Solicitante', [
          h('div', { class: 'font-medium text-slate-900' }, t.fullName),
          h('div', { class: 'mt-0.5 flex flex-wrap items-center gap-1.5 text-xs text-slate-700' },
            h('span', { class: 'rounded bg-slate-100 px-1.5 py-0.5 text-slate-800' }, C.userTypes[t.requesterType] || t.requesterType),
            h('span', null, who))]),
        td('Servicio', [
          ui.serviceTag(t.serviceType),
          h('div', { class: 'mt-0.5 text-xs font-medium text-slate-700' }, detailText(t)),
          h('div', { class: 'mt-0.5 line-clamp-2 max-w-xs text-xs text-slate-600', title: t.description }, t.description)]),
        td('Urgencia / pago', h('div', { class: 'flex flex-col items-start gap-1.5' }, ui.urgencyBadge(t.urgency), ui.paymentBadge(t.paymentStatus))),
        td('Estado', h('div', { class: 'flex flex-col items-start gap-1.5' },
          ui.statusBadge(t.status),
          h('span', { class: 'text-xs text-slate-700' }, t.assignee ? 'Asignado: ' + t.assignee.name : 'Sin asignar'))),
        td('Acción', h('button', {
          type: 'button', class: 'btn btn-secondary btn-sm', 'aria-label': 'Gestionar ticket ' + t.code,
          onclick: function () { openTicket(t.id); }
        }, UTD.icon('eye'), 'Gestionar')));
      tr.lastChild.classList.add('text-center');
      els.body.appendChild(tr);
    });
  }

  function renderPagination() {
    var size = UTD.config.limits.pageSize;
    var pages = Math.max(1, Math.ceil(dash.total / size));
    var from = dash.total ? (dash.page - 1) * size + 1 : 0;
    var to = Math.min(dash.page * size, dash.total);
    $('#pageInfo').textContent = dash.total ? 'Mostrando ' + from + '–' + to + ' de ' + dash.total + ' tickets' : 'Sin resultados';
    $('#pageNum').textContent = 'Página ' + dash.page + ' de ' + pages;
    $('#prevPage').disabled = dash.page <= 1;
    $('#nextPage').disabled = dash.page >= pages;
  }

  function clearFilters() {
    dash.filters = { q: '', status: '', service: '', urgency: '', payment: '', sort: 'date_desc' };
    $('#filterQ').value = '';
    $('#filterStatus').value = '';
    $('#filterService').value = '';
    $('#filterUrgency').value = '';
    $('#filterPayment').value = '';
    $('#filterSort').value = 'date_desc';
    dash.page = 1;
    loadList({ showLoader: true });
  }

  async function exportCsv(btn) {
    ui.setBusy(btn, true, 'Exportando…');
    try {
      var res = await UTD.api.listTickets(listParams({ page: 1, pageSize: 1000 }));
      var cols = [
        { header: 'Código', value: function (t) { return t.code; } },
        { header: 'Fecha', value: function (t) { return U.formatDateTime(t.createdAt); } },
        { header: 'Tipo de solicitante', value: function (t) { return C.userTypes[t.requesterType]; } },
        { header: 'Nombre', value: function (t) { return t.fullName; } },
        { header: 'Correo', value: function (t) { return t.email; } },
        { header: 'Teléfono', value: function (t) { return t.phone; } },
        { header: 'Carnet / RUC', value: function (t) { return t.requesterType === 'STUDENT' ? t.studentId : t.ruc; } },
        { header: 'Empresa', value: function (t) { return t.company; } },
        { header: 'Carrera', value: function (t) { return t.majorName; } },
        { header: 'Recinto', value: function (t) { return t.campusName; } },
        { header: 'Servicio', value: function (t) { return C.serviceTypes[t.serviceType]; } },
        { header: 'Detalle', value: detailText },
        { header: 'Descripción', value: function (t) { return t.description; } },
        { header: 'Urgencia', value: function (t) { return C.urgencies[t.urgency]; } },
        { header: 'Estado', value: function (t) { return R.statusLabel(t.status); } },
        { header: 'Pago', value: function (t) { return C.paymentStatuses[t.paymentStatus]; } },
        { header: 'Monto cotizado', value: function (t) { return t.quote ? t.quote.amount : ''; } },
        { header: 'Moneda', value: function (t) { return t.quote ? t.quote.currency : ''; } },
        { header: 'Asignado a', value: function (t) { return t.assignee ? t.assignee.name : ''; } }
      ];
      U.downloadText('tickets-' + U.todayYmd() + '.csv', U.toCsv(res.items, cols));
      ui.toast(res.total > res.items.length
        ? 'Se exportaron los primeros ' + res.items.length + ' de ' + res.total + ' tickets.'
        : 'Se exportaron ' + res.items.length + ' tickets.', 'success');
    } catch (err) {
      ui.toast(ui.describeError(err).message, 'error');
    } finally {
      ui.setBusy(btn, false);
    }
  }

  function initDashboard() {
    if (dash.initialized) return;
    dash.initialized = true;
    els.body = $('#ticketsBody');
    els.state = $('#ticketsState');
    fillFilterSelects();

    $('#filtersForm').addEventListener('submit', function (e) { e.preventDefault(); });
    $('#filterQ').addEventListener('input', U.debounce(function (e) {
      dash.filters.q = e.target.value.trim();
      dash.page = 1;
      loadList();
    }, 300));
    [['filterStatus', 'status'], ['filterService', 'service'], ['filterUrgency', 'urgency'], ['filterPayment', 'payment'], ['filterSort', 'sort']].forEach(function (p) {
      $('#' + p[0]).addEventListener('change', function (e) { dash.filters[p[1]] = e.target.value; dash.page = 1; loadList(); });
    });
    $('#clearFiltersBtn').addEventListener('click', clearFilters);
    $('#prevPage').addEventListener('click', function () { if (dash.page > 1) { dash.page--; loadList(); } });
    $('#nextPage').addEventListener('click', function () { dash.page++; loadList(); });
    $('#refreshBtn').addEventListener('click', function () { loadStats(); loadList({ showLoader: true }); });
    $('#exportBtn').addEventListener('click', function (e) { exportCsv(e.currentTarget); });
  }

  UTD.views.dashboard = {
    title: 'Panel de tickets',
    enter: function () {
      initDashboard();
      loadStats();
      loadList({ showLoader: true });
    },
    leave: function () {
      // Al salir del panel se borran de pantalla y de memoria los datos de clientes.
      closeTicketDialog();
      dash.items = [];
      dash.staff = null;
      dash.seq++;
      if (els.body) UTD.utils.clear(els.body);
      UTD.utils.clear($('#statsGrid'));
      UTD.utils.clear($('#ticketPanel'));
    }
  };

  // =====================================================================
  //  DIÁLOGO DE GESTIÓN DE UN TICKET
  // =====================================================================
  var openTicketId = null; // ticket que muestra el diálogo (para refrescarlo si hay un conflicto)
  function dlg() { return $('#ticketDialog'); }
  function closeTicketDialog() { ui.closeDialog(dlg()); }

  async function openTicket(id) {
    var panel = $('#ticketPanel');
    UTD.utils.clear(panel);
    panel.appendChild(h('div', { class: 'flex items-center justify-center gap-3 p-16 text-slate-700' }, ui.spinner(), 'Cargando ticket…'));
    ui.openDialog(dlg());
    try {
      var results = await Promise.all([UTD.api.getTicket(id), dash.staff ? Promise.resolve(dash.staff) : UTD.api.listStaff()]);
      dash.staff = results[1];
      renderTicket(results[0]);
    } catch (err) {
      UTD.utils.clear(panel);
      panel.appendChild(h('div', { class: 'p-6' },
        ui.notice('error', 'No se pudo abrir el ticket', ui.describeError(err).message),
        h('div', { class: 'mt-4 text-right' }, h('button', { type: 'button', class: 'btn btn-secondary', onclick: closeTicketDialog }, 'Cerrar'))));
    }
  }

  function kv(label, value) {
    if (value === null || value === undefined || value === '' || value === false) return null;
    return h('div', { class: 'flex flex-col gap-0.5 sm:flex-row sm:gap-3' },
      h('dt', { class: 'shrink-0 text-xs font-semibold uppercase tracking-wide text-slate-600 sm:w-32 sm:pt-0.5' }, label),
      h('dd', { class: 'min-w-0 break-words text-sm text-slate-900' }, value));
  }
  function dl(rows) { return h('dl', { class: 'space-y-2' }, rows); }

  function block(id, iconName, title, children) {
    return h('section', { class: 'rounded-xl border border-slate-200 bg-white p-4', 'aria-labelledby': id },
      h('h3', { id: id, tabindex: '-1', class: 'mb-3 flex items-center gap-2 text-sm font-bold text-slate-900' }, UTD.icon(iconName, { class: 'text-university-700' }), title),
      children);
  }

  function makeSelect(id, entries, current, placeholder) {
    var sel = h('select', { id: id, class: 'input' });
    if (placeholder !== null) sel.appendChild(h('option', { value: '' }, placeholder));
    entries.forEach(function (e) { sel.appendChild(h('option', { value: e.value, disabled: e.disabled || null }, e.label)); });
    sel.value = current === null || current === undefined ? '' : String(current);
    return sel;
  }

  function fieldWrap(id, labelText, control, hint) {
    return h('div', { class: 'field' },
      h('label', { class: 'label', for: id }, labelText),
      control,
      hint ? h('p', { class: 'hint', id: id + '-hint' }, hint) : null);
  }

  /** Ejecuta una acción del personal: muestra carga, actualiza el ticket, la lista y avisa el resultado. */
  async function act(btn, busyLabel, fn, successMsg, focusId, onFieldErrors) {
    var body = $('.dialog-body', dlg());
    var scroll = body ? body.scrollTop : 0;
    ui.setBusy(btn, true, busyLabel);
    try {
      var updated = await fn();
      ui.toast(successMsg, 'success');
      renderTicket(updated, focusId, scroll);
      loadList();
      loadStats();
    } catch (err) {
      ui.setBusy(btn, false);
      var info = ui.describeError(err);
      if (info.fieldErrors && onFieldErrors && onFieldErrors(info.fieldErrors)) return;
      ui.toast(info.message, 'error');
      // Conflicto (alguien más cambió el ticket o el paso ya no aplica): se recargan los datos para no seguir con una vista vieja.
      if (err && err.status === 409 && openTicketId !== null) {
        try { renderTicket(await UTD.api.getTicket(openTicketId), focusId, scroll); loadList(); loadStats(); } catch (e3) { /* el aviso ya se mostró */ }
      }
    }
  }

  function renderTicket(t, focusId, scrollTop) {
    openTicketId = t.id;
    var panel = $('#ticketPanel');
    UTD.utils.clear(panel);

    // ----- Encabezado -----
    panel.appendChild(h('div', { class: 'dialog-head' },
      h('div', null,
        h('h2', { id: 'ticketDialogTitle', tabindex: '-1', class: 'text-xl font-bold text-slate-900' }, 'Ticket ' + t.code),
        h('div', { class: 'mt-2 flex flex-wrap items-center gap-2' }, ui.statusBadge(t.status), ui.paymentBadge(t.paymentStatus), ui.urgencyBadge(t.urgency))),
      h('button', { type: 'button', class: 'btn-icon', 'aria-label': 'Cerrar', onclick: closeTicketDialog }, UTD.icon('x', { class: 'text-xl' }))));

    var body = h('div', { class: 'dialog-body space-y-5' });
    panel.appendChild(body);

    // ----- Cliente y servicio -----
    var contactLinks = h('div', { class: 'flex flex-wrap items-center gap-x-4 gap-y-1' },
      h('a', { href: 'mailto:' + encodeURIComponent(t.email).replace('%40', '@'), class: 'inline-flex items-center gap-1.5 text-university-700 underline hover:no-underline' }, UTD.icon('mail'), t.email),
      h('a', { href: 'tel:' + t.phone.replace(/[^\d+]/g, ''), class: 'inline-flex items-center gap-1.5 text-university-700 underline hover:no-underline' }, UTD.icon('phone'), t.phone),
      h('a', { href: U.waLink(U.toIntlPhone(t.phone)), target: '_blank', rel: 'noopener noreferrer', class: 'inline-flex items-center gap-1.5 text-green-800 underline hover:no-underline' },
        UTD.icon('whatsapp'), 'WhatsApp', h('span', { class: 'sr-only' }, ' (se abre en una pestaña nueva)')));

    var client = block('td-client', 'user', 'Cliente', dl([
      kv('Nombre', h('span', { class: 'text-base font-semibold' }, t.fullName)),
      kv('Tipo', C.userTypes[t.requesterType]),
      kv('Contacto', contactLinks),
      t.requesterType === 'STUDENT' ? kv('Carnet', t.studentId) : null,
      t.requesterType === 'STUDENT' ? kv('Carrera', t.majorName) : null,
      t.requesterType === 'STUDENT' ? kv('Recinto', t.campusName) : null,
      t.requesterType === 'EXTERNAL' ? kv('Razón social', t.company) : null,
      t.requesterType === 'EXTERNAL' ? kv('RUC', t.ruc) : null
    ]));

    var svcRows = [kv('Servicio', ui.serviceTag(t.serviceType)), kv('Creado', U.formatDateTime(t.createdAt)), kv('Actualizado', U.formatDateTime(t.updatedAt)),
      kv('Pago preferido', C.paymentMethods[t.preferredPaymentMethod])];
    if (t.hardware) {
      var hw = t.hardware;
      svcRows.push(kv('Equipo', C.equipmentTypes[hw.equipmentType]), kv('Marca / modelo', hw.brand + ' ' + hw.model), kv('N.º de serie', hw.serial),
        kv('¿Enciende?', C.powersOn[hw.powersOn]),
        kv('Accesorios', (hw.accessories || []).map(function (a) { return C.accessories[a]; }).concat(hw.accessoriesOther ? [hw.accessoriesOther] : []).join(', ')));
    }
    if (t.software) {
      var sw = t.software;
      var link = sw.referenceUrl && U.safeWebUrl(sw.referenceUrl)
        ? h('a', { href: sw.referenceUrl, target: '_blank', rel: 'noopener noreferrer', class: 'break-all text-university-700 underline hover:no-underline' }, sw.referenceUrl, h('span', { class: 'sr-only' }, ' (se abre en una pestaña nueva)'))
        : (sw.referenceUrl || '');
      svcRows.push(kv('Tipo de trabajo', C.softwareKinds[sw.kind]), kv('Fecha deseada', sw.desiredDate ? U.formatDateOnly(sw.desiredDate) : ''), kv('Referencia', link));
    }
    body.appendChild(h('div', { class: 'grid gap-4 md:grid-cols-2' }, client, block('td-service', 'wrench', 'Servicio', dl(svcRows))));

    // ----- Descripción y adjuntos -----
    body.appendChild(block('td-desc', 'file-text', 'Descripción', h('p', { class: 'whitespace-pre-wrap rounded-lg bg-slate-50 p-3 text-sm text-slate-800' }, t.description)));
    if (t.attachments && t.attachments.length) {
      var files = h('ul', { class: 'space-y-2' });
      t.attachments.forEach(function (a) {
        var name = a.url
          ? h('a', { href: a.url, target: '_blank', rel: 'noopener noreferrer', class: 'text-university-700 underline hover:no-underline' }, a.name, h('span', { class: 'sr-only' }, ' (se abre en una pestaña nueva)'))
          : h('span', null, a.name);
        files.appendChild(h('li', { class: 'flex items-center gap-2 text-sm text-slate-800' }, UTD.icon('paperclip', { class: 'text-slate-600' }), name,
          h('span', { class: 'text-xs text-slate-600' }, '(' + U.formatBytes(a.size) + ')')));
      });
      body.appendChild(block('td-files', 'paperclip', 'Adjuntos', files));
    }

    // ----- Gestión -----
    body.appendChild(h('div', { class: 'grid gap-4 md:grid-cols-2' },
      renderStatusBlock(t), renderQuoteBlock(t), renderPaymentBlock(t), renderAssignBlock(t)));
    body.appendChild(renderNotesBlock(t));
    body.appendChild(renderHistory(t));

    panel.appendChild(h('div', { class: 'dialog-foot' },
      h('button', { type: 'button', class: 'btn btn-secondary', onclick: closeTicketDialog }, 'Cerrar')));

    if (typeof scrollTop === 'number') body.scrollTop = scrollTop;
    var target = focusId && document.getElementById(focusId);
    if (target) target.focus({ preventScroll: true }); else if (typeof scrollTop !== 'number') $('#ticketDialogTitle').focus();
  }

  // ----- Estado -----
  function renderStatusBlock(t) {
    var allowed = R.allowedTransitions(t);
    if (!allowed.length) {
      return block('td-status-h', 'list-checks', 'Estado', h('p', { class: 'text-sm text-slate-700' }, 'Este ticket está cerrado: no admite más cambios de estado.'));
    }
    var entries = [];
    var blocked = [];
    allowed.forEach(function (to) {
      var why = R.transitionBlocker(t, to);
      entries.push({ value: to, label: R.statusLabel(to) + (why ? ' (no disponible)' : ''), disabled: !!why });
      if (why) blocked.push({ to: to, why: why });
    });
    var sel = makeSelect('td-status', entries, '', 'Selecciona el nuevo estado…');
    sel.setAttribute('data-rules', 'required');
    sel.setAttribute('data-label', 'Nuevo estado');
    var note = h('textarea', { id: 'td-status-note', class: 'input', rows: '3', maxlength: '500', 'data-label': 'Mensaje' });
    var noteWrap = fieldWrap('td-status-note', 'Mensaje para el cliente (opcional)', note, 'El cliente lo verá en la consulta de su ticket.');
    var noteLabel = noteWrap.querySelector('label');
    sel.addEventListener('change', function () {
      var need = R.needsReason(sel.value);
      noteLabel.textContent = need ? 'Motivo para el cliente (obligatorio)' : 'Mensaje para el cliente (opcional)';
      note.setAttribute('data-rules', need ? 'required|minLen:5' : '');
      V.clearError(note);
    });

    var btn = h('button', { type: 'button', class: 'btn btn-primary w-full' }, UTD.icon('check'), 'Actualizar estado');
    btn.addEventListener('click', async function () {
      var vs = V.validateField(sel);
      if (vs) { V.showError(sel, vs); sel.focus(); return; }
      V.clearError(sel);
      var to = sel.value;
      if (R.needsReason(to)) {
        var vn = V.validateField(note);
        if (vn) { V.showError(note, vn); note.focus(); return; }
      }
      if (to === 'CANCELLED' || to === 'UNREPAIRABLE' || to === 'DELIVERED') {
        var ok = await ui.confirm({
          title: '¿Cambiar a «' + R.statusLabel(to) + '»?',
          message: to === 'DELIVERED' ? 'El ticket se cerrará y no podrá modificarse.' : 'El cliente verá este cambio y tu mensaje en la consulta de su ticket.',
          confirmText: 'Sí, cambiar', tone: to === 'CANCELLED' ? 'danger' : 'primary'
        });
        if (!ok) return;
      }
      act(btn, 'Guardando…', function () { return UTD.api.changeStatus(t.id, { to: to, note: note.value.trim() }); },
        'Estado actualizado a «' + R.statusLabel(to) + '».', 'td-status-h',
        function (fe) { if (fe.note) { V.showError(note, fe.note); note.focus(); return true; } return false; });
    });

    var blockedList = blocked.length
      ? h('ul', { class: 'mt-3 space-y-1 rounded-lg bg-slate-50 p-3 text-xs text-slate-700' },
        blocked.map(function (b) { return h('li', null, h('strong', null, R.statusLabel(b.to) + ': '), b.why); }))
      : null;

    return block('td-status-h', 'list-checks', 'Estado', h('div', { class: 'space-y-3' },
      fieldWrap('td-status', 'Cambiar estado', sel), noteWrap, btn, blockedList));
  }

  // ----- Cotización -----
  function renderQuoteBlock(t) {
    var q = t.quote;
    if (!R.quoteEditable(t)) {
      if (!q) return block('td-quote-h', 'receipt', 'Cotización', h('p', { class: 'text-sm text-slate-700' }, 'Aún no hay cotización. Se puede crear cuando el ticket esté en revisión.'));
      var rows = [
        kv('Monto', h('strong', null, Number(q.amount) > 0 ? U.formatMoney(q.amount, q.currency) : 'Sin costo')),
        kv('Detalle', h('span', { class: 'whitespace-pre-wrap' }, q.description)),
        kv('Vigencia', q.validUntil ? U.formatDateOnly(q.validUntil) : ''),
        kv('Cliente', q.decision === 'APPROVED' ? 'Aprobada (' + (q.decidedBy === 'STAFF' ? 'registrada por el personal' : 'por el cliente') + ')'
          : q.decision === 'REJECTED' ? 'Rechazada' : q.published ? 'Pendiente de respuesta' : 'Aún no enviada')
      ];
      var extra = null;
      if (t.status === 'AWAITING_APPROVAL' && q.published && !q.decision) {
        var b = h('button', { type: 'button', class: 'btn btn-secondary w-full' }, UTD.icon('check'), 'Registrar aprobación del cliente');
        b.addEventListener('click', async function () {
          var ok = await ui.confirm({ title: '¿Registrar la aprobación?', message: 'Úsalo solo si el cliente aprobó la cotización por teléfono, WhatsApp o en persona.', confirmText: 'Sí, registrar' });
          if (!ok) return;
          act(b, 'Guardando…', function () { return UTD.api.recordQuoteDecision(t.id, {}); }, 'Aprobación registrada.', 'td-quote-h');
        });
        extra = h('div', { class: 'mt-3' }, b, h('p', { class: 'hint' }, 'Úsalo si el cliente aprobó por teléfono, WhatsApp o en persona.'));
      }
      return block('td-quote-h', 'receipt', 'Cotización', h('div', null, dl(rows), extra));
    }

    // Campo de texto (no type="number"): el campo numérico del navegador interpreta la coma como decimal según el idioma
    // del sistema, así que "3,000" se convertía en 3. Aquí lo interpreta UTD.utils.parseAmount y se muestra la vista previa.
    var amount = h('input', { id: 'td-q-amount', type: 'text', class: 'input', inputmode: 'decimal', autocomplete: 'off', maxlength: '16',
      value: q ? U.formatAmount(q.amount) : '', placeholder: 'Ej. 3,000.00', 'aria-describedby': 'td-q-amount-preview',
      'data-label': 'Monto', 'data-rules': 'required|positiveNumber', required: true });
    var currency = makeSelect('td-q-currency', Object.keys(C.currencies).map(function (k) { return { value: k, label: C.currencies[k] }; }), q ? q.currency : 'NIO', null);
    currency.setAttribute('data-rules', 'required');
    currency.setAttribute('data-label', 'Moneda');
    var desc = h('textarea', { id: 'td-q-desc', class: 'input', rows: '3', maxlength: '500', 'data-label': 'Detalle', 'data-rules': 'required|minLen:5', required: true });
    desc.value = q ? q.description : '';
    var until = h('input', { id: 'td-q-until', type: 'date', class: 'input', min: U.todayYmd(), value: q ? q.validUntil : '', 'data-label': 'Vigencia', 'data-rules': 'futureDate' });
    var form = h('form', { class: 'space-y-3', novalidate: true },
      h('div', { class: 'grid grid-cols-2 gap-3' }, fieldWrap('td-q-amount', 'Monto *', amount), fieldWrap('td-q-currency', 'Moneda *', currency)),
      h('p', { id: 'td-q-amount-preview', class: 'hint -mt-1 font-medium' }),
      fieldWrap('td-q-desc', 'Qué incluye *', desc),
      fieldWrap('td-q-until', 'Vigente hasta (opcional)', until),
      null);
    var btn = h('button', { type: 'submit', class: 'btn btn-primary w-full' }, UTD.icon('check'), q ? 'Actualizar cotización' : 'Guardar cotización');
    form.appendChild(btn);
    form.appendChild(h('p', { class: 'hint' }, 'Después pasa el ticket a «Cotizado» y luego a «Esperando aprobación» para enviarla al cliente. Usa monto 0 si el servicio no tiene costo.'));
    V.attachLive(form);

    var preview = form.querySelector('#td-q-amount-preview');
    function updatePreview() {
      var r = U.parseAmount(amount.value);
      preview.textContent = r.ok ? 'Se registrará: ' + U.formatMoney(r.value, currency.value) : '';
    }
    amount.addEventListener('input', updatePreview);
    currency.addEventListener('change', updatePreview);
    amount.addEventListener('blur', function () {
      var r = U.parseAmount(amount.value);
      if (r.ok) { amount.value = U.formatAmount(r.value); updatePreview(); } // normaliza: "3,000" -> "3,000.00"
    });
    updatePreview();

    form.addEventListener('submit', function (e) {
      e.preventDefault();
      var res = V.validateForm(form);
      if (!res.valid) { res.errors[0].el.focus(); return; }
      act(btn, 'Guardando…', function () {
        return UTD.api.saveQuote(t.id, { amount: U.parseAmount(amount.value).value, currency: currency.value, description: desc.value.trim(), validUntil: until.value });
      }, 'Cotización guardada.', 'td-quote-h', function (fe) {
        var map = { amount: amount, currency: currency, description: desc, validUntil: until };
        var first = null;
        Object.keys(fe).forEach(function (k) { if (map[k]) { V.showError(map[k], fe[k]); first = first || map[k]; } });
        if (first) { first.focus(); return true; }
        return false;
      });
    });
    return block('td-quote-h', 'receipt', 'Cotización', form);
  }

  // ----- Pago -----
  function renderPaymentBlock(t) {
    if (!R.requiresPayment(t)) {
      return block('td-pay-h', 'banknote', 'Pago', h('p', { class: 'text-sm text-slate-700' },
        'Se habilita cuando el cliente aprueba una cotización con monto mayor a cero. Método preferido: ' + (C.paymentMethods[t.preferredPaymentMethod] || '—') + '.'));
    }
    var rows = [kv('Método', C.paymentMethods[t.preferredPaymentMethod]), kv('Monto', U.formatMoney(t.quote.amount, t.quote.currency))];
    if (t.payment) {
      rows.push(kv('Referencia', t.payment.reference), kv('Comprobante', t.payment.proofName), kv('Informado', U.formatDateTime(t.payment.reportedAt)));
    }
    var sel = makeSelect('td-pay', ['PENDING', 'PROOF_SENT', 'PAID'].map(function (k) { return { value: k, label: C.paymentStatuses[k] }; }), t.paymentStatus, null);
    var btn = h('button', { type: 'button', class: 'btn btn-primary w-full' }, UTD.icon('check'), 'Guardar pago');
    btn.addEventListener('click', function () {
      if (sel.value === t.paymentStatus) { ui.toast('No hay cambios en el pago.', 'info', 2500); return; }
      act(btn, 'Guardando…', function () { return UTD.api.setPayment(t.id, { status: sel.value }); }, 'Pago actualizado.', 'td-pay-h');
    });
    return block('td-pay-h', 'banknote', 'Pago', h('div', { class: 'space-y-3' }, dl(rows), fieldWrap('td-pay', 'Estado del pago', sel), btn));
  }

  // ----- Asignación -----
  function renderAssignBlock(t) {
    var entries = (dash.staff || []).map(function (s) { return { value: s.id, label: s.name }; });
    // Si la persona asignada ya no está activa no aparece en la lista: se agrega para que guardar no la quite sin querer.
    if (t.assignee && !entries.some(function (e) { return String(e.value) === String(t.assignee.id); })) entries.push({ value: t.assignee.id, label: t.assignee.name + ' (inactivo)' });
    var sel = makeSelect('td-assign', entries, t.assignee ? t.assignee.id : '', 'Sin asignar');
    var btn = h('button', { type: 'button', class: 'btn btn-secondary w-full' }, UTD.icon('user-round'), 'Guardar asignación');
    btn.addEventListener('click', function () {
      var current = t.assignee ? String(t.assignee.id) : '';
      if (sel.value === current) { ui.toast('No hay cambios en la asignación.', 'info', 2500); return; }
      act(btn, 'Guardando…', function () { return UTD.api.assign(t.id, sel.value === '' ? null : Number(sel.value)); }, 'Asignación guardada.', 'td-assign-h');
    });
    return block('td-assign-h', 'user-round', 'Asignación', h('div', { class: 'space-y-3' }, fieldWrap('td-assign', 'Persona responsable', sel), btn));
  }

  // ----- Notas internas -----
  function renderNotesBlock(t) {
    var ta = h('textarea', { id: 'td-note', class: 'input', rows: '2', maxlength: '1000', 'data-label': 'Nota', 'data-rules': 'required|minLen:2', required: true });
    var btn = h('button', { type: 'submit', class: 'btn btn-secondary' }, UTD.icon('message-square-text'), 'Agregar nota interna');
    var form = h('form', { class: 'space-y-3', novalidate: true },
      fieldWrap('td-note', 'Nota interna (el cliente no la ve)', ta), btn);
    V.attachLive(form);
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      var res = V.validateForm(form);
      if (!res.valid) { res.errors[0].el.focus(); return; }
      act(btn, 'Guardando…', function () { return UTD.api.addNote(t.id, ta.value.trim()); }, 'Nota agregada.', 'td-note-h',
        function (fe) { if (fe.text) { V.showError(ta, fe.text); ta.focus(); return true; } return false; });
    });
    return block('td-note-h', 'message-square-text', 'Notas internas', form);
  }

  // ----- Historial -----
  var HISTORY_LABEL = { CREATED: 'Solicitud creada', STATUS: 'Cambio de estado', QUOTE: 'Cotización', QUOTE_DECISION: 'Decisión sobre la cotización', PAYMENT: 'Pago', ASSIGN: 'Asignación', NOTE: 'Nota interna' };

  function renderHistory(t) {
    var list = h('ol', { class: 'timeline' });
    t.history.slice().reverse().forEach(function (e) {
      var change = e.type === 'STATUS' && e.to
        ? h('span', { class: 'inline-flex flex-wrap items-center gap-1.5' }, e.from ? ui.statusBadge(e.from) : null, e.from ? UTD.icon('arrow-right', { class: 'text-slate-500' }) : null, ui.statusBadge(e.to))
        : null;
      list.appendChild(h('li', { dataset: { internal: e.internal ? 'true' : 'false' } },
        h('p', { class: 'flex flex-wrap items-center gap-x-2 text-xs text-slate-600' },
          h('span', null, U.formatDateTime(e.at)), h('span', null, '· ' + e.by),
          e.internal ? h('span', { class: 'badge border-amber-300 bg-amber-100 text-amber-900' }, 'Interno') : h('span', { class: 'badge border-slate-300 bg-slate-100 text-slate-800' }, 'Visible al cliente')),
        h('p', { class: 'mt-1 text-sm font-medium text-slate-900' }, HISTORY_LABEL[e.type] || e.type, change ? ' ' : null, change),
        e.text ? h('p', { class: 'mt-0.5 whitespace-pre-wrap text-sm text-slate-700' }, e.text) : null));
    });
    return block('td-history-h', 'clock', 'Historial', list);
  }

  UTD.admin = { closeDialogs: closeTicketDialog };
})(window.UTD = window.UTD || {});
