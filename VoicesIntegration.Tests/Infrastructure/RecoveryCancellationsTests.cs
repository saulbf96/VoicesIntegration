using VoicesIntegration.Infrastructure.Recovery;

namespace VoicesIntegration.Tests.Infrastructure;

public class RecoveryCancellationsTests
{
    [Fact]
    public void Cancel_SinTrabajoCorriendo_RegresaFalse()
        => Assert.False(new RecoveryCancellations().Cancel(Guid.NewGuid()));

    [Fact]
    public void Cancel_TrabajoCorriendo_CancelaSuToken()
    {
        var reg = new RecoveryCancellations();
        var id = Guid.NewGuid();
        using var cts = reg.Start(id, CancellationToken.None);

        Assert.True(reg.Cancel(id));
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_DespuesDeTerminar_RegresaFalse()
    {
        var reg = new RecoveryCancellations();
        var id = Guid.NewGuid();
        using (reg.Start(id, CancellationToken.None)) reg.End(id);

        Assert.False(reg.Cancel(id));
    }

    [Fact]
    public void Cancel_NoAfectaOtrosTrabajos()
    {
        var reg = new RecoveryCancellations();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        using var ca = reg.Start(a, CancellationToken.None);
        using var cb = reg.Start(b, CancellationToken.None);

        reg.Cancel(a);
        Assert.False(cb.IsCancellationRequested);
    }

    [Fact]
    public void ApagarLaApi_TambienCancelaElTrabajo()
    {
        using var stopping = new CancellationTokenSource();
        using var cts = new RecoveryCancellations().Start(Guid.NewGuid(), stopping.Token);

        stopping.Cancel();
        Assert.True(cts.IsCancellationRequested);
    }
}
