using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using GrunflexPOS2.Data;
using GrunflexPOS2.Domain.Abstractions;
using GrunflexPOS2.Models.Entities;

namespace GrunflexPOS2.Services
{
    public class CajaService
    {
        private readonly GrunflexDbContext _db;
        private readonly IUserSessionContext _session;

        public CajaService(GrunflexDbContext db)
            : this(db, App.SessionContext)
        {
        }

        public CajaService(GrunflexDbContext db, IUserSessionContext session)
        {
            _db = db;
            _session = session;
        }

        public CajaSesion? ObtenerSesionAbierta(Guid cajaId)
        {
            return _db.CajaSesiones
                .FirstOrDefault(x => x.CajaId == cajaId && x.Abierta);
        }

        public CajaSesion AbrirCaja(Guid cajaId, decimal montoInicial)
        {
            var existe = _db.CajaSesiones
                .Any(x => x.CajaId == cajaId && x.Abierta);

            if (existe)
                throw new Exception("Ya hay una caja abierta");

            if (_session.UsuarioActual == null)
                throw new Exception("No hay usuario logueado");

            // Número secuencial real: posición de esta caja dentro del set ordenado por
            // FechaCreacion. La primera caja registrada (típicamente la principal) es 1;
            // las adicionales son 2, 3, 4... Antes esto se calculaba como
            //   Math.Abs(MachineName.GetHashCode() % 100) + 1
            // lo cual generaba números pseudo-aleatorios (Caja 92, Caja 16, etc.) sin
            // relación con la topología real del negocio.
            int numeroCaja = CalcularNumeroSecuencial(cajaId);

            var sesion = new CajaSesion
            {
                Id = Guid.NewGuid(),
                CajaId = cajaId,

                NumeroCaja = numeroCaja,
                Cajero = _session.UsuarioActual.Username,

                UsuarioAperturaId = _session.UsuarioActual.Id,

                FechaApertura = DateTime.UtcNow,

                MontoApertura = montoInicial,
                Abierta = true,

                TotalVentas = 0,
                TotalIngresos = 0,
                TotalRetiros = 0,

                Diferencia = 0
            };

            _db.CajaSesiones.Add(sesion);
            _db.SaveChanges();

            return sesion;
        }

        public void RegistrarMovimiento(Guid cajaSesionId, string tipo, decimal monto, string descripcion)
        {
            var sesion = _db.CajaSesiones.First(x => x.Id == cajaSesionId);

            if (!sesion.Abierta)
                throw new Exception("No se pueden registrar movimientos en una caja cerrada");

            var mov = new MovimientoCaja
            {
                Id = Guid.NewGuid(),
                CajaSesionId = cajaSesionId,

                Fecha = DateTime.UtcNow,

                Tipo = tipo,
                Monto = monto,
                Descripcion = descripcion
            };

            _db.MovimientosCaja.Add(mov);

            if (tipo == "VENTA") sesion.TotalVentas += monto;
            if (tipo == "INGRESO") sesion.TotalIngresos += monto;
            if (tipo == "RETIRO") sesion.TotalRetiros += monto;

            _db.SaveChanges();
        }

        public void CerrarCaja(Guid cajaSesionId, decimal montoReal)
        {
            var sesion = _db.CajaSesiones.First(x => x.Id == cajaSesionId);

            if (!sesion.Abierta)
                throw new Exception("Caja ya cerrada");

            if (_session.UsuarioActual == null)
                throw new Exception("No hay usuario logueado");

            decimal esperado =
                sesion.MontoApertura +
                sesion.TotalVentas +
                sesion.TotalIngresos -
                sesion.TotalRetiros;

            decimal diferencia = montoReal - esperado;

            sesion.UsuarioCierreId = _session.UsuarioActual.Id;

            sesion.FechaCierre = DateTime.UtcNow;

            sesion.MontoCierre = montoReal;
            sesion.Diferencia = diferencia;
            sesion.Abierta = false;

            try
            {
                _db.SaveChanges();
            }
            catch (Exception ex)
            {
                var error = ex.InnerException?.InnerException?.Message
                         ?? ex.InnerException?.Message
                         ?? ex.Message;

                throw new Exception("Error al cerrar caja:\n" + error);
            }
        }

        public decimal CalcularEsperado(Guid cajaSesionId)
        {
            var s = _db.CajaSesiones.First(x => x.Id == cajaSesionId);

            return s.MontoApertura
                   + s.TotalVentas
                   + s.TotalIngresos
                   - s.TotalRetiros;
        }

        public decimal ObtenerDineroEnCaja(Guid cajaId)
        {
            var sesion = ObtenerSesionAbierta(cajaId);

            if (sesion == null)
                return 0;

            return sesion.MontoApertura
                   + sesion.TotalVentas
                   + sesion.TotalIngresos
                   - sesion.TotalRetiros;
        }

        /// <summary>
        /// Devuelve el número ordinal (1, 2, 3...) de la caja según su orden de creación
        /// en la base central. La caja más antigua es 1 (principal) y las siguientes
        /// quedan 2, 3, etc. Determinístico entre terminales mientras lean la misma BD.
        /// </summary>
        public int CalcularNumeroSecuencial(Guid cajaId)
        {
            try
            {
                var caja = _db.Cajas.AsNoTracking().FirstOrDefault(c => c.Id == cajaId);
                if (caja == null) return 1;

                int anteriores = _db.Cajas
                    .AsNoTracking()
                    .Count(c => c.FechaCreacion < caja.FechaCreacion
                             || (c.FechaCreacion == caja.FechaCreacion && string.Compare(c.Id.ToString(), caja.Id.ToString(), StringComparison.Ordinal) < 0));
                return anteriores + 1;
            }
            catch
            {
                return 1;
            }
        }
    }
}