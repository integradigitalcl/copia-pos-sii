using System;
using GrunflexPOS2.Domain.Abstractions;
using GrunflexPOS2.Models.Entities;

namespace GrunflexPOS2.Infrastructure.Session;

public sealed class UserSessionContext : IUserSessionContext
{
    public Usuario? UsuarioActual { get; set; }
    public Guid CajaActualId { get; set; }
}
