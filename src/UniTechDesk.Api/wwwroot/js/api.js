/*
 * Capa de acceso a datos. Las vistas SOLO hablan con UTD.api; nunca usan fetch ni el arreglo de tickets.
 *  - useMock = true  -> delega en js/mock-api.js (datos simulados en el navegador).
 *  - useMock = false -> llama a la API REST real (ver README para el contrato de endpoints).
 * Para pasar a producción: config.useMock = false y quitar <script src="js/mock-api.js">.
 */
(function (UTD) {
  'use strict';

  /** Error uniforme: status HTTP (0 = sin conexión), mensaje en español y errores por campo. */
  function ApiError(status, message, fieldErrors) {
    this.name = 'ApiError';
    this.status = status;
    this.message = message || 'Error de comunicación con el servidor.';
    this.fieldErrors = fieldErrors || null;
  }
  ApiError.prototype = Object.create(Error.prototype);
  ApiError.prototype.constructor = ApiError;
  UTD.ApiError = ApiError;

  // ---------- Sesión del personal ----------
  // El JWT se guarda en sessionStorage (se borra al cerrar la pestaña). Si prefieres máxima seguridad,
  // el backend puede entregar el token en una cookie HttpOnly + SameSite y aquí no se guardaría nada.
  var SESSION_KEY = 'utd.session';
  var memorySession = null;
  var expiryTimer = null;

  function readSession() {
    if (memorySession) return memorySession;
    try {
      var raw = sessionStorage.getItem(SESSION_KEY);
      if (!raw) return null;
      var s = JSON.parse(raw);
      if (!s || !s.token || new Date(s.expiresAt).getTime() <= Date.now()) { sessionStorage.removeItem(SESSION_KEY); return null; }
      memorySession = s;
      return s;
    } catch (e) { return null; }
  }

  UTD.session = {
    get: readSession,
    token: function () { var s = readSession(); return s ? s.token : null; },
    isActive: function () { return !!readSession(); },
    user: function () { var s = readSession(); return s ? s.user : null; },
    start: function (data) {
      memorySession = { token: data.token, expiresAt: data.expiresAt, user: data.user };
      try { sessionStorage.setItem(SESSION_KEY, JSON.stringify(memorySession)); } catch (e) { /* sin almacenamiento: queda en memoria */ }
      clearTimeout(expiryTimer);
      var ms = new Date(data.expiresAt).getTime() - Date.now();
      if (ms > 0 && ms < 2147483647) {
        expiryTimer = setTimeout(function () { UTD.session.end('expired'); }, ms);
      }
    },
    end: function (reason) {
      memorySession = null;
      clearTimeout(expiryTimer);
      try { sessionStorage.removeItem(SESSION_KEY); } catch (e) { /* nada */ }
      document.dispatchEvent(new CustomEvent('utd:session-ended', { detail: { reason: reason || 'logout' } }));
    }
  };

  // ---------- HTTP real ----------
  function parseProblem(status, body) {
    // Formato ProblemDetails / ValidationProblemDetails de ASP.NET Core: { title, detail, errors: { campo: [msgs] } }
    var fieldErrors = null;
    if (body && body.errors && typeof body.errors === 'object') {
      fieldErrors = {};
      Object.keys(body.errors).forEach(function (k) {
        var v = body.errors[k];
        var key = k.replace(/^\$\./, '');
        key = key.charAt(0).toLowerCase() + key.slice(1);
        fieldErrors[key] = Array.isArray(v) ? v[0] : String(v);
      });
    }
    var message = (body && (body.detail || body.title || body.message)) || null;
    return new ApiError(status, message, fieldErrors);
  }

  function request(method, path, opts) {
    opts = opts || {};
    var headers = { 'Accept': 'application/json' };
    var init = { method: method, headers: headers };
    if (opts.auth) {
      var t = UTD.session.token();
      if (!t) return Promise.reject(new ApiError(401, 'Tu sesión expiró. Inicia sesión de nuevo.'));
      headers['Authorization'] = 'Bearer ' + t;
    }
    if (opts.form) {
      init.body = opts.form; // FormData: el navegador pone el Content-Type con su boundary
    } else if (opts.body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(opts.body);
    }
    var url = UTD.config.apiBaseUrl.replace(/\/$/, '') + path;
    if (opts.query) {
      var qs = Object.keys(opts.query)
        .filter(function (k) { return opts.query[k] !== '' && opts.query[k] !== null && opts.query[k] !== undefined; })
        .map(function (k) { return encodeURIComponent(k) + '=' + encodeURIComponent(opts.query[k]); }).join('&');
      if (qs) url += '?' + qs;
    }
    return fetch(url, init).then(function (res) {
      if (res.status === 204) return null;
      var ct = res.headers.get('content-type') || '';
      var parse = ct.indexOf('json') !== -1 ? res.json() : res.text().then(function () { return null; });
      return parse.catch(function () { return null; }).then(function (body) {
        if (res.ok) return body;
        if (res.status === 401 && opts.auth) UTD.session.end('expired');
        throw parseProblem(res.status, body);
      });
    }, function () {
      throw new ApiError(0, 'No pudimos conectar con el servidor.');
    });
  }

  function formWith(data, files, fileField) {
    var fd = new FormData();
    fd.append('data', JSON.stringify(data));
    (files || []).forEach(function (f) { fd.append(fileField || 'files', f, f.name); });
    return fd;
  }

  var http = {
    getCatalogs: function () { return request('GET', '/catalogs'); },
    createTicket: function (payload, files) { return request('POST', '/tickets', { form: formWith(payload, files) }); },
    trackTicket: function (code, email) { return request('POST', '/tickets/track', { body: { code: code, email: email } }); },
    decideQuote: function (code, email, decision) {
      return request('POST', '/tickets/' + encodeURIComponent(code) + '/quote-decision', { body: { email: email, decision: decision } });
    },
    submitPaymentProof: function (code, email, proof) {
      var fd = formWith({ email: email, reference: proof.reference }, proof.file ? [proof.file] : [], 'file');
      return request('POST', '/tickets/' + encodeURIComponent(code) + '/payment-proof', { form: fd });
    },
    login: function (username, password) { return request('POST', '/auth/login', { body: { username: username, password: password } }); },

    listTickets: function (params) { return request('GET', '/admin/tickets', { auth: true, query: params }); },
    getStats: function () { return request('GET', '/admin/tickets/stats', { auth: true }); },
    getTicket: function (id) { return request('GET', '/admin/tickets/' + id, { auth: true }); },
    changeStatus: function (id, body) { return request('POST', '/admin/tickets/' + id + '/status', { auth: true, body: body }); },
    saveQuote: function (id, body) { return request('PUT', '/admin/tickets/' + id + '/quote', { auth: true, body: body }); },
    recordQuoteDecision: function (id, body) { return request('POST', '/admin/tickets/' + id + '/quote-decision', { auth: true, body: body }); },
    setPayment: function (id, body) { return request('POST', '/admin/tickets/' + id + '/payment', { auth: true, body: body }); },
    assign: function (id, staffId) { return request('PUT', '/admin/tickets/' + id + '/assignee', { auth: true, body: { staffId: staffId } }); },
    addNote: function (id, text) { return request('POST', '/admin/tickets/' + id + '/notes', { auth: true, body: { text: text } }); },
    listStaff: function () { return request('GET', '/admin/staff', { auth: true }); }
  };

  // ---------- Fachada ----------
  // Cada método decide en el momento de la llamada si usa la simulación o la API real.
  var api = {};
  Object.keys(http).forEach(function (name) {
    api[name] = function () {
      var impl = UTD.config.useMock && UTD.mockApi ? UTD.mockApi : http;
      if (UTD.config.useMock && !UTD.mockApi) {
        return Promise.reject(new ApiError(0, 'Falta cargar js/mock-api.js (o cambia config.useMock a false).'));
      }
      return impl[name].apply(impl, arguments);
    };
  });

  UTD.api = api;
})(window.UTD = window.UTD || {});
