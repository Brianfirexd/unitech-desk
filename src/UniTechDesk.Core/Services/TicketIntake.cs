using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Domain;
using UniTechDesk.Core.Validation;

namespace UniTechDesk.Core.Services;

/// <summary>Valida y normaliza el formulario de "Nueva solicitud". Mismas reglas y mensajes que UTD.mockApi.validateCreate.</summary>
public static class TicketIntake
{
    public const int DescriptionMin = 15;
    public const int DescriptionMax = 2000;

    public static NewTicket Validate(CreateTicketRequest p, Catalogs catalogs, DateOnly today, DateTime now, string termsVersion)
    {
        var e = new FieldErrors();

        var requesterType = p.RequesterType ?? "";
        var fullName = Rules.Clean(p.FullName);
        var email = Rules.Clean(p.Email);
        var phone = Rules.Clean(p.Phone);

        e.Need("requesterType", WorkflowCatalog.UserTypes.ContainsKey(requesterType), "Selecciona el tipo de solicitante.");
        e.Need("fullName", fullName.Length >= 3, "Escribe tu nombre completo.");
        if (fullName.Length > 120) e.Add("fullName", "Máximo 120 caracteres.");
        e.Need("email", Rules.IsEmail(email), "Correo no válido.");
        e.Need("phone", phone.Length > 0 && Rules.IsPhone(phone), "Teléfono no válido.");

        string? studentId = null, company = null, ruc = null;
        if (requesterType == Codes.UserType.Student)
        {
            studentId = Rules.Clean(p.StudentId);
            e.Need("studentId", Rules.IsStudentId(studentId), "Carnet no válido.");
            e.Need("majorId", p.MajorId is { } m && catalogs.Majors.Any(x => x.Id == m), "Selecciona una carrera.");
            e.Need("campusId", p.CampusId is { } c && catalogs.Campuses.Any(x => x.Id == c), "Selecciona un recinto.");
        }
        else if (requesterType == Codes.UserType.External)
        {
            company = Rules.Clean(p.Company);
            ruc = Rules.Clean(p.Ruc).ToUpperInvariant();
            e.Need("company", company.Length >= 2, "Escribe la razón social.");
            if (company.Length > 150) e.Add("company", "Máximo 150 caracteres.");
            e.Need("ruc", Rules.IsRuc(ruc), "RUC no válido.");
        }

        var serviceType = p.ServiceType ?? "";
        e.Need("serviceType", WorkflowCatalog.ServiceTypes.ContainsKey(serviceType), "Selecciona el tipo de servicio.");

        HardwareInfo? hardware = null;
        SoftwareInfo? software = null;
        if (serviceType == Codes.Service.Hardware)
        {
            var hw = p.Hardware ?? new HardwareDto();
            var equipment = hw.EquipmentType ?? "";
            var brand = Rules.Clean(hw.Brand);
            var model = Rules.Clean(hw.Model);
            var serial = Rules.Clean(hw.Serial);
            var other = Rules.Clean(hw.AccessoriesOther);
            var powersOn = hw.PowersOn ?? "";
            e.Need("hardware.equipmentType", WorkflowCatalog.EquipmentTypes.ContainsKey(equipment), "Selecciona el tipo de equipo.");
            e.Need("hardware.brand", brand.Length > 0, "Escribe la marca.");
            if (brand.Length > 60) e.Add("hardware.brand", "Máximo 60 caracteres.");
            e.Need("hardware.model", model.Length > 0, "Escribe el modelo.");
            if (model.Length > 80) e.Add("hardware.model", "Máximo 80 caracteres.");
            if (serial.Length > 60) e.Add("hardware.serial", "Máximo 60 caracteres.");
            if (other.Length > 200) e.Add("hardware.accessoriesOther", "Máximo 200 caracteres.");
            e.Need("hardware.powersOn", WorkflowCatalog.PowersOn.ContainsKey(powersOn), "Indica si el equipo enciende.");
            e.Need("backupAck", p.BackupAck == true, "Debes confirmar el aviso de respaldo.");
            hardware = new HardwareInfo
            {
                EquipmentType = equipment, Brand = brand, Model = model, Serial = serial, PowersOn = powersOn, AccessoriesOther = other,
                Accessories = (hw.Accessories ?? new()).Where(WorkflowCatalog.Accessories.ContainsKey).Distinct().ToList()
            };
        }
        else if (serviceType == Codes.Service.Software)
        {
            var sw = p.Software ?? new SoftwareDto();
            var kind = sw.Kind ?? "";
            var url = Rules.Clean(sw.ReferenceUrl);
            e.Need("software.kind", WorkflowCatalog.SoftwareKinds.ContainsKey(kind), "Selecciona el tipo de trabajo.");
            if (url.Length > 0)
            {
                e.Need("software.referenceUrl", Rules.IsWebUrl(url), "Enlace no válido.");
                if (url.Length > 300) e.Add("software.referenceUrl", "Máximo 300 caracteres.");
            }
            DateOnly? desired = null;
            var rawDate = (sw.DesiredDate ?? "").Trim();
            if (rawDate.Length > 0)
            {
                if (!Rules.TryParseDate(rawDate, out var d)) e.Add("software.desiredDate", "Fecha no válida.");
                else if (d < today.AddDays(-1)) e.Add("software.desiredDate", "Elige una fecha de hoy en adelante.");   // un día de margen por zonas horarias
                else desired = d;
            }
            software = new SoftwareInfo { Kind = kind, ReferenceUrl = url, DesiredDate = desired };
        }

        var description = Rules.Clean(p.Description, allowNewlines: true);
        e.Need("description", description.Length is >= DescriptionMin and <= DescriptionMax,
            $"La descripción debe tener entre {DescriptionMin} y {DescriptionMax} caracteres.");
        var urgency = p.Urgency ?? "";
        var payMethod = p.PreferredPaymentMethod ?? "";
        e.Need("urgency", WorkflowCatalog.Urgencies.ContainsKey(urgency), "Selecciona la urgencia.");
        e.Need("preferredPaymentMethod", WorkflowCatalog.PaymentMethods.ContainsKey(payMethod), "Selecciona un método de pago.");
        e.Need("acceptedTerms", p.AcceptedTerms == true, "Debes aceptar las condiciones.");

        e.ThrowIfAny("Revisa los datos del formulario.");

        var student = requesterType == Codes.UserType.Student;
        return new NewTicket
        {
            RequesterType = requesterType, FullName = fullName, Email = email, Phone = Rules.StoredPhone(phone),
            StudentId = studentId, MajorId = student ? p.MajorId : null, CampusId = student ? p.CampusId : null,
            Company = company, Ruc = ruc,
            ServiceType = serviceType, Hardware = hardware, Software = software,
            Description = description, Urgency = urgency, PreferredPaymentMethod = payMethod,
            AcceptedTermsAt = now, TermsVersion = termsVersion, Now = now
        };
    }
}
