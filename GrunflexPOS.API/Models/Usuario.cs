using System;

namespace Grunflex.API.Models
{
    public class Usuario
    {
        public Guid Id { get; set; }

        public string Nombre { get; set; } = "";

        public string Username { get; set; } = "";

        public string Rol { get; set; } = "";

        public string Password { get; set; } = "";
    }
}