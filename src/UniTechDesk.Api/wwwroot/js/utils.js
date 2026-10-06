/*
 * Utilidades generales. Lo importante de seguridad está aquí:
 *  - h() crea nodos DOM con textContent (nunca interpreta HTML), así que los datos del usuario no pueden
 *    inyectar etiquetas ni scripts (XSS).
 *  - safeUrl() solo deja pasar http(s)/mailto/tel; bloquea "javascript:" y similares.
 *  - toCsv() neutraliza fórmulas (=, +, -, @) para evitar inyección en Excel.
 */
(function (UTD) {
  'use strict';

  var U = {};

  var SAFE_PROTOCOLS = ['http:', 'https:', 'mailto:', 'tel:'];

  /** Devuelve la URL si su protocolo es seguro (o es relativa/ancla); si no, ''. */
  U.safeUrl = function (value) {
    if (typeof value !== 'string') return '';
    var v = value.trim();
    if (!v) return '';
    if (v.charAt(0) === '#' || v.charAt(0) === '/') return v;
    try {
      // Se analiza con el mismo parser que usa el navegador: así también caen "java\nscript:" y variantes.
      var url = new URL(v, 'http://placeholder.invalid');
      return SAFE_PROTOCOLS.indexOf(url.protocol) !== -1 ? v : '';
    } catch (e) { return ''; }
  };

  /** Solo http(s): para enlaces que escribe el cliente (por ejemplo, un repositorio). */
  U.safeWebUrl = function (value) {
    var v = U.safeUrl(value);
    return /^https?:\/\//i.test(v) ? v : '';
  };

  var HTML_ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
  /** Para el raro caso de armar HTML como texto. La app no lo necesita porque usa h(). */
  U.escapeHtml = function (value) {
    return String(value == null ? '' : value).replace(/[&<>"']/g, function (c) { return HTML_ESCAPES[c]; });
  };

  /**
   * h('div', { class: 'x', onclick: fn, 'aria-label': 'y' }, 'texto', otroNodo, [lista])
   * Los hijos que son texto se insertan como nodos de texto.
   */
  U.h = function (tag, props) {
    var el = document.createElement(tag);
    var start = 2;
    if (props !== null && props !== undefined && (typeof props !== 'object' || props instanceof Node || Array.isArray(props))) {
      start = 1; // el segundo argumento ya es un hijo
      props = null;
    }
    if (props) {
      Object.keys(props).forEach(function (key) {
        var val = props[key];
        if (val === false || val === null || val === undefined) return;
        if (key === 'class') { el.className = val; return; }
        if (key === 'text') { el.textContent = val; return; }
        if (key === 'dataset') { Object.keys(val).forEach(function (k) { el.dataset[k] = val[k]; }); return; }
        if (key === 'value') { el.value = val; return; }
        if (key.slice(0, 2) === 'on' && typeof val === 'function') { el.addEventListener(key.slice(2).toLowerCase(), val); return; }
        if (key === 'href' || key === 'src') {
          // blob: lo genera el propio navegador (vista previa de archivos que eligió la persona usuaria).
          var safe = key === 'src' && /^blob:/i.test(String(val)) ? String(val) : U.safeUrl(String(val));
          if (safe) el.setAttribute(key, safe);
          return;
        }
        el.setAttribute(key, val === true ? '' : String(val));
      });
    }
    function append(child) {
      if (child === null || child === undefined || child === false) return;
      if (Array.isArray(child)) { child.forEach(append); return; }
      if (child instanceof Node) { el.appendChild(child); return; }
      el.appendChild(document.createTextNode(String(child)));
    }
    for (var i = start; i < arguments.length; i++) append(arguments[i]);
    return el;
  };

  U.clear = function (node) { while (node.firstChild) node.removeChild(node.firstChild); return node; };
  U.$ = function (sel, root) { return (root || document).querySelector(sel); };
  U.$$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };

  // ---------- Formatos ----------
  var LOCALE = 'es-NI';
  U.formatDate = function (iso) {
    if (!iso) return '—';
    var d = new Date(iso);
    if (isNaN(d)) return '—';
    return d.toLocaleDateString(LOCALE, { day: '2-digit', month: 'short', year: 'numeric' });
  };
  U.formatTime = function (iso) {
    if (!iso) return '';
    var d = new Date(iso);
    if (isNaN(d)) return '';
    return d.toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' });
  };
  U.formatDateTime = function (iso) {
    return iso ? U.formatDate(iso) + ', ' + U.formatTime(iso) : '—';
  };
  /** "2026-11-30" (campo date) -> fecha legible sin que la zona horaria la corra un día. */
  U.formatDateOnly = function (ymd) {
    if (!ymd) return '—';
    var p = String(ymd).split('-');
    if (p.length !== 3) return '—';
    return new Date(+p[0], +p[1] - 1, +p[2]).toLocaleDateString(LOCALE, { day: '2-digit', month: 'long', year: 'numeric' });
  };
  U.todayYmd = function () {
    var d = new Date();
    var m = String(d.getMonth() + 1).padStart(2, '0');
    var day = String(d.getDate()).padStart(2, '0');
    return d.getFullYear() + '-' + m + '-' + day;
  };
  U.formatMoney = function (amount, currency) {
    var n = Number(amount) || 0;
    var symbol = currency === 'USD' ? 'US$' : 'C$';
    return symbol + ' ' + n.toLocaleString(LOCALE, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  };
  /** Número listo para escribirse en un campo de monto: 3000 -> "3,000.00". Es lo mismo que entiende parseAmount. */
  U.formatAmount = function (amount) {
    var n = Number(amount);
    if (amount === '' || amount === null || amount === undefined || !isFinite(n)) return '';
    return n.toLocaleString(LOCALE, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  };

  /**
   * Interpreta un monto escrito a mano sin importar el idioma del navegador ni cómo separe miles y decimales cada persona.
   *   "3,000" -> 3000   "3.000" -> 3000   "3,85" -> 3.85   "1,250.50" -> 1250.5   "1.250,50" -> 1250.5   "C$ 850" -> 850
   * Con un solo separador seguido de exactamente 3 dígitos (y un entero de 1 a 3 cifras sin cero inicial) se toma como
   * separador de miles; un monto de dinero casi nunca lleva 3 decimales. Para que la persona lo confirme, el panel
   * muestra siempre «Se registrará: C$ 3,000.00».
   * Devuelve { ok, empty, value, error }.
   */
  U.parseAmount = function (raw) {
    var bad = function (msg) { return { ok: false, empty: false, value: NaN, error: msg }; };
    var s = String(raw === null || raw === undefined ? '' : raw).replace(/[\s '’]/g, '').replace(/^(C\$|US\$|\$)/i, '');
    if (!s) return { ok: false, empty: true, value: NaN, error: '' };
    if (!/^[0-9.,]+$/.test(s)) return bad('Escribe solo números (ejemplo: 3,000.00 o 3000).');

    var lastDot = s.lastIndexOf('.');
    var lastComma = s.lastIndexOf(',');
    var decimalSep = null;
    var groupSep = null;
    if (lastDot !== -1 && lastComma !== -1) {
      decimalSep = lastDot > lastComma ? '.' : ',';
      groupSep = decimalSep === '.' ? ',' : '.';
    } else if (lastDot !== -1 || lastComma !== -1) {
      var sep = lastDot !== -1 ? '.' : ',';
      var at = s.lastIndexOf(sep);
      var count = s.split(sep).length - 1;
      var looksLikeGroup = count > 1 || (s.length - at - 1 === 3 && /^[1-9]\d{0,2}$/.test(s.slice(0, at)));
      if (looksLikeGroup) groupSep = sep; else decimalSep = sep;
    }

    var intPart = s;
    var decPart = '';
    if (decimalSep) {
      var i = s.lastIndexOf(decimalSep);
      intPart = s.slice(0, i);
      decPart = s.slice(i + 1);
    }
    if (groupSep && intPart.indexOf(groupSep) !== -1) {
      if (!new RegExp('^\\d{1,3}(\\' + groupSep + '\\d{3})+$').test(intPart)) return bad('El monto no tiene un formato válido (ejemplo: 3,000.00).');
      intPart = intPart.split(groupSep).join('');
    }
    if (!/^\d*$/.test(intPart) || !/^\d*$/.test(decPart) || (!intPart && !decPart)) return bad('El monto no tiene un formato válido (ejemplo: 3,000.00).');
    if (decPart.length > 2) return bad('Usa máximo 2 decimales (ejemplo: 3,000.50).');

    var value = Number((intPart || '0') + '.' + (decPart || '0'));
    if (!isFinite(value) || value > 999999999.99) return bad('El monto es demasiado grande.');
    return { ok: true, empty: false, value: value, error: '' };
  };

  U.formatBytes = function (bytes) {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(0) + ' KB';
    return (bytes / 1024 / 1024).toFixed(1) + ' MB';
  };

  // ---------- Contacto ----------
  U.digits = function (v) { return String(v || '').replace(/\D/g, ''); };
  U.waLink = function (phoneIntl, text) {
    var p = U.digits(phoneIntl);
    return 'https://wa.me/' + p + (text ? '?text=' + encodeURIComponent(text) : '');
  };
  /** Teléfono de 8 dígitos de Nicaragua -> número internacional para wa.me / tel: */
  U.toIntlPhone = function (phone) {
    var d = U.digits(phone);
    if (d.length === 8) return '505' + d;
    return d;
  };

  U.debounce = function (fn, ms) {
    var t;
    return function () {
      var args = arguments, ctx = this;
      clearTimeout(t);
      t = setTimeout(function () { fn.apply(ctx, args); }, ms);
    };
  };

  // ---------- CSV ----------
  /** Neutraliza celdas que Excel interpretaría como fórmula. */
  function csvCell(value) {
    var s = value === null || value === undefined ? '' : String(value);
    if (/^(?:[=+\-@\t\r]|\s+[=+\-@])/.test(s)) s = "'" + s;   // también cuando la fórmula viene precedida de espacios
    return '"' + s.replace(/"/g, '""') + '"';
  }
  /** columns: [{ header, value: row => ... }] */
  U.toCsv = function (rows, columns) {
    var lines = [columns.map(function (c) { return csvCell(c.header); }).join(',')];
    rows.forEach(function (row) {
      lines.push(columns.map(function (c) { return csvCell(c.value(row)); }).join(','));
    });
    return lines.join('\r\n');
  };
  U.downloadText = function (filename, text, mime) {
    // El BOM hace que Excel abra bien las tildes y la ñ.
    var blob = new Blob(['﻿', text], { type: (mime || 'text/csv') + ';charset=utf-8' });
    var url = URL.createObjectURL(blob);
    var a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
  };

  U.copyText = function (text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      return navigator.clipboard.writeText(text);
    }
    return new Promise(function (resolve, reject) {
      var ta = document.createElement('textarea');
      ta.value = text;
      ta.setAttribute('readonly', '');
      ta.className = 'hp';
      document.body.appendChild(ta);
      ta.select();
      try { document.execCommand('copy') ? resolve() : reject(new Error('copy')); } catch (e) { reject(e); }
      ta.remove();
    });
  };

  U.sleep = function (ms) { return new Promise(function (r) { setTimeout(r, ms); }); };

  UTD.utils = U;
  UTD.h = U.h;
})(window.UTD = window.UTD || {});
