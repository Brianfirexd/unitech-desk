using UniTechDesk.Api.Cli;
using UniTechDesk.Api.Infrastructure;
using UniTechDesk.TestSupport;

namespace UniTechDesk.Tests;

/// <summary>El comando check avisa de las cuentas de demostración que siguen con la contraseña pública.</summary>
public class DemoAccountsTests
{
    private static readonly BCryptPasswordHasher Hasher = new();

    [Fact]
    public async Task Avisa_de_las_cuentas_de_demostracion_activas_con_la_contraseña_publica()
    {
        var store = new InMemoryStore(TimeProvider.System);
        var demo = Hasher.Hash(Commands.DemoPassword);
        store.AddStaff("admin", "Administración", "ADMIN", demo);
        store.AddStaff("tecnico1", "Técnico 1", "TECNICO", demo);

        var found = await Commands.FindDemoAccountsAsync(store, Hasher, default);
        Assert.Equal(new[] { "admin", "tecnico1" }, found);
    }

    [Fact]
    public async Task No_avisa_si_ya_cambiaron_la_contraseña_o_la_cuenta_esta_desactivada()
    {
        var store = new InMemoryStore(TimeProvider.System);
        store.AddStaff("admin", "Administración", "ADMIN", Hasher.Hash("Otra-contraseña-larga-2026!"));
        store.AddStaff("tecnico1", "Técnico 1", "TECNICO", Hasher.Hash(Commands.DemoPassword), active: false);
        store.AddStaff("maria", "María", "ADMIN", Hasher.Hash(Commands.DemoPassword));   // no es una cuenta de demostración

        Assert.Empty(await Commands.FindDemoAccountsAsync(store, Hasher, default));
    }
}
