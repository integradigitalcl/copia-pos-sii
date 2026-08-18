using System;
using System.Collections.Generic;
using System.Linq;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Models;

namespace GrunflexPOS.API.Services
{
    public class PagoService
    {
        private static List<PagoTransaccion> _transacciones = new List<PagoTransaccion>();

        // 👉 WPF crea pago
        public PagoTransaccion CrearPago(PagoRequest request)
        {
            var transaccion = new PagoTransaccion
            {
                Monto = request.Monto,
                NumeroTicket = request.NumeroTicket,
                Estado = "PENDIENTE",

                // 🔴 IDENTIDAD
                TerminalId = request.TerminalId,

                // 🔴 SEGURIDAD
                Token = request.Token
            };

            _transacciones.Add(transaccion);

            return transaccion;
        }

        // 🔴 VALIDACIÓN SEGURA POR TERMINAL + TOKEN
        public List<PagoTransaccion> ObtenerPendientesPorTerminal(string terminalId, string token)
        {
            return _transacciones
                .Where(t =>
                    t.Estado == "PENDIENTE" &&
                    t.TerminalId == terminalId &&
                    t.Token == token // 🔐 VALIDACIÓN CLAVE
                )
                .ToList();
        }

        public List<PagoTransaccion> ObtenerPendientes()
        {
            return _transacciones
                .Where(t => t.Estado == "PENDIENTE")
                .ToList();
        }

        // 👉 PAX responde pago
        public PagoTransaccion? ResolverPago(Guid id, bool aprobado)
        {
            var transaccion = _transacciones.FirstOrDefault(t => t.Id == id);

            if (transaccion == null)
                return null;

            transaccion.Estado = aprobado ? "APROBADO" : "RECHAZADO";
            transaccion.CodigoAutorizacion = aprobado
                ? Guid.NewGuid().ToString().Substring(0, 6)
                : "000000";

            return transaccion;
        }

        public PagoTransaccion? ObtenerEstado(Guid id)
        {
            return _transacciones.FirstOrDefault(t => t.Id == id);
        }
    }
}