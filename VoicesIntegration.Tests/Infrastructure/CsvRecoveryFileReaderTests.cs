using System.IO.Compression;
using System.Text;
using VoicesIntegration.Application.Abstractions.Recovery;
using VoicesIntegration.Domain.Common;
using VoicesIntegration.Infrastructure.Recovery;

namespace VoicesIntegration.Tests.Infrastructure;

public sealed class CsvRecoveryFileReaderTests : IDisposable
{
    private const string Header = "campaign_id,campaign_name,channel,identifier,phone_number,type,data,message,call_attempts,duration_minutes,duration_seconds,duration_transfer_minutes,duration_transfer_seconds,credits,dialed_at,failed_at,sip_code,sip_description,transfer,sip_code_transfer,sip_description_transfer,a_num";

    // Igual que el reporte real: "data" es JSON (con comas y comillas dobles escapadas) y message trae SSML.
    private const string Fila1 = "6aea,\"BLASTER MOVILIDAD 03112026\",BLASTER,id-1,5554851787,FIJO,\"{\"\"prefijo\"\":\"\"19086\"\",\"\"saldo\"\":\"\"$47,060.00\"\"}\",\"<speak>Hola, cliente</speak>\",1,0,33,0,0,1,2026-11-03 13:31:25,,200,Exitoso,No,,-,5512345678";
    private const string Fila2 = "6aea,\"BLASTER MOVILIDAD 03112026\",BLASTER,id-2,5554851788,MOVIL,\"{}\",\"texto\",2,1,10,2,9,3,,2026-11-03 18:20:00,480,No contestada,Sí,200,Exitoso,";
    private const string FilaSinMarcar = "6aea,\"X\",BLASTER,id-3,5554851789,MOVIL,,,0,0,0,0,0,0,,,,,No,,,";

    private readonly string _dir = Directory.CreateTempSubdirectory("voices-csv-").FullName;

    [Fact]
    public async Task LeeElFormatoReal_ConJsonComasYComillas()
    {
        var path = Write("r.csv", Header, Fila1, Fila2, FilaSinMarcar);

        var rows = await Read(path);

        Assert.Equal(3, rows.Count);
        var r1 = rows[0];
        Assert.Equal("id-1", r1.Identifier);
        Assert.Equal("{\"prefijo\":\"19086\",\"saldo\":\"$47,060.00\"}", r1.Data);
        Assert.Equal(new DateTime(2026, 11, 3, 13, 31, 25), r1.DialedAt);   // hora México, tal cual
        Assert.Null(r1.FailedAt);
        Assert.Equal(200, r1.SipCode);
        Assert.False(r1.Transfer);
        Assert.Null(r1.SipDescriptionTransfer);   // "-" = vacío
        Assert.Equal("5512345678", r1.ANum);

        var r2 = rows[1];
        Assert.True(r2.Transfer);                 // "Sí"
        Assert.Equal(200, r2.SipCodeTransfer);
        Assert.Equal(9, r2.DurationTransferSeconds);
        Assert.Null(rows[2].SipCode);             // no marcada por Voices
    }


    [Fact]
    public async Task LeeDentroDeUnZip()
    {
        var csv = Write("r.csv", Header, Fila1);
        var zip = Path.Combine(_dir, "r.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) z.CreateEntryFromFile(csv, "reporte.csv");

        var rows = await Read(zip);

        Assert.Equal("id-1", Assert.Single(rows).Identifier);
    }

    [Fact]
    public async Task ArchivoQueNoEsDeVoices_DiceQueColumnasFaltan()
    {
        var path = Write("otro.csv", "nombre,telefono", "Juan,555");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Read(path));

        Assert.Contains("identifier", ex.Message);
    }

    [Fact]
    public async Task FilaSinIdentifier_SeOmite()
    {
        var path = Write("r.csv", Header, ",X,BLASTER,,555,,,,,,,,,,,,,,,,,", Fila1);

        Assert.Single(await Read(path));

    }


    [Fact]
    public async Task ModoLigero_NoLeeTextosLargos_YFiltroSoloDevuelveLasPedidas()
    {
        var path = Write("r.csv", Header, Fila1, Fila2);
        var list = new List<RecoveryRow>();
        await foreach (var r in new CsvRecoveryFileReader().ReadAsync(path, light: true, include: id => id == "id-2", CancellationToken.None)) list.Add(r);

        var row = Assert.Single(list);
        Assert.Equal("id-2", row.Identifier);
        Assert.Null(row.Data);
        Assert.Null(row.Message);
        Assert.Equal(480, row.SipCode);
    }

    [Fact]
    public async Task Sms_AceptaNombresAlternativos_YConvierteFechaConZona()
    {
        var path = Write("sms.csv", "ID,Telefono,Mensaje,Estado,Operador,Fecha Envio,datetime",
            "m-1,5512345678,\"Hola, Núñez\",sent,TELCEL,2026-11-05 10:00:00,2026-11-05T16:01:00Z");

        var list = new List<SmsRecoveryRow>();
        await foreach (var r in new CsvRecoveryFileReader().ReadSmsAsync(path, CancellationToken.None)) list.Add(r);

        var row = Assert.Single(list);
        Assert.Equal("m-1", row.MessageId);
        Assert.Equal("5512345678", row.To);
        Assert.Equal("Hola, Núñez", row.Message);
        Assert.Equal("SENT", row.Status);
        Assert.Equal(new DateTime(2026, 11, 5, 10, 0, 0), row.SentAt);
        Assert.Equal(new DateTime(2026, 11, 5, 10, 1, 0), row.StatusAt);   // 16:01 UTC = 10:01 México
    }

    [Fact]
    public async Task Sms_ConArchivoDeLlamadas_DiceQueElTipoEstaMal()
    {
        var path = Write("r.csv", Header, Fila1);

        var ex = await Assert.ThrowsAsync<DomainException>(async () =>
        {
            await foreach (var _ in new CsvRecoveryFileReader().ReadSmsAsync(path, CancellationToken.None)) { }
        });

        Assert.Contains("LLAMADAS", ex.Message);
    }

    private string Write(string name, params string[] lines)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, string.Join("\n", lines) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return p;
    }


    private static async Task<List<RecoveryRow>> Read(string path)
    {
        var list = new List<RecoveryRow>();
        await foreach (var r in new CsvRecoveryFileReader().ReadAsync(path, light: false, include: null, CancellationToken.None)) list.Add(r);
        return list;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }


}
