using System;

namespace GrunflexPOS2.Models.Entities
{
    public class Usuario
    {
        public Guid Id { get; set; }

        public string Nombre { get; set; } = "";

        public string Username { get; set; } = "";

        public string Rol { get; set; } = "";

        /// <summary>Hash BCrypt del usuario (columna legacy; no exponer en API).</summary>
        public string Password { get; set; } = "";
    }
}