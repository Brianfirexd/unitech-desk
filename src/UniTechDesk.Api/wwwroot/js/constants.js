/*
 * Catálogos y reglas de negocio del flujo de tickets.
 * Los códigos (OPEN, HARDWARE…) son los valores que viajan al backend; las etiquetas son solo para mostrar.
 * Una sola fuente de verdad: filtros, insignias, modal y validaciones se generan desde aquí.
 */
(function (UTD) {
  'use strict';

  var C = {};

  C.userTypes = { STUDENT: 'Estudiante', EXTERNAL: 'Cliente externo' };
  C.serviceTypes = { HARDWARE: 'Hardware', SOFTWARE: 'Software' };
  C.equipmentTypes = { DESKTOP: 'Computadora de escritorio (PC)', LAPTOP: 'Laptop / portátil' };
  C.softwareKinds = {
    CUSTOM_DEV: 'Desarrollo a la medida',
    FIX_REVIEW: 'Corrección o revisión de software',
    INSTALL_SUPPORT: 'Instalación y soporte',
    CONSULTING: 'Consultoría'
  };
  C.powersOn = { YES: 'Sí, enciende', NO: 'No enciende', SOMETIMES: 'A veces' };
  C.accessories = {
    CHARGER: 'Cargador / fuente',
    MOUSE: 'Mouse',
    KEYBOARD: 'Teclado',
    BAG: 'Maletín o funda',
    CABLES: 'Cables'
  };
  C.urgencies = { LOW: 'Baja', MEDIUM: 'Media', HIGH: 'Alta' };
  C.urgencyRank = { LOW: 1, MEDIUM: 2, HIGH: 3 };
  C.paymentMethods = { CASH: 'Efectivo', TRANSFER: 'Transferencia', CARD: 'Tarjeta' };
  C.paymentStatuses = { NONE: 'Sin cobro', PENDING: 'Pendiente', PROOF_SENT: 'En verificación', PAID: 'Pagado' };
  C.currencies = { NIO: 'Córdobas (C$)', USD: 'Dólares (US$)' };

  // Catálogos que en producción vendrán de la BD (GET /catalogs). Estos valores se usan en la demostración.
  C.defaultCatalogs = {
    majors: [
      { id: 1, name: 'Ingeniería en Sistemas' },
      { id: 2, name: 'Ingeniería Química' },
      { id: 3, name: 'Ingeniería en Computación' },
      { id: 4, name: 'Ingeniería en Telecomunicaciones' },
      { id: 5, name: 'Ingeniería Industrial' },
      { id: 6, name: 'Ingeniería Civil' },
      { id: 7, name: 'Ingeniería Electrónica' },
      { id: 8, name: 'Arquitectura' }
    ],
    campuses: [
      { id: 1, name: 'RUSB' },
      { id: 2, name: 'RUPAP' },
      { id: 3, name: 'CUR ESTELI' }
    ]
  };

  var BOTH = ['HARDWARE', 'SOFTWARE'];

  // Estados del ticket. "services" indica a qué tipo de servicio aplica cada uno.
  // Las clases de color son cadenas completas para que Tailwind las detecte al compilar.
  C.statuses = {
    OPEN:              { label: 'Abierto',              services: BOTH,         final: false, badge: 'bg-yellow-100 text-yellow-900 border-yellow-300' },
    IN_REVIEW:         { label: 'En revisión',          services: BOTH,         final: false, badge: 'bg-blue-100 text-blue-900 border-blue-300' },
    QUOTED:            { label: 'Cotizado',             services: BOTH,         final: false, badge: 'bg-teal-100 text-teal-900 border-teal-300' },
    AWAITING_APPROVAL: { label: 'Esperando aprobación', services: BOTH,         final: false, badge: 'bg-orange-100 text-orange-900 border-orange-300' },
    IN_PROGRESS:       { label: 'En reparación',        services: ['HARDWARE'], final: false, badge: 'bg-indigo-100 text-indigo-900 border-indigo-300' },
    WAITING_PARTS:     { label: 'Esperando repuestos',  services: ['HARDWARE'], final: false, badge: 'bg-amber-100 text-amber-900 border-amber-300' },
    IN_DEVELOPMENT:    { label: 'En desarrollo',        services: ['SOFTWARE'], final: false, badge: 'bg-indigo-100 text-indigo-900 border-indigo-300' },
    TESTING:           { label: 'En pruebas',           services: ['SOFTWARE'], final: false, badge: 'bg-cyan-100 text-cyan-900 border-cyan-300' },
    COMPLETED:         { label: 'Completado',           services: BOTH,         final: false, badge: 'bg-green-100 text-green-900 border-green-300' },
    READY:             { label: 'Listo para entrega',   services: BOTH,         final: false, badge: 'bg-lime-100 text-lime-900 border-lime-300' },
    DELIVERED:         { label: 'Entregado',            services: BOTH,         final: true,  badge: 'bg-emerald-100 text-emerald-900 border-emerald-300' },
    CANCELLED:         { label: 'Cancelado',            services: BOTH,         final: true,  badge: 'bg-red-100 text-red-900 border-red-300' },
    UNREPAIRABLE:      { label: 'No reparable',         services: ['HARDWARE'], final: false, badge: 'bg-slate-200 text-slate-900 border-slate-400' }
  };
  C.statusOrder = ['OPEN', 'IN_REVIEW', 'QUOTED', 'AWAITING_APPROVAL', 'IN_PROGRESS', 'IN_DEVELOPMENT', 'WAITING_PARTS',
    'TESTING', 'UNREPAIRABLE', 'COMPLETED', 'READY', 'DELIVERED', 'CANCELLED'];

  // Transiciones permitidas desde cada estado (se filtran además por tipo de servicio).
  C.transitions = {
    OPEN:              ['IN_REVIEW', 'CANCELLED'],
    IN_REVIEW:         ['QUOTED', 'UNREPAIRABLE', 'CANCELLED'],
    QUOTED:            ['AWAITING_APPROVAL', 'IN_REVIEW', 'CANCELLED'],
    AWAITING_APPROVAL: ['IN_PROGRESS', 'IN_DEVELOPMENT', 'QUOTED', 'CANCELLED'],
    IN_PROGRESS:       ['WAITING_PARTS', 'COMPLETED', 'READY', 'UNREPAIRABLE', 'CANCELLED'],
    WAITING_PARTS:     ['IN_PROGRESS', 'UNREPAIRABLE', 'CANCELLED'],
    IN_DEVELOPMENT:    ['TESTING', 'COMPLETED', 'CANCELLED'],
    TESTING:           ['IN_DEVELOPMENT', 'COMPLETED', 'CANCELLED'],
    COMPLETED:         ['READY', 'DELIVERED'],
    READY:             ['DELIVERED'],
    UNREPAIRABLE:      ['READY', 'CANCELLED'],
    DELIVERED:         [],
    CANCELLED:         ['IN_REVIEW']
  };

  // Pasos que ve el cliente en la consulta de su ticket, según el tipo de servicio.
  C.flows = {
    HARDWARE: [
      { label: 'Recibido',   statuses: ['OPEN'] },
      { label: 'Revisión',   statuses: ['IN_REVIEW'] },
      { label: 'Cotización', statuses: ['QUOTED', 'AWAITING_APPROVAL'] },
      { label: 'Reparación', statuses: ['IN_PROGRESS', 'WAITING_PARTS', 'UNREPAIRABLE'] },
      { label: 'Listo',      statuses: ['COMPLETED', 'READY'] },
      { label: 'Entregado',  statuses: ['DELIVERED'] }
    ],
    SOFTWARE: [
      { label: 'Recibido',   statuses: ['OPEN'] },
      { label: 'Revisión',   statuses: ['IN_REVIEW'] },
      { label: 'Cotización', statuses: ['QUOTED', 'AWAITING_APPROVAL'] },
      { label: 'Desarrollo', statuses: ['IN_DEVELOPMENT', 'TESTING'] },
      { label: 'Listo',      statuses: ['COMPLETED', 'READY'] },
      { label: 'Entregado',  statuses: ['DELIVERED'] }
    ]
  };

  var R = {};

  /** Pasos del flujo del cliente y paso actual (-1 si el ticket está cancelado). */
  R.flowProgress = function (serviceType, status) {
    var steps = C.flows[serviceType] || C.flows.HARDWARE;
    var idx = -1;
    steps.forEach(function (s, i) { if (s.statuses.indexOf(status) !== -1) idx = i; });
    return { steps: steps, index: idx };
  };

  R.statusLabel = function (code) { return (C.statuses[code] || {}).label || code; };

  /** El ticket necesita cobro cuando hay una cotización aprobada con monto mayor a cero. */
  R.requiresPayment = function (ticket) {
    var q = ticket && ticket.quote;
    return !!(q && q.decision === 'APPROVED' && Number(q.amount) > 0);
  };

  /** La cotización solo se puede editar antes de enviarla al cliente. */
  R.quoteEditable = function (ticket) {
    return ticket.status === 'IN_REVIEW' || ticket.status === 'QUOTED';
  };

  /** Estados a los que se puede mover el ticket (según tipo de servicio). */
  R.allowedTransitions = function (ticket) {
    return (C.transitions[ticket.status] || []).filter(function (to) {
      return C.statuses[to].services.indexOf(ticket.serviceType) !== -1;
    });
  };

  /** Estados que exigen escribir un motivo. */
  R.needsReason = function (to) { return to === 'CANCELLED' || to === 'UNREPAIRABLE'; };

  /**
   * Devuelve un texto con el motivo por el que NO se puede pasar a "to", o '' si se puede.
   * El backend aplica las mismas reglas; aquí solo se usan para guiar al usuario.
   */
  R.transitionBlocker = function (ticket, to, rules) {
    rules = rules || (UTD.config && UTD.config.rules) || {};
    if (R.allowedTransitions(ticket).indexOf(to) === -1) {
      return 'No se puede pasar de «' + R.statusLabel(ticket.status) + '» a «' + R.statusLabel(to) + '».';
    }
    var q = ticket.quote;
    if (to === 'QUOTED' && !q) return 'Primero guarda la cotización.';
    if (to === 'AWAITING_APPROVAL' && !q) return 'Primero guarda la cotización para poder enviarla al cliente.';
    if (to === 'QUOTED' && ticket.status === 'AWAITING_APPROVAL' && q && q.decision) {
      return 'El cliente ya respondió la cotización; no se puede retirar.';
    }
    if ((to === 'IN_PROGRESS' || to === 'IN_DEVELOPMENT') && !(q && q.decision === 'APPROVED')) {
      return 'El trabajo inicia cuando el cliente aprueba la cotización.';
    }
    if (to === 'DELIVERED' && rules.requirePaymentBeforeDelivery && R.requiresPayment(ticket) && ticket.paymentStatus !== 'PAID') {
      return 'No se puede entregar hasta confirmar el pago.';
    }
    return '';
  };

  UTD.constants = C;
  UTD.rules = R;
})(window.UTD = window.UTD || {});
