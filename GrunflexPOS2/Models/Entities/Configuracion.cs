using System;

namespace GrunflexPOS2.Models.Entities
{
    public class Configuracion
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Clave { get; set; } = string.Empty;

        public string Valor { get; set; } = string.Empty;
    }
}