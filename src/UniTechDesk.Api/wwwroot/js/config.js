/*
 * Configuración del frontend. Es lo único que deberías tocar al conectar el backend.
 */
(function (UTD) {
  'use strict';

  UTD.config = {
    appName: 'UniTech Desk',

    // true  = datos simulados en el navegador (localStorage). Útil para desarrollar y demostrar el front.
    // false = usa la API real en apiBaseUrl. Al pasar a producción, quita también <script src="js/mock-api.js">.
    useMock: false,

    // URL base de tu API REST (C# .NET Core). Ejemplo: 'https://localhost:7001/api'
    apiBaseUrl: '/api',

    // Latencia simulada (ms) de la API de demostración: [mínimo, máximo].
    mockLatencyMs: [150, 350],

    contact: {
      // WhatsApp en formato internacional sin "+" ni espacios.
      whatsapp: '50576764946',
      whatsappText: 'Hola, quisiera consultar sobre un servicio tecnológico.',
      email: '',                        // Si lo dejas vacío, no se muestra.
      hours: '',                        // Ej.: 'Lunes a viernes, 8:00 a.m. a 5:00 p.m.' (vacío = no se muestra)
      location: 'Facultad de Ciencias y Sistemas - DACTIC - RUSB'
    },

    // Reglas de negocio que el front aplica para guiar al usuario (el backend debe aplicarlas también).
    rules: {
      requirePaymentBeforeDelivery: true
    },

    // Política mostrada en la página de privacidad y condiciones. Revísala con la universidad.
    policy: {
      unclaimedDays: 30
    },

    // El front NO envía correos: eso lo hace el backend (SMTP, SendGrid, etc.). Pon email en true solo cuando el backend ya
    // envíe el código y los avisos al correo del cliente; así la pantalla de confirmación no promete algo que no ocurre.
    notifications: {
      email: true
    },

    limits: {
      maxFiles: 5,
      maxFileSizeMB: 5,
      descriptionMin: 15,
      descriptionMax: 2000,
      pageSize: 10
    },

    // Verificación anti-bots. Con siteKey de Cloudflare Turnstile se muestra el widget y se envía el token
    // en "captchaToken". Sin siteKey, el formulario solo usa un campo trampa; el backend debe aplicar
    // límite de peticiones (rate limiting) de todos modos.
    captcha: {
      provider: 'turnstile',
      siteKey: ''
    },

    login: {
      maxAttempts: 5,        // intentos fallidos antes de bloquear el botón unos segundos (solo UX)
      lockSeconds: 30
    },

    // Expresiones de validación de formato (ajústalas a los formatos reales de la universidad).
    patterns: {
      studentId: '^[A-Za-z0-9-]{6,15}$',
      ruc: '^[A-Za-z0-9]{14}$'
    }
  };
})(window.UTD = window.UTD || {});
