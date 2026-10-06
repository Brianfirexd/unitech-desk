namespace UniTechDesk.Core.Domain;

/// <summary>Códigos de texto que viajan en la API, el front y las llaves de los catálogos de la BD.</summary>
public static class Codes
{
    public static class UserType { public const string Student = "STUDENT"; public const string External = "EXTERNAL"; }
    public static class Service { public const string Hardware = "HARDWARE"; public const string Software = "SOFTWARE"; }

    public static class Status
    {
        public const string Open = "OPEN";
        public const string InReview = "IN_REVIEW";
        public const string Quoted = "QUOTED";
        public const string AwaitingApproval = "AWAITING_APPROVAL";
        public const string InProgress = "IN_PROGRESS";
        public const string WaitingParts = "WAITING_PARTS";
        public const string InDevelopment = "IN_DEVELOPMENT";
        public const string Testing = "TESTING";
        public const string Completed = "COMPLETED";
        public const string Ready = "READY";
        public const string Delivered = "DELIVERED";
        public const string Cancelled = "CANCELLED";
        public const string Unrepairable = "UNREPAIRABLE";
    }

    public static class PaymentStatus
    {
        public const string None = "NONE";
        public const string Pending = "PENDING";
        public const string ProofSent = "PROOF_SENT";
        public const string Paid = "PAID";
    }

    public static class PaymentMethod { public const string Cash = "CASH"; public const string Transfer = "TRANSFER"; public const string Card = "CARD"; }
    public static class Decision { public const string Approved = "APPROVED"; public const string Rejected = "REJECTED"; }
    public static class DecidedBy { public const string Client = "CLIENT"; public const string Staff = "STAFF"; }

    public static class Event
    {
        public const string Created = "CREATED";
        public const string Status = "STATUS";
        public const string Quote = "QUOTE";
        public const string QuoteDecision = "QUOTE_DECISION";
        public const string Payment = "PAYMENT";
        public const string Assign = "ASSIGN";
        public const string Note = "NOTE";
    }

    public static class Author { public const string System = "SYSTEM"; public const string Client = "CLIENT"; public const string Staff = "STAFF"; }
    public static class AttachmentCategory { public const string Request = "SOLICITUD"; public const string PaymentProof = "COMPROBANTE_PAGO"; }

    /// <summary>Plantillas de correo (columna NotificacionCorreo.Plantilla).</summary>
    public static class Mail
    {
        public const string TicketCreated = "TICKET_CREATED";
        public const string StatusUpdate = "STATUS_UPDATE";
        public const string QuotePublished = "QUOTE_PUBLISHED";
        public const string QuoteApproved = "QUOTE_APPROVED";
        public const string QuoteRejected = "QUOTE_REJECTED";
        public const string ProofReceived = "PROOF_RECEIVED";
        public const string PaymentConfirmed = "PAYMENT_CONFIRMED";
        public const string ReadyForPickup = "READY_FOR_PICKUP";
        public const string StaffNewTicket = "STAFF_NEW_TICKET";
        public const string StaffQuoteDecision = "STAFF_QUOTE_DECISION";
        public const string StaffProofReceived = "STAFF_PROOF_RECEIVED";
    }

    public static class MailState { public const string Pending = "PENDING"; public const string Sent = "SENT"; public const string Failed = "FAILED"; }
}
