using VoicesIntegration.Infrastructure.Monitoring;

namespace VoicesIntegration.Tests.Infrastructure;

public class LimpiezaLogsWorkerTests
{
    private static readonly TimeSpan Once = new(23, 0, 0);

    [Theory]
    [InlineData("2026-11-09 22:59", "2026-11-02 23:00")]
    [InlineData("2026-11-09 23:00", "2026-11-09 23:00")]
    [InlineData("2026-11-11 09:00", "2026-11-09 23:00")]
    [InlineData("2026-11-15 23:59", "2026-11-09 23:00")]
    public void UltimaProgramada_EsElLunesMasRecienteALas23(string ahora, string esperada)
        => Assert.Equal(DateTime.Parse(esperada), LimpiezaLogsWorker.UltimaProgramada(DateTime.Parse(ahora), DayOfWeek.Monday, Once));

    [Fact]
    public void ParaBorrar_TodoMenosElDelLunes_YNoTocaOtrosArchivos()
    {
        var archivos = new[]
        {
            Path.Combine("logs", "voices-20261103.log"), Path.Combine("logs", "voices-20261108.log"), Path.Combine("logs", "voices-20261108_001.log"),
            Path.Combine("logs", "voices-20261109.log"), Path.Combine("logs", "voices-20261109_001.log"),
            Path.Combine("logs", "voices-20261112.log"),
            Path.Combine("logs", "otro.log"), Path.Combine("logs", ".ultima-limpieza")
        };

        var borrar = LimpiezaLogsWorker.ParaBorrar(archivos, new DateOnly(2026, 11, 9)).Select(Path.GetFileName).ToList();

        Assert.Equal(["voices-20261103.log", "voices-20261108.log", "voices-20261108_001.log"], borrar);
    }
}
