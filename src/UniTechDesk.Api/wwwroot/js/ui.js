/*
 * Piezas de interfaz compartidas: avisos (toasts), diálogos nativos <dialog>, confirmaciones, insignias.
 * Los <dialog> con showModal() ya traen: foco atrapado, cierre con Esc, fondo inerte y aria-modal.
 */
(function (UTD) {
  'use strict';

  var h = UTD.utils.h;
  var C = UTD.constants;
  var ui = {};

  // ---------- Avisos (toasts) ----------
  var TOAST_STYLE = {
    success: { box: 'border-emerald-300 bg-emerald-50 text-emerald-900', icon: 'circle-check' },
    error:   { box: 'border-red-300 bg-red-50 text-red-900', icon: 'circle-alert' },
    info:    { box: 'border-blue-300 bg-blue-50 text-blue-900', icon: 'info' }
  };

  ui.toast = function (message, type, ms) {
    type = TOAST_STYLE[type] ? type : 'info';
    var box = document.getElementById('toasts');
    if (!box) return;
    var style = TOAST_STYLE[type];
    var toast;
    function dismiss() { if (toast && toast.parentNode) toast.parentNode.removeChild(toast); }
    toast = h('div', {
      class: 'toast-in pointer-events-auto flex items-start gap-3 rounded-lg border p-4 shadow-lg ' + style.box,
      role: type === 'error' ? 'alert' : 'status'
    },
      UTD.icon(style.icon, { class: 'mt-0.5 text-lg' }),
      h('p', { class: 'flex-1 text-sm font-medium' }, message),
      h('button', { type: 'button', class: 'rounded p-0.5 hover:bg-black/10', 'aria-label': 'Cerrar notificación', onclick: dismiss }, UTD.icon('x'))
    );
    box.appendChild(toast);
    setTimeout(dismiss, ms || (type === 'error' ? 8000 : 5000));
  };

  // ---------- Diálogos ----------
  ui.openDialog = function (dlg) {
    if (dlg.open) return;
    dlg._opener = document.activeElement;
    dlg.showModal();
    document.documentElement.classList.add('dialog-open');
  };

  ui.closeDialog = function (dlg, value) {
    if (dlg.open) dlg.close(value);
  };

  ui.initDialogs = function () {
    UTD.utils.$$('dialog.dlg').forEach(function (dlg) {
      // Clic en el fondo oscuro (el evento llega al <dialog> mismo, no al panel).
      dlg.addEventListener('click', function (e) { if (e.target === dlg) dlg.close(); });
      dlg.addEventListener('close', function () {
        if (!document.querySelector('dialog.dlg[open]')) document.documentElement.classList.remove('dialog-open');
        var opener = dlg._opener;
        dlg._opener = null;
        if (opener && opener.isConnected && opener.focus) opener.focus();
      });
    });
  };

  /** Confirmación accesible. Devuelve una promesa con true/false. */
  ui.confirm = function (opts) {
    var dlg = document.getElementById('confirmDialog');
    return new Promise(function (resolve) {
      document.getElementById('confirmTitle').textContent = opts.title || '¿Confirmar?';
      document.getElementById('confirmMsg').textContent = opts.message || '';
      var ok = document.getElementById('confirmOk');
      var cancel = document.getElementById('confirmCancel');
      ok.textContent = opts.confirmText || 'Confirmar';
      ok.className = 'btn ' + (opts.tone === 'danger' ? 'btn-danger' : 'btn-primary');
      var answered = false;
      function finish(value) {
        if (answered) return;
        answered = true;
        ok.removeEventListener('click', onOk);
        cancel.removeEventListener('click', onCancel);
        dlg.removeEventListener('close', onClose);
        if (dlg.open) dlg.close();
        resolve(value);
      }
      function onOk() { finish(true); }
      function onCancel() { finish(false); }
      function onClose() { finish(false); }
      ok.addEventListener('click', onOk);
      cancel.addEventListener('click', onCancel);
      dlg.addEventListener('close', onClose);
      ui.openDialog(dlg);
      cancel.focus(); // el foco inicial va a la opción segura
    });
  };

  // ---------- Botones y estados de carga ----------
  ui.spinner = function () { return UTD.icon('loader-circle', { class: 'animate-spin' }); };

  ui.setBusy = function (btn, busy, label) {
    if (busy) {
      if (!btn._original) btn._original = Array.prototype.slice.call(btn.childNodes);
      UTD.utils.clear(btn);
      btn.appendChild(ui.spinner());
      btn.appendChild(document.createTextNode(label || 'Procesando…'));
      btn.disabled = true;
      btn.setAttribute('aria-busy', 'true');
    } else {
      if (btn._original) {
        UTD.utils.clear(btn);
        btn._original.forEach(function (n) { btn.appendChild(n); });
        btn._original = null;
      }
      btn.disabled = false;
      btn.removeAttribute('aria-busy');
    }
  };

  // ---------- Avisos dentro de la página ----------
  var NOTICE_STYLE = {
    info:    { box: 'border-blue-300 bg-blue-50 text-blue-900', icon: 'info' },
    success: { box: 'border-emerald-300 bg-emerald-50 text-emerald-900', icon: 'circle-check' },
    warning: { box: 'border-amber-300 bg-amber-50 text-amber-900', icon: 'triangle-alert' },
    error:   { box: 'border-red-300 bg-red-50 text-red-900', icon: 'circle-alert' }
  };
  ui.notice = function (type, title, body) {
    var s = NOTICE_STYLE[type] || NOTICE_STYLE.info;
    return h('div', { class: 'flex items-start gap-3 rounded-lg border p-4 text-sm ' + s.box, role: type === 'error' ? 'alert' : null },
      UTD.icon(s.icon, { class: 'mt-0.5 text-lg' }),
      h('div', { class: 'min-w-0' },
        title ? h('p', { class: 'font-semibold' }, title) : null,
        body ? h('p', { class: title ? 'mt-0.5' : '' }, body) : null));
  };

  // ---------- Insignias ----------
  ui.statusBadge = function (code) {
    var s = C.statuses[code];
    return h('span', { class: 'badge ' + (s ? s.badge : 'bg-slate-100 text-slate-900 border-slate-300') }, s ? s.label : String(code));
  };

  var URGENCY_STYLE = { LOW: 'text-slate-700', MEDIUM: 'text-orange-700', HIGH: 'text-red-700 font-bold' };
  ui.urgencyBadge = function (code) {
    return h('span', { class: 'inline-flex items-center gap-1.5 text-sm ' + (URGENCY_STYLE[code] || 'text-slate-700') },
      UTD.icon('flag', { class: 'text-xs' }), 'Urgencia ' + (C.urgencies[code] || code).toLowerCase());
  };

  var PAYMENT_STYLE = {
    NONE:       { cls: 'bg-slate-100 text-slate-800 border-slate-300', icon: 'minus' },
    PENDING:    { cls: 'bg-amber-100 text-amber-900 border-amber-300', icon: 'clock' },
    PROOF_SENT: { cls: 'bg-blue-100 text-blue-900 border-blue-300', icon: 'receipt' },
    PAID:       { cls: 'bg-green-100 text-green-900 border-green-300', icon: 'check' }
  };
  ui.paymentBadge = function (code) {
    var s = PAYMENT_STYLE[code] || PAYMENT_STYLE.NONE;
    var ic = UTD.hasIcon(s.icon) ? UTD.icon(s.icon, { class: 'text-xs' }) : null;
    return h('span', { class: 'badge ' + s.cls }, ic, C.paymentStatuses[code] || code);
  };

  ui.serviceTag = function (serviceType) {
    return h('span', { class: 'inline-flex items-center gap-1.5 font-medium text-slate-800' },
      UTD.icon(serviceType === 'HARDWARE' ? 'wrench' : 'code-xml', { class: 'text-slate-600' }),
      C.serviceTypes[serviceType] || serviceType);
  };

  // ---------- Errores de la API ----------
  /** Convierte un error en { message, fieldErrors } listo para mostrar. */
  ui.describeError = function (err) {
    var fallback = 'Ocurrió un error inesperado. Inténtalo de nuevo.';
    if (!err) return { message: fallback, fieldErrors: null };
    if (err.status === 0) return { message: 'No pudimos conectar con el servidor. Revisa tu conexión e inténtalo de nuevo.', fieldErrors: null };
    if (err.status === 429) return { message: 'Hiciste demasiados intentos. Espera unos minutos antes de volver a intentarlo.', fieldErrors: null };
    if (err.status >= 500) return { message: 'El servidor tuvo un problema. Inténtalo de nuevo en unos minutos.', fieldErrors: null };
    return { message: err.message || fallback, fieldErrors: err.fieldErrors || null };
  };

  UTD.ui = ui;
})(window.UTD = window.UTD || {});
