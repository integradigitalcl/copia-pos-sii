using System;
using GrunflexPOS2.Models.Entities;

namespace GrunflexPOS2.Domain.Abstractions;

public interface IUserSessionContext
{
    Usuario? UsuarioActual { get; set; }
    Guid CajaActualId { get; set; }
}
