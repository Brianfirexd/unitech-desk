/*
 * Vista "Consultar ticket": la persona escribe código + correo, ve el estado, aprueba o rechaza la
 * cotización, y (si paga por transferencia) registra la referencia de su pago.
 * El código y el correo se mandan por POST, no por la URL, para que no queden en registros de acceso.
 */
(function (UTD) {
  'use strict';

  var h = UTD.utils.h;
  var $ = UTD.utils.$;
  var V = UTD.validation;
  var C = UTD.constants;
  var R = UTD.rules;
  var ui = UTD.ui;
  var U = UTD.utils;

  var state = { lookup: null, ticket: null, busy: false, initialized: false };
  var els = {};

  var PROOF_EXTS = ['jpg', 'jpeg', 'png', 'pdf'];

  // ---------- Consulta ----------
  async function lookup(code, email, opts) {
    opts = opts || {};
    state.busy = true;
    if (opts.button) ui.setBusy(opts.button, true, 'Consultando…');
    try {
      var ticket = await UTD.api.trackTicket(code, email);
      state.lookup = { code: code, email: email };
      state.ticket = ticket;
      render(ticket, opts.focus);
      return true;
    } catch (err) {
      var info = ui.describeError(err);
      if (opts.keepResult) { ui.toast(info.message, 'error'); return false; }
      U.clear(els.result);
      state.ticket = null;
      els.result.appendChild(ui.notice('error', 'No pudimos mostrar tu ticket', info.message));
      return false;
    } finally {
      state.busy = false;
      if (opts.button) ui.setBusy(opts.button, false);
    }
  }

  function onSubmit(e) {
    e.preventDefault();
    if (state.busy) return;
    var res = V.validateForm(els.form);
    if (!res.valid) { res.errors[0].el.focus(); return; }
    lookup(els.code.value.trim().toUpperCase(), els.email.value.trim(), { button: els.btn, focus: true });
  }

  async function refresh(btn) {
    if (!state.lookup || state.busy) return;
    var ok = await lookup(state.lookup.code, state.lookup.email, { button: btn, keepResult: true });
    if (ok) ui.toast('Estado actualizado.', 'success', 2500);
  }

  // ---------- Acciones del cliente ----------
  async function decide(decision) {
    var t = state.ticket;
    var money = U.formatMoney(t.quote.amount, t.quote.currency);
    var approve = decision === 'APPROVED';
    var ok = await ui.confirm({
      title: approve ? '¿Aprobar la cotización?' : '¿Rechazar la cotización?',
      message: approve
        ? 'Aceptas el costo de ' + money + ' por este trabajo. Después podrás pagar con el método que elegiste.'
        : 'Si rechazas la cotización, el ticket se cancelará. Esta acción no se puede deshacer.',
      confirmText: approve ? 'Sí, aprobar' : 'Sí, rechazar',
      tone: approve ? 'primary' : 'danger'
    });
    if (!ok) return;
    try {
      var updated = await UTD.api.decideQuote(state.lookup.code, state.lookup.email, decision);
      state.ticket = updated;
      render(updated);
      ui.toast(approve ? 'Cotización aprobada. ¡Gracias!' : 'Cotización rechazada. El ticket fue cancelado.', approve ? 'success' : 'info');
    } catch (err) {
      ui.toast(ui.describeError(err).message, 'error');
      refresh();
    }
  }

  async function sendProof(form, btn) {
    var res = V.validateForm(form);
    var fileInput = form.querySelector('input[type="file"]');
    var file = fileInput.files && fileInput.files[0] ? fileInput.files[0] : null;
    var fileMsg = file ? V.validateFile(file, PROOF_EXTS) : '';
    if (fileMsg) { V.showError(fileInput, fileMsg); res.errors.push({ el: fileInput, key: 'proofFile', message: fileMsg }); }
    else V.clearError(fileInput);
    if (res.errors.length) { res.errors[0].el.focus(); return; }

    ui.setBusy(btn, true, 'Enviando…');
    try {
      var updated = await UTD.api.submitPaymentProof(state.lookup.code, state.lookup.email, {
        reference: form.querySelector('input[name="reference"]').value.trim(), file: file
      });
      state.ticket = updated;
      render(updated);
      ui.toast('Recibimos tu comprobante. Lo verificaremos pronto.', 'success');
    } catch (err) {
      var info = ui.describeError(err);
      if (info.fieldErrors && info.fieldErrors.reference) V.showError(form.querySelector('input[name="reference"]'), info.fieldErrors.reference);
      else ui.toast(info.message, 'error');
      if (err && err.status === 409) refresh(); // el estado del ticket cambió: se actualiza la pantalla
    } finally {
      ui.setBusy(btn, false);
    }
  }

  // ---------- Dibujo del resultado ----------
  function stepState(i, idx, status) {
    if (status === 'DELIVERED') return 'done';
    if (idx === -1) return 'todo';
    if (i < idx) return 'done';
    if (i === idx) return status === 'UNREPAIRABLE' ? 'stopped' : 'current';
    return 'todo';
  }

  function renderStepper(t) {
    var p = R.flowProgress(t.serviceType, t.status);
    var list = h('ol', { class: 'stepper', 'aria-label': 'Avance del ticket' });
    p.steps.forEach(function (s, i) {
      var st = stepState(i, p.index, t.status);
      var dotContent = st === 'done' ? UTD.icon('check') : st === 'stopped' ? UTD.icon('x') : String(i + 1);
      list.appendChild(h('li', { class: 'step', dataset: { state: st }, 'aria-current': st === 'current' ? 'step' : null },
        h('span', { class: 'step-dot' }, dotContent),
        h('span', { class: 'step-label' }, s.label,
          h('span', { class: 'sr-only' }, st === 'done' ? ' (completado)' : st === 'current' ? ' (paso actual)' : st === 'stopped' ? ' (detenido)' : ' (pendiente)'))));
    });
    return h('section', { class: 'card p-6', 'aria-labelledby': 'track-progress' },
      h('h3', { id: 'track-progress', class: 'eyebrow mb-4' }, 'Avance'), list);
  }

  function lastStatusText(t, status) {
    for (var i = t.timeline.length - 1; i >= 0; i--) {
      if (t.timeline[i].status === status && t.timeline[i].text) return t.timeline[i].text;
    }
    return '';
  }

  function renderStatusNotice(t) {
    var days = UTD.config.policy.unclaimedDays;
    var msg = lastStatusText(t, t.status);
    switch (t.status) {
      case 'CANCELLED':
        return ui.notice('error', 'Este ticket fue cancelado', msg);
      case 'UNREPAIRABLE':
        return ui.notice('warning', 'Tu equipo no tiene reparación', (msg ? msg + ' ' : '') + 'Coordina con el personal la devolución de tu equipo.');
      case 'WAITING_PARTS':
        return ui.notice('info', 'Estamos esperando repuestos', msg || 'Te avisaremos apenas lleguen para continuar con la reparación.');
      case 'READY':
        return ui.notice('success', t.serviceType === 'HARDWARE' ? 'Tu equipo está listo para retirar' : 'Tu trabajo está listo para entrega',
          t.serviceType === 'HARDWARE' ? 'Tienes ' + days + ' días para retirarlo. ' + (msg || '') : msg);
      case 'DELIVERED':
        return ui.notice('success', 'Ticket entregado', 'Gracias por confiar en UniTech Desk.');
      default:
        return null;
    }
  }

  // Si el cliente ya aprobó la cotización pero el personal aún no inicia el trabajo, "Esperando aprobación" confundiría.
  function clientStatusBadge(t) {
    if (t.status === 'AWAITING_APPROVAL' && t.quote && t.quote.decision === 'APPROVED') {
      return h('span', { class: 'badge border-emerald-300 bg-emerald-100 text-emerald-900' }, UTD.icon('check', { class: 'text-xs' }), 'Cotización aprobada');
    }
    return ui.statusBadge(t.status);
  }

  function quoteExpired(q) { return !!(q.validUntil && q.validUntil < U.todayYmd()); }

  function renderQuote(t) {
    var q = t.quote;
    var card = h('section', { class: 'card overflow-hidden', 'aria-labelledby': 'track-quote' });
    card.appendChild(h('div', { class: 'card-header flex flex-wrap items-center justify-between gap-2' },
      h('h3', { id: 'track-quote', class: 'flex items-center gap-2 text-lg font-bold text-slate-900' }, UTD.icon('receipt', { class: 'text-university-700' }), 'Cotización'),
      q.decision === 'APPROVED' ? h('span', { class: 'badge border-emerald-300 bg-emerald-100 text-emerald-900' }, UTD.icon('check', { class: 'text-xs' }), 'Aprobada')
        : q.decision === 'REJECTED' ? h('span', { class: 'badge border-red-300 bg-red-100 text-red-900' }, 'Rechazada') : null));
    var body = h('div', { class: 'card-body space-y-4' });
    body.appendChild(h('p', { class: 'text-3xl font-extrabold text-university-900' }, Number(q.amount) > 0 ? U.formatMoney(q.amount, q.currency) : 'Sin costo'));
    body.appendChild(h('p', { class: 'whitespace-pre-wrap text-slate-700' }, q.description));
    if (q.validUntil) body.appendChild(h('p', { class: 'text-sm text-slate-600' }, 'Vigente hasta el ' + U.formatDateOnly(q.validUntil) + '.'));

    var pending = !q.decision && t.status === 'AWAITING_APPROVAL';
    if (pending && quoteExpired(q)) {
      body.appendChild(ui.notice('warning', 'Esta cotización venció', 'Escríbenos para que la actualicemos.'));
    } else if (pending) {
      body.appendChild(h('div', { class: 'rounded-lg border border-orange-200 bg-orange-50 p-4' },
        h('p', { class: 'mb-3 font-semibold text-slate-900' }, '¿Estás de acuerdo con esta cotización?'),
        h('div', { class: 'flex flex-wrap gap-3' },
          h('button', { type: 'button', class: 'btn btn-success', onclick: function () { decide('APPROVED'); } }, UTD.icon('check'), 'Aprobar cotización'),
          h('button', { type: 'button', class: 'btn btn-secondary', onclick: function () { decide('REJECTED'); } }, 'Rechazar'))));
    } else if (q.decision === 'APPROVED') {
      body.appendChild(h('p', { class: 'text-sm text-slate-600' }, 'Aprobada el ' + U.formatDate(q.decidedAt) + '. Iniciaremos el trabajo muy pronto.'));
    }
    card.appendChild(body);
    return card;
  }

  function copyButton(text, label) {
    return h('button', {
      type: 'button', class: 'btn-icon shrink-0', 'aria-label': label,
      onclick: function () {
        U.copyText(text).then(function () { ui.toast('Copiado.', 'success', 2000); }, function () { ui.toast('No se pudo copiar.', 'error'); });
      }
    }, UTD.icon('copy'));
  }

  function renderProofForm() {
    var form = h('form', { class: 'mt-5 space-y-4 rounded-lg border border-slate-200 bg-slate-50 p-4', novalidate: true });
    var refId = 'proofReference';
    form.appendChild(h('div', { class: 'field' },
      h('label', { class: 'label', for: refId }, 'Número de referencia de tu transferencia ', h('span', { class: 'req', 'aria-hidden': 'true' }, '*')),
      h('input', { id: refId, name: 'reference', type: 'text', class: 'input', maxlength: '40', autocomplete: 'off', required: true,
        'data-label': 'Número de referencia', 'data-rules': 'required|minLen:4' })));
    form.appendChild(h('div', { class: 'field' },
      h('label', { class: 'label', for: 'proofFile' }, 'Comprobante ', h('span', { class: 'font-normal text-slate-600' }, '(opcional · imagen o PDF, hasta ' + UTD.config.limits.maxFileSizeMB + ' MB)')),
      h('input', { id: 'proofFile', type: 'file', accept: '.jpg,.jpeg,.png,.pdf', class: 'input file:mr-3 file:rounded-md file:border-0 file:bg-university-100 file:px-3 file:py-1 file:text-sm file:font-semibold file:text-university-900' })));
    var btn = h('button', { type: 'submit', class: 'btn btn-primary' }, UTD.icon('send'), 'Enviar referencia de pago');
    form.appendChild(btn);
    V.attachLive(form);
    form.addEventListener('submit', function (e) { e.preventDefault(); sendProof(form, btn); });
    return form;
  }

  function renderPayment(t) {
    var q = t.quote;
    if (!(q && q.decision === 'APPROVED')) return null;
    if (!(Number(q.amount) > 0)) return ui.notice('info', 'Este trabajo no tiene costo', 'No necesitas realizar ningún pago.');
    if (t.paymentStatus === 'PAID') return ui.notice('success', 'Pago confirmado', 'Ya registramos tu pago. ¡Gracias!');
    if (t.paymentStatus === 'PROOF_SENT') {
      return ui.notice('info', 'Estamos verificando tu pago', 'Recibimos tu comprobante' + (t.paymentReference ? ' (referencia ' + t.paymentReference + ')' : '') + '. Te avisaremos cuando esté confirmado.');
    }
    var pi = t.paymentInstructions;
    if (!pi) return null;
    var card = h('section', { class: 'card overflow-hidden', 'aria-labelledby': 'track-pay' });
    card.appendChild(h('div', { class: 'card-header' },
      h('h3', { id: 'track-pay', class: 'flex items-center gap-2 text-lg font-bold text-slate-900' }, UTD.icon('banknote', { class: 'text-university-700' }), 'Cómo pagar'),
      h('p', { class: 'mt-1 text-sm text-slate-700' }, 'Método elegido: ' + (C.paymentMethods[pi.method] || pi.method))));
    var body = h('div', { class: 'card-body' });
    body.appendChild(h('p', { class: 'text-slate-700' }, pi.note));
    if (pi.accounts && pi.accounts.length) {
      var grid = h('div', { class: 'mt-4 grid gap-3 sm:grid-cols-2' });
      pi.accounts.forEach(function (a) {
        grid.appendChild(h('div', { class: 'flex items-center justify-between gap-2 rounded-lg border border-blue-200 bg-blue-50 p-3' },
          h('div', { class: 'min-w-0' },
            h('p', { class: 'text-xs text-slate-700' }, a.bank + ' · ' + (a.currency === 'USD' ? 'Dólares' : 'Córdobas')),
            h('p', { class: 'font-bold tracking-wide text-slate-900' }, a.number),
            h('p', { class: 'truncate text-xs text-slate-700' }, a.holder)),
          copyButton(a.number, 'Copiar número de cuenta ' + a.number)));
      });
      body.appendChild(grid);
    }
    if (pi.method === 'TRANSFER') body.appendChild(renderProofForm());
    card.appendChild(body);
    return card;
  }

  function renderTimeline(t) {
    var items = t.timeline.slice().reverse();
    var list = h('ol', { class: 'timeline' });
    items.forEach(function (e) {
      var head = e.status && C.statuses[e.status] ? ui.statusBadge(e.status) : null;
      list.appendChild(h('li', null,
        h('p', { class: 'text-xs text-slate-600' }, U.formatDateTime(e.at)),
        head ? h('div', { class: 'mt-1' }, head) : null,
        e.text ? h('p', { class: 'mt-1 text-sm text-slate-800' }, e.text) : null));
    });
    return h('section', { class: 'card p-6', 'aria-labelledby': 'track-history' },
      h('h3', { id: 'track-history', class: 'eyebrow mb-4' }, 'Historial'), list);
  }

  function render(t, focus) {
    U.clear(els.result);

    var refreshBtn = h('button', { type: 'button', class: 'btn btn-secondary btn-sm' }, UTD.icon('refresh-cw'), 'Actualizar');
    refreshBtn.addEventListener('click', function () { refresh(refreshBtn); });

    var title = h('h2', { tabindex: '-1', class: 'text-2xl font-extrabold tracking-wide text-university-900' }, t.code);
    els.result.appendChild(h('section', { class: 'card p-6', 'aria-label': 'Resumen del ticket' },
      h('div', { class: 'flex flex-wrap items-start justify-between gap-3' },
        h('div', null,
          h('p', { class: 'eyebrow' }, 'Ticket'),
          title,
          h('p', { class: 'mt-1 flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-slate-700' },
            ui.serviceTag(t.serviceType), h('span', null, 'Creado el ' + U.formatDate(t.createdAt)))),
        h('div', { class: 'flex flex-col items-end gap-2' }, clientStatusBadge(t), refreshBtn))));

    var notice = renderStatusNotice(t);
    if (notice) els.result.appendChild(notice);
    els.result.appendChild(renderStepper(t));
    if (t.quote) els.result.appendChild(renderQuote(t));
    var pay = renderPayment(t);
    if (pay) els.result.appendChild(pay);
    els.result.appendChild(renderTimeline(t));

    var wa = UTD.config.contact.whatsapp;
    if (wa) {
      els.result.appendChild(h('p', { class: 'text-center text-sm text-slate-700' }, '¿Dudas sobre tu ticket? ',
        h('a', {
          href: U.waLink(wa, 'Hola, consulto por mi ticket ' + t.code), target: '_blank', rel: 'noopener noreferrer',
          class: 'font-semibold text-university-700 underline hover:no-underline'
        }, 'Escríbenos por WhatsApp', h('span', { class: 'sr-only' }, ' (se abre en una pestaña nueva)'))));
    }
    if (focus) title.focus();
  }

  // ---------- Ciclo de vida ----------
  function init() {
    if (state.initialized) return;
    state.initialized = true;
    els.form = $('#trackForm');
    els.code = $('#trackCode');
    els.email = $('#trackEmail');
    els.btn = $('#trackBtn');
    els.result = $('#trackResult');
    V.attachLive(els.form);
    els.form.addEventListener('submit', onSubmit);
    els.code.addEventListener('input', function () { els.code.value = els.code.value.toUpperCase().replace(/\s/g, ''); });
  }

  UTD.views = UTD.views || {};
  UTD.views.track = {
    title: 'Consultar ticket',
    init: init,
    enter: function (query) {
      init();
      var code = query && query.codigo ? String(query.codigo).toUpperCase() : '';
      if (code && V.isTicketCode(code)) {
        els.code.value = code;
        els.email.focus();
      }
    },
    leave: function () {
      // Al salir se borra lo consultado: otra persona podría usar el mismo equipo.
      state.lookup = null;
      state.ticket = null;
      if (els.result) U.clear(els.result);
      if (els.email) els.email.value = '';
    }
  };
})(window.UTD = window.UTD || {});
