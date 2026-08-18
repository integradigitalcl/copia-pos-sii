using Grunflex.Licensing.Security;
using Grunflex.API.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace Grunflex.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UsuariosController : ControllerBase
    {
        private static List<Usuario> usuarios = new List<Usuario>();

        private string GenerarPassword(int length = 6)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var random = new Random();
            var password = new StringBuilder();

            for (int i = 0; i < length; i++)
                password.Append(chars[random.Next(chars.Length)]);

            return password.ToString();
        }

        private static Usuario ToPublicDto(Usuario u) => new()
        {
            Id = u.Id,
            Username = u.Username,
            Nombre = u.Nombre,
            Rol = u.Rol,
            Password = string.Empty
        };

        [HttpGet]
        public IActionResult Get()
        {
            return Ok(usuarios.Select(ToPublicDto));
        }

        [HttpPost]
        public IActionResult Post([FromBody] Usuario usuario)
        {
            usuario.Id = Guid.NewGuid();
            var plain = GenerarPassword();
            usuario.Password = PasswordHasher.Hash(plain);

            usuarios.Add(usuario);

            return Ok(new
            {
                usuario.Id,
                usuario.Username,
                usuario.Nombre,
                usuario.Rol,
                temporaryPassword = plain
            });
        }

        [HttpPut("{id}")]
        public IActionResult Put(Guid id, [FromBody] Usuario usuario)
        {
            var existente = usuarios.FirstOrDefault(x => x.Id == id);

            if (existente == null)
                return NotFound();

            existente.Nombre = usuario.Nombre;
            existente.Username = usuario.Username;
            existente.Rol = usuario.Rol;

            return Ok(ToPublicDto(existente));
        }

        [HttpDelete("{id}")]
        public IActionResult Delete(Guid id)
        {
            var usuario = usuarios.FirstOrDefault(x => x.Id == id);

            if (usuario == null)
                return NotFound();

            usuarios.Remove(usuario);
            return Ok();
        }

        [HttpGet("{id}/password")]
        public IActionResult GetPassword(Guid id) =>
            StatusCode(StatusCodes.Status410Gone, "Las contraseñas ya no se exponen por API.");

        [HttpPost("{id}/reset-password")]
        public IActionResult ResetPassword(Guid id)
        {
            var usuario = usuarios.FirstOrDefault(x => x.Id == id);

            if (usuario == null)
                return NotFound();

            var plain = GenerarPassword();
            usuario.Password = PasswordHasher.Hash(plain);

            return Ok(new { temporaryPassword = plain });
        }

        [HttpPost("login")]
        public IActionResult Login([FromBody] Usuario login)
        {
            var usuario = usuarios.FirstOrDefault(x => x.Username == login.Username);
            if (usuario == null || !PasswordHasher.Verify(login.Password, usuario.Password))
                return Unauthorized("Credenciales incorrectas");

            return Ok(ToPublicDto(usuario));
        }
    }
}
