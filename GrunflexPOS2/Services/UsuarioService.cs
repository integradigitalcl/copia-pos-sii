using System.Linq;
using System.Threading.Tasks;
using Grunflex.Licensing.Security;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Services;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Services
{
    public class UsuarioService
    {
        public Task<Usuario?> Login(string username, string password)
        {
            var user = App.DbContext.Usuarios
                .AsNoTracking()
                .FirstOrDefault(u => u.Username == username);

            if (user == null)
                return Task.FromResult<Usuario?>(null);

            if (!PasswordHasher.TryVerifyAndUpgrade(password, user.Password, out var upgraded))
                return Task.FromResult<Usuario?>(null);

            if (upgraded != null)
                PersistPasswordUpgrade(username, upgraded);

            user.Password = string.Empty;
            return Task.FromResult<Usuario?>(user);
        }

        /// <summary>
        /// SQLite compara GUID en TEXT con collation binario; EF puede emitir distinto casing que filas
        /// insertadas fuera de EF (p. ej. scripts de recuperación). Actualizamos por Username.
        /// </summary>
        private static void PersistPasswordUpgrade(string username, string upgradedHash)
        {
            try
            {
                App.DbContext.Database.ExecuteSqlRaw(
                    "UPDATE Usuarios SET Password = {0} WHERE Username = {1}",
                    upgradedHash,
                    username);
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log("UsuarioService.PersistPasswordUpgrade", ex);
            }
        }
    }
}
