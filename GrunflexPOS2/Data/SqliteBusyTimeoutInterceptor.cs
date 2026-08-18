using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GrunflexPOS2.Data
{
    /// <summary>
    /// Aplica <c>PRAGMA busy_timeout</c> en cada conexión SQLite que abre EF Core.
    /// El keyword "Default Timeout" de la cadena de conexión solo establece el timeout
    /// del SqliteCommand (CommandTimeout) y NO modifica el busy_timeout interno de SQLite,
    /// que es el que decide cuánto esperar cuando otra conexión tiene la BD lockeada.
    ///
    /// Esto es crítico para multicaja sobre SMB: cuando varias terminales y la API local
    /// del servidor tocan la misma BD por red, un SELECT puede ver una ventana de RESERVED
    /// lock muy breve. Sin busy_timeout, falla inmediato con "SQLite Error 5: database is
    /// locked". Con 10s de cortesía, la operación se reintenta dentro del propio motor y
    /// el usuario nunca lo nota.
    /// </summary>
    internal sealed class SqliteBusyTimeoutInterceptor : DbConnectionInterceptor
    {
        private readonly int _millis;

        public SqliteBusyTimeoutInterceptor(int millis = 10000)
        {
            _millis = millis;
        }

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            AplicarBusyTimeout(connection);
            base.ConnectionOpened(connection, eventData);
        }

        public override Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            AplicarBusyTimeout(connection);
            return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
        }

        private void AplicarBusyTimeout(DbConnection connection)
        {
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"PRAGMA busy_timeout = {_millis};";
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // best-effort: no romper si el motor cambia o la BD está caída.
            }
        }
    }
}
