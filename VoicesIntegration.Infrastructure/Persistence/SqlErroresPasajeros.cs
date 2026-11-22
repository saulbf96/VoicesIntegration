using Microsoft.Data.SqlClient;

namespace VoicesIntegration.Infrastructure.Persistence;

// Errores de conexion o de tiempo de espera del servidor de base de datos (el 205 se pausa unos minutos):
// se pueden reintentar. Llave repetida, permisos o SQL mal escrito NO.
public static class SqlErroresPasajeros
{
    private static readonly HashSet<int> Numeros =
    [
        -2,     // tiempo de espera (incluye "previo al inicio de sesion" / protocolo de enlace)
        -1,     // error al conectar
        2, 53,  // no se encontro / no se pudo abrir conexion con el servidor
        64, 121, 233, 258, 10053, 10054, 10060, 10061,   // conexion cortada por red o por el servidor
        1205,   // interbloqueo (deadlock)
        4221,   // espera de replica
        40143, 40197, 40501, 40613   // servidor ocupado / no disponible
    ];

    public static bool Es(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqlException s)
            {
                if (Numeros.Contains(s.Number)) return true;
                foreach (SqlError err in s.Errors)
                    if (Numeros.Contains(err.Number)) return true;
                if (s.Class >= 20) return true;   // conexion rota por el servidor
            }
            if (e is TimeoutException) return true;
            // Sin conexiones libres en el pool mientras el servidor no respondia
            if (e is InvalidOperationException io && io.Message.Contains("pool", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
