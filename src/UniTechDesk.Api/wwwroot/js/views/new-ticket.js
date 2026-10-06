/*
 * Vista "Nuevo ticket": formulario con secciones condicionales, validación, carga de archivos
 * (con arrastrar y soltar real) y confirmación con el código del ticket.
 */
(function (UTD) {
  'use strict';

  var h = UTD.utils.h;
  var $ = UTD.utils.$;
  var $$ = UTD.utils.$$;
  var V = UTD.validation;
  var C = UTD.constants;
  var ui = UTD.ui;

  var IMAGE_EXTS = ['jpg', 'jpeg', 'png', 'gif', 'webp'];
  var DOC_EXTS = ['pdf'];

  var state = { files: [], dirty: false, submitting: false, captchaToken: '', captchaWidgetId: null, catalogsLoaded: false, initialized: false };
  var els = {};

  // ---------- Utilidades del formulario ----------
  function checked(name) {
    var r = els.form.querySelector('input[name="' + name + '"]:checked');
    return r ? r.value : '';
  }
  function val(id) {
    var el = document.getElementById(id);
    return el && !el.disabled ? String(el.value || '').trim() : '';
  }
  function fillSelect(select, entries, placeholder) {
    UTD.utils.clear(select);
    select.appendChild(h('option', { value: '' }, placeholder));
    entries.forEach(function (e) { select.appendChild(h('option', { value: e.value }, e.label)); });
  }
  function objEntries(obj) { return Object.keys(obj).map(function (k) { return { value: k, label: obj[k] }; }); }

  /** Muestra u oculta una sección y deshabilita sus campos para que no se validen ni se envíen. */
  function setSection(el, visible) {
    el.hidden = !visible;
    $$('input, select, textarea', el).forEach(function (i) {
      i.disabled = !visible;
      if (!visible && i.getAttribute('data-rules')) V.clearError(i);
    });
  }

  function syncRequester() {
    var t = checked('requesterType');
    setSection(els.studentFields, t === 'STUDENT');
    setSection(els.externalFields, t === 'EXTERNAL');
  }

  function allowedExts() { return checked('serviceType') === 'SOFTWARE' ? IMAGE_EXTS.concat(DOC_EXTS) : IMAGE_EXTS; }

  function syncService() {
    var t = checked('serviceType');
    setSection(els.hardwareFields, t === 'HARDWARE');
    setSection(els.softwareFields, t === 'SOFTWARE');
    var exts = allowedExts();
    els.fileInput.setAttribute('accept', exts.map(function (e) { return '.' + e; }).join(','));
    els.filesHelp.textContent = (t === 'SOFTWARE'
      ? 'Imágenes (JPG, PNG, GIF, WEBP) o documentos (' + DOC_EXTS.map(function (e) { return e.toUpperCase(); }).join(', ') + ').'
      : 'Fotos o capturas (JPG, PNG, GIF, WEBP).') +
      ' Hasta ' + UTD.config.limits.maxFiles + ' archivos de ' + UTD.config.limits.maxFileSizeMB + ' MB.';
    // Si cambió el tipo de servicio, se quitan los archivos que ya no aplican.
    var removed = [];
    state.files = state.files.filter(function (f) {
      var ext = (f.file.name.split('.').pop() || '').toLowerCase();
      if (exts.indexOf(ext) === -1) { removed.push(f.file.name); if (f.url) URL.revokeObjectURL(f.url); return false; }
      return true;
    });
    renderFiles();
    if (removed.length) showFileErrors(['Se quitaron archivos que no aplican a este servicio: ' + removed.join(', ') + '.']);
  }

  function syncAll() { syncRequester(); syncService(); }

  // ---------- Catálogos (carreras y recintos) ----------
  function loadCatalogs() {
    if (state.catalogsLoaded) return Promise.resolve();
    return UTD.api.getCatalogs().then(function (cat) {
      UTD.catalogs = cat;
      fillSelect(els.majorId, cat.majors.map(function (m) { return { value: m.id, label: m.name }; }), 'Selecciona carrera…');
      fillSelect(els.campusId, cat.campuses.map(function (m) { return { value: m.id, label: m.name }; }), 'Selecciona recinto…');
      state.catalogsLoaded = true;
    }).catch(function () {
      fillSelect(els.majorId, [], 'No disponible');
      fillSelect(els.campusId, [], 'No disponible');
      ui.toast('No se pudieron cargar las carreras y recintos. Recarga la página para intentarlo de nuevo.', 'error');
    });
  }

  // ---------- Archivos ----------
  function isImage(file) { return /^image\//.test(file.type) || IMAGE_EXTS.indexOf((file.name.split('.').pop() || '').toLowerCase()) !== -1; }

  function showFileErrors(messages) {
    UTD.utils.clear(els.fileErrors);
    var seen = {};
    messages.forEach(function (m) {
      if (seen[m]) return;
      seen[m] = true;
      els.fileErrors.appendChild(h('p', { class: 'flex items-start gap-1.5' }, UTD.icon('circle-alert', { class: 'mt-px' }), h('span', m)));
    });
  }

  function addFiles(list) {
    var L = UTD.config.limits;
    var exts = allowedExts();
    var errors = [];
    Array.prototype.slice.call(list).forEach(function (f) {
      if (state.files.length >= L.maxFiles) { errors.push('Solo puedes adjuntar hasta ' + L.maxFiles + ' archivos.'); return; }
      var msg = V.validateFile(f, exts);
      if (msg) { errors.push(msg); return; }
      var dup = state.files.some(function (x) { return x.file.name === f.name && x.file.size === f.size; });
      if (dup) { errors.push('«' + f.name + '» ya está en la lista.'); return; }
      state.files.push({ file: f, url: isImage(f) ? URL.createObjectURL(f) : null });
    });
    showFileErrors(errors);
    renderFiles();
    state.dirty = true;
  }

  function removeFile(entry) {
    if (entry.url) URL.revokeObjectURL(entry.url);
    state.files = state.files.filter(function (f) { return f !== entry; });
    showFileErrors([]);
    renderFiles();
    els.fileInput.focus();
  }

  function clearFiles() {
    state.files.forEach(function (f) { if (f.url) URL.revokeObjectURL(f.url); });
    state.files = [];
    showFileErrors([]);
    renderFiles();
  }

  function renderFiles() {
    UTD.utils.clear(els.fileList);
    state.files.forEach(function (entry) {
      var thumb = entry.url
        ? h('img', { src: entry.url, alt: '', class: 'h-12 w-12 rounded border border-slate-200 object-cover' })
        : h('span', { class: 'flex h-12 w-12 items-center justify-center rounded border border-slate-200 bg-slate-50 text-xl text-slate-600' }, UTD.icon('file-text'));
      els.fileList.appendChild(h('li', { class: 'flex items-center gap-3 rounded-lg border border-slate-200 bg-white p-2' },
        thumb,
        h('div', { class: 'min-w-0 flex-1' },
          h('p', { class: 'truncate text-sm font-medium text-slate-800', title: entry.file.name }, entry.file.name),
          h('p', { class: 'text-xs text-slate-600' }, UTD.utils.formatBytes(entry.file.size))),
        h('button', {
          type: 'button', class: 'btn-icon', 'aria-label': 'Quitar archivo ' + entry.file.name,
          onclick: function () { removeFile(entry); }
        }, UTD.icon('trash-2'))));
    });
  }

  function bindDropzone() {
    var dz = els.dropzone;
    ['dragenter', 'dragover'].forEach(function (ev) {
      dz.addEventListener(ev, function (e) { e.preventDefault(); dz.setAttribute('data-dragging', 'true'); });
    });
    ['dragleave', 'dragend'].forEach(function (ev) {
      dz.addEventListener(ev, function () { dz.removeAttribute('data-dragging'); });
    });
    dz.addEventListener('drop', function (e) {
      e.preventDefault();
      dz.removeAttribute('data-dragging');
      if (e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length) addFiles(e.dataTransfer.files);
    });
    els.fileInput.addEventListener('change', function () {
      if (els.fileInput.files && els.fileInput.files.length) addFiles(els.fileInput.files);
      els.fileInput.value = ''; // permite volver a elegir el mismo archivo
    });
    // Si sueltan un archivo fuera de la zona, el navegador abriría la imagen y se perdería el formulario.
    ['dragover', 'drop'].forEach(function (ev) {
      window.addEventListener(ev, function (e) {
        var types = e.dataTransfer && e.dataTransfer.types ? Array.prototype.slice.call(e.dataTransfer.types) : [];
        if (types.indexOf('Files') !== -1) e.preventDefault();
      });
    });
  }

  // ---------- Verificación anti-bots (Cloudflare Turnstile, opcional) ----------
  function initCaptcha() {
    var cfg = UTD.config.captcha;
    if (!cfg || !cfg.siteKey) return;
    els.captchaBox.hidden = false;
    var s = document.createElement('script');
    s.setAttribute('src', 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit');
    s.async = true;
    s.defer = true;
    s.onload = function () {
      if (!window.turnstile) return;
      state.captchaWidgetId = window.turnstile.render('#captchaWidget', {
        sitekey: cfg.siteKey,
        callback: function (token) { state.captchaToken = token; V.clearError(els.captchaBox); },
        'expired-callback': function () { state.captchaToken = ''; },
        'error-callback': function () { state.captchaToken = ''; }
      });
    };
    s.onerror = function () { ui.toast('No se pudo cargar la verificación de seguridad. Recarga la página.', 'error'); };
    document.head.appendChild(s);
  }

  function resetCaptcha() {
    state.captchaToken = '';
    if (window.turnstile && state.captchaWidgetId !== null) window.turnstile.reset(state.captchaWidgetId);
  }

  // ---------- Envío ----------
  function buildPayload() {
    var type = checked('requesterType');
    var service = checked('serviceType');
    var payload = {
      requesterType: type,
      fullName: val('fullName'),
      email: val('email'),
      phone: val('phone'),
      studentId: type === 'STUDENT' ? val('studentId') : '',
      majorId: type === 'STUDENT' ? Number(val('majorId')) : null,
      campusId: type === 'STUDENT' ? Number(val('campusId')) : null,
      company: type === 'EXTERNAL' ? val('company') : '',
      ruc: type === 'EXTERNAL' ? val('ruc').toUpperCase() : '',
      serviceType: service,
      hardware: null,
      software: null,
      description: val('description'),
      urgency: val('urgency'),
      preferredPaymentMethod: checked('preferredPaymentMethod'),
      acceptedTerms: !!$('#acceptTerms').checked,
      backupAck: service === 'HARDWARE' ? !!$('#backupAck').checked : false,
      captchaToken: state.captchaToken || '',
      hp: String(($('#hp_field') || {}).value || ''),
      elapsedMs: Date.now() - state.shownAt
    };
    if (service === 'HARDWARE') {
      payload.hardware = {
        equipmentType: val('equipmentType'),
        brand: val('brand'),
        model: val('model'),
        serial: val('serial'),
        powersOn: checked('powersOn'),
        accessories: $$('input[name="accessories"]:checked', els.form).map(function (c) { return c.value; }),
        accessoriesOther: val('accessoriesOther')
      };
    } else if (service === 'SOFTWARE') {
      payload.software = { kind: val('softwareKind'), desiredDate: val('desiredDate'), referenceUrl: val('referenceUrl') };
    }
    return payload;
  }

  // Nombre de campo del backend -> elemento del formulario
  var FIELD_ALIASES = {
    'hardware.equipmentType': 'equipmentType', 'hardware.brand': 'brand', 'hardware.model': 'model', 'hardware.serial': 'serial',
    'hardware.powersOn': 'powersOn-YES', 'software.kind': 'softwareKind', 'software.desiredDate': 'desiredDate',
    'software.referenceUrl': 'referenceUrl', 'backupAck': 'backupAck',
    'hardware.accessoriesOther': 'accessoriesOther', 'acceptedTerms': 'acceptTerms'
  };
  function findField(key) {
    var id = FIELD_ALIASES[key] || key;
    return document.getElementById(id) || els.form.querySelector('[name="' + id + '"]');
  }

  function applyServerErrors(fieldErrors) {
    var errors = [];
    Object.keys(fieldErrors).forEach(function (key) {
      var el = findField(key);
      if (el) { V.showError(el, fieldErrors[key]); errors.push({ el: el, key: key, message: fieldErrors[key] }); }
    });
    return errors;
  }

  async function onSubmit(e) {
    e.preventDefault();
    if (state.submitting) return;

    var result = V.validateForm(els.form);
    var errors = result.errors.slice();
    if (!els.captchaBox.hidden && !state.captchaToken) {
      var msg = 'Completa la verificación de seguridad.';
      V.showError(els.captchaBox, msg);
      errors.push({ el: els.captchaBox, key: 'captcha', message: msg });
    }
    if (errors.length) {
      V.renderSummary(els.summary, errors);
      errors[0].el.focus();
      els.form.classList.remove('shake');
      void els.form.offsetWidth; // reinicia la animación
      els.form.classList.add('shake');
      return;
    }
    V.renderSummary(els.summary, []);

    state.submitting = true;
    ui.setBusy(els.submitBtn, true, 'Enviando…');
    try {
      var payload = buildPayload();
      var res = await UTD.api.createTicket(payload, state.files.map(function (f) { return f.file; }));
      showSuccess(res, payload.email);
    } catch (err) {
      var info = ui.describeError(err);
      if (info.fieldErrors) {
        var list = applyServerErrors(info.fieldErrors);
        if (list.length) { V.renderSummary(els.summary, list); list[0].el.focus(); }
        else ui.toast(info.message, 'error');
      } else {
        ui.toast(info.message, 'error');
      }
      resetCaptcha();
    } finally {
      state.submitting = false;
      ui.setBusy(els.submitBtn, false);
    }
  }

  // ---------- Pantalla de confirmación ----------
  function showSuccess(res, email) {
    els.successCode.textContent = res.code;
    els.successEmail.textContent = email;
    // Solo se afirma que se envió un correo si el backend está configurado para enviarlo (config.notifications.email).
    if (UTD.config.notifications && UTD.config.notifications.email) $('#successEmailLead').textContent = 'Te enviamos el código y los avances al correo';
    els.successTrackLink.setAttribute('href', '#/consultar?codigo=' + encodeURIComponent(res.code));
    els.formCard.hidden = true;
    els.success.hidden = false;
    resetForm(true);
    document.title = 'Solicitud recibida · ' + UTD.config.appName;
    $('#successTitle').focus();
    window.scrollTo(0, 0);
  }

  function showForm() {
    els.success.hidden = true;
    els.formCard.hidden = false;
  }

  function resetForm(silent) {
    els.form.reset();
    clearFiles();
    V.clearAll(els.form);
    V.renderSummary(els.summary, []);
    V.clearError(els.captchaBox);
    syncAll();
    updateCounter();
    resetCaptcha();
    state.dirty = false;
    state.shownAt = Date.now();
    if (!silent) els.form.querySelector('input:not([disabled])').focus();
  }

  function updateCounter() {
    var n = els.description.value.length;
    var max = UTD.config.limits.descriptionMax;
    els.descCount.textContent = n + ' / ' + max;
    els.descCount.className = 'hint shrink-0' + (n > max - 100 ? ' !text-amber-800 font-semibold' : '');
  }

  // ---------- Ciclo de vida de la vista ----------
  function init() {
    if (state.initialized) return;
    state.initialized = true;
    els.form = $('#ticketForm');
    els.formCard = $('#ticketFormCard');
    els.success = $('#ticketSuccess');
    els.summary = $('#formErrors');
    els.studentFields = $('#studentFields');
    els.externalFields = $('#externalFields');
    els.hardwareFields = $('#hardwareFields');
    els.softwareFields = $('#softwareFields');
    els.majorId = $('#majorId');
    els.campusId = $('#campusId');
    els.description = $('#description');
    els.descCount = $('#desc-count');
    els.fileInput = $('#fileInput');
    els.filesHelp = $('#filesHelp');
    els.fileList = $('#fileList');
    els.fileErrors = $('#fileErrors');
    els.dropzone = $('#dropzone');
    els.captchaBox = $('#captchaBox');
    els.submitBtn = $('#submitTicketBtn');
    els.successCode = $('#successCode');
    els.successEmail = $('#successEmail');
    els.successTrackLink = $('#successTrackLink');

    fillSelect($('#equipmentType'), objEntries(C.equipmentTypes), 'Selecciona…');
    fillSelect($('#softwareKind'), objEntries(C.softwareKinds), 'Selecciona…');
    var acc = $('#accessoriesList');
    Object.keys(C.accessories).forEach(function (code) {
      acc.appendChild(h('label', { class: 'check-row' }, h('input', { type: 'checkbox', name: 'accessories', value: code }), C.accessories[code]));
    });

    V.attachLive(els.form);
    els.form.addEventListener('submit', onSubmit);
    els.form.addEventListener('input', function () { state.dirty = true; });
    els.form.addEventListener('change', function (e) {
      state.dirty = true;
      if (e.target.name === 'requesterType') syncRequester();
      if (e.target.name === 'serviceType') syncService();
    });
    els.description.addEventListener('input', updateCounter);
    $('#ruc').addEventListener('input', function (e) { e.target.value = e.target.value.toUpperCase().replace(/\s/g, ''); });

    $('#resetFormBtn').addEventListener('click', async function () {
      if (!state.dirty) { resetForm(false); return; }
      var ok = await ui.confirm({
        title: '¿Limpiar el formulario?',
        message: 'Se borrarán todos los datos que escribiste y los archivos que elegiste.',
        confirmText: 'Sí, limpiar', tone: 'danger'
      });
      if (ok) resetForm(false);
    });
    $('#copyCodeBtn').addEventListener('click', function () {
      UTD.utils.copyText(els.successCode.textContent).then(
        function () { ui.toast('Código copiado.', 'success', 2500); },
        function () { ui.toast('No se pudo copiar. Selecciona el código y cópialo manualmente.', 'error'); });
    });
    $('#newAnotherBtn').addEventListener('click', function () {
      showForm();
      document.querySelector('#view-new-ticket h1').focus();
    });

    bindDropzone();
    initCaptcha();
    syncAll();
    updateCounter();
  }

  UTD.views = UTD.views || {};
  UTD.views['new-ticket'] = {
    title: 'Nuevo ticket',
    init: init,
    enter: function () {
      init();
      state.shownAt = state.shownAt || Date.now();
      $('#desiredDate').setAttribute('min', UTD.utils.todayYmd());
      loadCatalogs();
    },
    leave: function () { if (!els.success.hidden) showForm(); }
  };
})(window.UTD = window.UTD || {});
