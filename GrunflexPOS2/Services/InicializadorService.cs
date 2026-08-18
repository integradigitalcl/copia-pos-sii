using System;
using System.Linq;
using GrunflexPOS2.Data;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Services.Multicaja;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Services
{
    public class InicializadorService
    {
        private readonly GrunflexDbContext _db;

        public InicializadorService(GrunflexDbContext db)
        {
            _db = db;
        }

        public void Inicializar()
        {
            if (MulticajaRuntime.UseApiOnlyClient)
            {
                if (!_db.Empresas.Any())
                {
                    _db.Empresas.Add(new Empresa
                    {
                        Id = Guid.NewGuid(),
                        Nombre = "Multicaja (local)",
                        FechaCreacion = DateTime.UtcNow
                    });
                    _db.SaveChanges();
                }

                return;
            }

            // 🔹 EMPRESA
            var empresa = _db.Empresas.FirstOrDefault();

            if (empresa == null)
            {
                empresa = new Empresa
                {
                    Id = Guid.NewGuid(),
                    Nombre = "Grunflex",
                    FechaCreacion = DateTime.UtcNow
                };

                _db.Empresas.Add(empresa);
                _db.SaveChanges();
            }

            // CAJA principal del servidor: solo se crea la primera vez, y siempre con
            // nombre "Caja 1 (MachineName)" para que sea identificable cuando el operador
            // mira la lista en la BD central desde una caja adicional. Cajas adicionales
            // son creadas por AutoRegistrarCajaSiCorresponde (App.OnStartup) con "Caja 2",
            // "Caja 3", etc., usando MAX(numero) + 1.
            var caja = _db.Cajas.FirstOrDefault();
            if (caja == null)
            {
                caja = new Caja
                {
                    Id = Guid.NewGuid(),
                    Nombre = $"Caja 1 ({Environment.MachineName})",
                    EmpresaId = empresa.Id,
                    Activa = true,
                    FechaCreacion = DateTime.UtcNow
                };

                _db.Cajas.Add(caja);
                _db.SaveChanges();
            }

            // El primer usuario administrador se crea en el asistente de instalación (CrearUsuarioInicialWindow).
        }
    }
}