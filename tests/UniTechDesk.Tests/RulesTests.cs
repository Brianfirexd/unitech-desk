using UniTechDesk.Core.Domain;
using UniTechDesk.Core.Validation;

namespace UniTechDesk.Tests;

public class RulesTests
{
    [Theory]
    [InlineData("ana@example.com", true)]
    [InlineData("ana.lopez+tag@sub.example.edu.ni", true)]
    [InlineData("ana@example", false)]
    [InlineData("ana@@example.com", false)]
    [InlineData("ana lopez@example.com", false)]
    [InlineData("@example.com", false)]
    [InlineData("ana@example.c", false)]
    [InlineData("", false)]
    public void Email(string value, bool expected) => Assert.Equal(expected, Rules.IsEmail(value));

    [Fact]
    public void Email_demasiado_largo_se_rechaza() => Assert.False(Rules.IsEmail(new string('a', 250) + "@example.com"));

    [Theory]
    [InlineData("8888-1234", true)]
    [InlineData("(505) 8888 1234", false)]          // el prefijo sin "+" no es un número local de 8 dígitos
    [InlineData("+505 8888-1234", true)]
    [InlineData("22223333", true)]
    [InlineData("57771234", true)]
    [InlineData("+1 305 555 0100", true)]
    [InlineData("12345678", false)]                  // los números nicaragüenses empiezan en 2, 5, 7 u 8
    [InlineData("888812", false)]
    [InlineData("+505 1888 1234", false)]
    [InlineData("abcd", false)]
    [InlineData("", false)]
    public void Telefono(string value, bool expected) => Assert.Equal(expected, Rules.IsPhone(value));

    [Fact]
    public void Telefono_con_digitos_no_ascii_se_rechaza() => Assert.False(Rules.IsPhone("٨٨٨٨١٢٣٤"));   // dígitos arábigos orientales

    [Fact]
    public void Telefono_largo_se_guarda_sin_separadores()
    {
        Assert.Equal("8888-1234", Rules.StoredPhone("8888-1234"));
        var stored = Rules.StoredPhone("+505 (8888) 12-34 . 56 . 78 . 90");
        Assert.True(stored.Length <= 20);
        Assert.DoesNotContain(" ", stored);
    }

    [Theory]
    [InlineData("20231234", true)]
    [InlineData("2023-1234", true)]
    [InlineData("12345", false)]
    [InlineData("1234567890123456", false)]
    [InlineData("2023 1234", false)]
    public void Carnet(string value, bool expected) => Assert.Equal(expected, Rules.IsStudentId(value));

    [Theory]
    [InlineData("J0310000012345", true)]
    [InlineData("j0310000012345", true)]
    [InlineData("J031000001234", false)]
    [InlineData("J03100000123456", false)]
    [InlineData("J031000001234-", false)]
    public void Ruc(string value, bool expected) => Assert.Equal(expected, Rules.IsRuc(value));

    [Theory]
    [InlineData("UTD-1001", true)]
    [InlineData("utd-1001", true)]
    [InlineData("UTD-12", false)]
    [InlineData("UTD-123456789", false)]
    [InlineData("UTD1001", false)]
    [InlineData("1001", false)]
    [InlineData("UTD-1001; DROP TABLE Ticket", false)]
    public void CodigoDeTicket(string value, bool expected) => Assert.Equal(expected, Rules.IsTicketCode(value));

    [Fact]
    public void NormalizarCodigo() => Assert.Equal("UTD-1001", Rules.NormalizeCode("  utd-1001 "));

    [Theory]
    [InlineData("https://example.com/ref", true)]
    [InlineData("http://example.com", true)]
    [InlineData("HTTPS://EXAMPLE.COM", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html;base64,AAAA", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("//example.com", false)]
    [InlineData("https://", false)]
    [InlineData("example.com", false)]
    public void UrlWeb(string value, bool expected) => Assert.Equal(expected, Rules.IsWebUrl(value));

    [Theory]
    [InlineData("2026-10-06", true)]
    [InlineData("2026-02-30", false)]
    [InlineData("06/10/2026", false)]
    [InlineData("2026-1-6", false)]
    [InlineData("", false)]
    public void Fecha(string value, bool expected) => Assert.Equal(expected, Rules.TryParseDate(value, out _));

    [Fact]
    public void Clean_recorta_y_quita_caracteres_de_control()
    {
        Assert.Equal("Ana López", Rules.Clean("  Ana\u0000 López\u0007  "));
        Assert.Equal("a b", Rules.Clean("a\tb"));
        Assert.Equal("", Rules.Clean(null));
        Assert.Equal("", Rules.Clean("   "));
    }

    [Fact]
    public void Clean_conserva_saltos_de_linea_solo_si_se_piden()
    {
        Assert.Equal("línea 1 línea 2", Rules.Clean("línea 1\r\nlínea 2"));
        Assert.Equal("línea 1\nlínea 2", Rules.Clean("línea 1\r\nlínea 2", allowNewlines: true));
    }

    [Fact]
    public void Clean_quita_caracteres_de_direccion_de_texto()
    {
        // U+202E (RLO) sirve para disfrazar "exe.png" como "gnp.exe" o invertir un enlace
        Assert.Equal("facturaexe.pdf", Rules.Clean("factura\u202Eexe.pdf"));
        Assert.Equal("ab", Rules.Clean("a\u2066b\u2069"));
        Assert.Equal("ab", Rules.Clean("a\uFEFFb"));
        Assert.Equal("ab", Rules.Clean("a\u2028b"));
    }

    [Fact]
    public void Clean_normaliza_a_FormC()
    {
        // "e" + acento combinante → "é"
        Assert.Equal("café", Rules.Clean("café"));
    }

    [Fact]
    public void Clean_no_falla_con_sustitutos_sueltos()
    {
        // Un JSON puede traer "\ud800" suelto; no debe producir un error 500.
        var result = Rules.Clean("a\uD800b");
        Assert.NotNull(result);
        Assert.StartsWith("a", result);
    }
}

public class MoneyTests
{
    [Theory]
    [InlineData(850, "NIO", "C$ 850.00")]
    [InlineData(3000, "NIO", "C$ 3,000.00")]
    [InlineData(1234567.5, "USD", "US$ 1,234,567.50")]
    [InlineData(0, "NIO", "C$ 0.00")]
    public void Formato(double amount, string currency, string expected) =>
        Assert.Equal(expected, UniTechDesk.Core.Services.Money.Format((decimal)amount, currency));
}

public class WorkflowTests
{
    [Fact]
    public void Hay_13_estados_y_todas_las_transiciones_apuntan_a_estados_existentes()
    {
        Assert.Equal(13, WorkflowCatalog.Statuses.Count);
        foreach (var (src, tos) in WorkflowCatalog.Transitions)
        {
            Assert.True(WorkflowCatalog.Statuses.ContainsKey(src), src);
            foreach (var to in tos) Assert.True(WorkflowCatalog.Statuses.ContainsKey(to), $"{src} → {to}");
        }
        Assert.Equal(WorkflowCatalog.Statuses.Count, WorkflowCatalog.Transitions.Count);
    }

    [Fact]
    public void Cada_transicion_sirve_para_al_menos_un_servicio_comun()
    {
        foreach (var (src, tos) in WorkflowCatalog.Transitions)
            foreach (var to in tos)
            {
                var common = WorkflowCatalog.Statuses[src].Services.Intersect(WorkflowCatalog.Statuses[to].Services).ToList();
                Assert.True(common.Count > 0, $"{src} → {to} no comparte servicio");
            }
    }

    [Fact]
    public void Los_estados_finales_no_tienen_salida_salvo_cancelado_que_se_puede_reabrir()
    {
        Assert.Empty(WorkflowCatalog.Transitions[Codes.Status.Delivered]);
        Assert.Equal(new[] { Codes.Status.InReview }, WorkflowCatalog.Transitions[Codes.Status.Cancelled]);
    }

    [Fact]
    public void Transiciones_permitidas_dependen_del_servicio()
    {
        Assert.Contains(Codes.Status.InProgress, Workflow.AllowedTransitions("HARDWARE", Codes.Status.AwaitingApproval));
        Assert.DoesNotContain(Codes.Status.InDevelopment, Workflow.AllowedTransitions("HARDWARE", Codes.Status.AwaitingApproval));
        Assert.Contains(Codes.Status.InDevelopment, Workflow.AllowedTransitions("SOFTWARE", Codes.Status.AwaitingApproval));
        Assert.DoesNotContain(Codes.Status.Unrepairable, Workflow.AllowedTransitions("SOFTWARE", Codes.Status.InReview));
    }

    [Fact]
    public void Solo_cancelado_y_no_reparable_exigen_motivo()
    {
        foreach (var s in WorkflowCatalog.Statuses.Keys)
            Assert.Equal(s is "CANCELLED" or "UNREPAIRABLE", Workflow.NeedsReason(s));
    }

    [Fact]
    public void Los_estados_de_trabajo_son_los_cuatro_que_exige_el_trigger()
    {
        var work = WorkflowCatalog.Statuses.Keys.Where(Workflow.IsWorkStatus).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "IN_DEVELOPMENT", "IN_PROGRESS", "TESTING", "WAITING_PARTS" }, work);
    }

    [Fact]
    public void Catalogos_coinciden_con_los_de_la_base()
    {
        Assert.Equal(new[] { "CASH", "CARD", "TRANSFER" }.OrderBy(x => x), WorkflowCatalog.PaymentMethods.Keys.OrderBy(x => x));
        Assert.Equal(new[] { "NIO", "USD" }.OrderBy(x => x), WorkflowCatalog.Currencies.Keys.OrderBy(x => x));
        Assert.Equal(new[] { "NONE", "PENDING", "PROOF_SENT", "PAID" }.OrderBy(x => x), WorkflowCatalog.PaymentStatuses.Keys.OrderBy(x => x));
        Assert.Equal(new[] { "LOW", "MEDIUM", "HIGH" }.OrderBy(x => x), WorkflowCatalog.Urgencies.Keys.OrderBy(x => x));
    }
}
