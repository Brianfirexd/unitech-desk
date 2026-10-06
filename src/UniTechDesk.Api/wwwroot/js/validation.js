/*
 * Validación declarativa: cada campo declara sus reglas en el HTML con data-rules="required|email|minLen:3".
 *  - Solo se validan los campos habilitados (las secciones ocultas se deshabilitan).
 *  - Los errores se asocian con aria-invalid y aria-describedby para lectores de pantalla.
 * Estas validaciones son solo de apoyo a la persona usuaria: el backend debe repetirlas todas.
 */
(function (UTD) {
  'use strict';

  var h = UTD.utils.h;
  var V = {};

  // ---------- Validadores de formato (reutilizables) ----------
  V.isEmail = function (v) { return v.length <= 254 && /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(v); };

  /** Nicaragua: 8 dígitos que empiezan en 2, 5, 7 u 8 (con o sin +505). También acepta números internacionales con "+". */
  V.isPhone = function (v) {
    var s = String(v).replace(/[\s().-]/g, '');
    if (s.charAt(0) === '+') {
      if (/^\+505/.test(s)) return /^\+505[2578]\d{7}$/.test(s);
      return /^\+\d{8,15}$/.test(s);
    }
    return /^[2578]\d{7}$/.test(s);
  };

  V.isWebUrl = function (v) {
    try { var u = new URL(v); return u.protocol === 'http:' || u.protocol === 'https:'; } catch (e) { return false; }
  };

  V.isTicketCode = function (v) { return /^UTD-\d{3,8}$/i.test(v); };

  function matches(patternKey, v) {
    var p = UTD.config.patterns[patternKey];
    return new RegExp(p).test(v);
  }

  // ---------- Reglas ----------
  // Cada regla devuelve '' si es válida o el mensaje de error.
  var RULES = {
    email: function (v, el) { return !v || V.isEmail(v) ? '' : 'Escribe un correo válido, por ejemplo nombre@correo.com.'; },
    phone: function (v) { return !v || V.isPhone(v) ? '' : 'Escribe un teléfono válido: 8 dígitos (ej. 8888-8888) o con código de país (+505…).'; },
    studentId: function (v) { return !v || matches('studentId', v) ? '' : 'El carnet debe tener entre 6 y 15 caracteres (letras, números o guion).'; },
    ruc: function (v) { return !v || matches('ruc', v.toUpperCase()) ? '' : 'El RUC debe tener 14 caracteres (letras y números, sin espacios).'; },
    url: function (v) { return !v || V.isWebUrl(v) ? '' : 'Escribe un enlace que empiece con http:// o https://.'; },
    ticketCode: function (v) { return !v || V.isTicketCode(v) ? '' : 'El código tiene el formato UTD-1234.'; },
    minLen: function (v, el, arg) { return !v || v.length >= Number(arg) ? '' : 'Escribe al menos ' + arg + ' caracteres.'; },
    maxLen: function (v, el, arg) { return v.length <= Number(arg) ? '' : 'Máximo ' + arg + ' caracteres.'; },
    futureDate: function (v) { return !v || v >= UTD.utils.todayYmd() ? '' : 'Elige una fecha de hoy en adelante.'; },
    positiveNumber: function (v) {
      var r = UTD.utils.parseAmount(v);
      return r.empty || r.ok ? '' : r.error;
    }
  };

  function groupFor(el) {
    if (el.type === 'radio' && el.name) {
      var form = el.form || document;
      return Array.prototype.slice.call(form.querySelectorAll('input[type="radio"][name="' + el.name + '"]'));
    }
    return [el];
  }

  function fieldKey(el) { return el.type === 'radio' && el.name ? el.name : (el.id || el.name); }

  function valueOf(el) {
    if (el.type === 'radio') return groupFor(el).some(function (r) { return r.checked; }) ? '1' : '';
    if (el.type === 'checkbox') return el.checked ? '1' : '';
    return String(el.value || '').trim();
  }

  function labelOf(el) { return el.getAttribute('data-label') || el.name || el.id || 'Este campo'; }

  /** Valida un campo y devuelve el mensaje de error ('' si es válido). */
  V.validateField = function (el) {
    var rules = (el.getAttribute('data-rules') || '').split('|').filter(Boolean);
    var value = valueOf(el);
    for (var i = 0; i < rules.length; i++) {
      var parts = rules[i].split(':');
      var name = parts[0], arg = parts[1];
      var msg = '';
      if (name === 'required') {
        if (!value) {
          msg = el.getAttribute('data-msg-required') ||
            (el.tagName === 'SELECT' || el.type === 'radio'
              ? 'Selecciona una opción en «' + labelOf(el) + '».'
              : '«' + labelOf(el) + '» es obligatorio.');
        }
      } else if (RULES[name]) {
        msg = RULES[name](value, el, arg);
      }
      if (msg) return msg;
    }
    return '';
  };

  // ---------- Mostrar / limpiar errores ----------
  V.showError = function (el, message) {
    var key = fieldKey(el);
    var field = el.closest('.field') || el.parentElement;
    var errId = 'err-' + key;
    var p = field.querySelector('[data-error-for="' + key + '"]');
    if (!p) {
      p = h('p', { class: 'field-error', id: errId, dataset: { errorFor: key } });
      field.appendChild(p);
    }
    UTD.utils.clear(p);
    p.appendChild(UTD.icon('circle-alert', { class: 'mt-px' }));
    p.appendChild(h('span', message));
    p.hidden = false;
    groupFor(el).forEach(function (c) {
      c.setAttribute('aria-invalid', 'true');
      var ids = (c.getAttribute('aria-describedby') || '').split(' ').filter(Boolean);
      if (ids.indexOf(errId) === -1) ids.push(errId);
      c.setAttribute('aria-describedby', ids.join(' '));
    });
  };

  V.clearError = function (el) {
    var key = fieldKey(el);
    var field = el.closest('.field') || el.parentElement;
    var errId = 'err-' + key;
    var p = field && field.querySelector('[data-error-for="' + key + '"]');
    if (p) { UTD.utils.clear(p); p.hidden = true; }
    groupFor(el).forEach(function (c) {
      c.removeAttribute('aria-invalid');
      var ids = (c.getAttribute('aria-describedby') || '').split(' ').filter(function (x) { return x && x !== errId; });
      if (ids.length) c.setAttribute('aria-describedby', ids.join(' ')); else c.removeAttribute('aria-describedby');
    });
  };

  V.clearAll = function (form) {
    UTD.utils.$$('[data-error-for]', form).forEach(function (p) { UTD.utils.clear(p); p.hidden = true; });
    UTD.utils.$$('[aria-invalid]', form).forEach(function (c) { c.removeAttribute('aria-invalid'); });
  };

  /** Valida todos los campos habilitados del formulario. Devuelve { valid, errors: [{ el, key, message }] }. */
  V.validateForm = function (form) {
    var errors = [];
    var seen = {};
    UTD.utils.$$('[data-rules]', form).forEach(function (el) {
      if (el.disabled) return;
      var key = fieldKey(el);
      if (seen[key]) return;
      seen[key] = true;
      var msg = V.validateField(el);
      if (msg) { V.showError(el, msg); errors.push({ el: el, key: key, message: msg }); }
      else V.clearError(el);
    });
    return { valid: errors.length === 0, errors: errors };
  };

  /** Valida al salir del campo y limpia el error en cuanto el valor pasa a ser válido. */
  V.attachLive = function (form) {
    form.addEventListener('focusout', function (e) {
      var el = e.target;
      if (!el.getAttribute || !el.getAttribute('data-rules') || el.disabled) return;
      if (el.type === 'checkbox' || el.type === 'radio') return;
      var msg = V.validateField(el);
      if (msg) V.showError(el, msg); else V.clearError(el);
    });
    function revalidateIfInvalid(e) {
      var el = e.target;
      if (!el.getAttribute || !el.getAttribute('data-rules') || el.disabled) return;
      var invalid = groupFor(el).some(function (c) { return c.getAttribute('aria-invalid') === 'true'; });
      if (!invalid) return;
      var msg = V.validateField(el);
      if (msg) V.showError(el, msg); else V.clearError(el);
    }
    form.addEventListener('input', revalidateIfInvalid);
    form.addEventListener('change', revalidateIfInvalid);
  };

  /** Resumen de errores arriba del formulario, con enlaces que llevan al campo. */
  V.renderSummary = function (container, errors) {
    UTD.utils.clear(container);
    if (!errors.length) { container.hidden = true; return; }
    var list = h('ul', { class: 'mt-2 list-disc space-y-1 pl-5' });
    errors.forEach(function (err) {
      list.appendChild(h('li', h('button', {
        type: 'button',
        class: 'text-left underline hover:no-underline',
        onclick: function () { err.el.focus(); if (err.el.scrollIntoView) err.el.scrollIntoView({ block: 'center' }); }
      }, err.message)));
    });
    container.appendChild(h('p', { class: 'flex items-center gap-2 font-semibold' }, UTD.icon('circle-alert'),
      errors.length === 1 ? 'Hay 1 campo por corregir:' : 'Hay ' + errors.length + ' campos por corregir:'));
    container.appendChild(list);
    container.hidden = false;
  };

  /** Valida un archivo adjunto. allowed: { extensiones: [...] }. Devuelve '' o el mensaje. */
  V.validateFile = function (file, allowedExts) {
    var maxBytes = UTD.config.limits.maxFileSizeMB * 1024 * 1024;
    var ext = (file.name.split('.').pop() || '').toLowerCase();
    if (allowedExts.indexOf(ext) === -1) {
      return '«' + file.name + '»: formato no permitido (solo ' + allowedExts.join(', ').toUpperCase() + ').';
    }
    if (file.size > maxBytes) {
      return '«' + file.name + '» pesa ' + UTD.utils.formatBytes(file.size) + '; el máximo es ' + UTD.config.limits.maxFileSizeMB + ' MB.';
    }
    if (file.size === 0) return '«' + file.name + '» está vacío.';
    return '';
  };

  UTD.validation = V;
})(window.UTD = window.UTD || {});
