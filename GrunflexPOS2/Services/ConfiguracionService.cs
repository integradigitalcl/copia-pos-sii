using System;
using System.Linq;
using System.Globalization;
using GrunflexPOS2.Data;
using GrunflexPOS2.Licensing;
using GrunflexPOS2.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Services
{
    public class ConfiguracionService
    {
        private readonly GrunflexDbContext _context;

        public ConfiguracionService()
            : this(null)
        {
        }

        public ConfiguracionService(GrunflexDbContext? context)
        {
            if (context != null)
                _context = context;
            else if (App.DbContext != null)
                _context = App.DbContext;
            else
                _context = new GrunflexDbContext();
        }

        // 🔹 OBTENER VALOR
        public string Get(string clave)
        {
            if (LocalPosConfigStore.IsLocalFallbackKey(clave))
            {
                var local = LocalPosConfigStore.TryGet(clave);
                if (local != null)
                    return local;
            }

            var config = _context.Set<Configuracion>()
                                 .FirstOrDefault(c => c.Clave == clave);

            return config?.Valor ?? string.Empty;
        }

        // 🔹 GUARDAR / ACTUALIZAR
        public void Set(string clave, string valor)
        {
            if (LocalPosConfigStore.IsLocalFallbackKey(clave))
                LocalPosConfigStore.Set(clave, valor);

            try
            {
                PersistToDatabase(clave, valor);
            }
            catch (DbUpdateException) when (LocalPosConfigStore.IsLocalFallbackKey(clave))
            {
                // Licencia / instalación ya guardada en LocalAppData.
            }
        }

        private void PersistToDatabase(string clave, string valor)
        {
            var config = _context.Set<Configuracion>()
                .FirstOrDefault(c => c.Clave == clave);

            if (config == null)
            {
                config = new Configuracion
                {
                    Id = Guid.NewGuid(),
                    Clave = clave,
                    Valor = valor
                };
                _context.Add(config);
            }
            else
            {
                config.Valor = valor;
            }

            try
            {
                _context.SaveChanges();
            }
            catch (DbUpdateException)
            {
                var cs = _context.Database.GetConnectionString() ?? string.Empty;
                if (!cs.Contains(@"\\", StringComparison.Ordinal))
                {
                    try { _context.Database.Migrate(); } catch { /* logged elsewhere */ }
                    _context.SaveChanges();
                    return;
                }

                throw;
            }
        }

        // =========================
        // 🔥 CORREO (ATAJOS)
        // =========================

        public bool GetCorreoActivo()
        {
            return Get("correo_activo") == "true";
        }

        public void SetCorreoActivo(bool activo)
        {
            Set("correo_activo", activo ? "true" : "false");
        }

        public string GetCorreoEmail()
        {
            return Get("correo_email");
        }

        public void SetCorreoEmail(string email)
        {
            Set("correo_email", email);
        }

        public string GetCorreoClave()
        {
            return Get("correo_clave");
        }

        public void SetCorreoClave(string clave)
        {
            Set("correo_clave", clave);
        }

        // =========================
        // OPCIONES HABILITADAS POS
        // =========================
        public bool GetUsarInventario() => GetBool("opt_usar_inventario", true);
        public void SetUsarInventario(bool v) => SetBool("opt_usar_inventario", v);

        public string GetMetodoCostoInventario() =>
            GetOrDefault("opt_metodo_costo", "Costo promedio ponderado");
        public void SetMetodoCostoInventario(string v) =>
            Set("opt_metodo_costo", string.IsNullOrWhiteSpace(v) ? "Costo promedio ponderado" : v.Trim());

        public bool GetOfrecerCredito() => GetBool("opt_ofrecer_credito", true);
        public void SetOfrecerCredito(bool v) => SetBool("opt_ofrecer_credito", v);

        public bool GetVentaProductoComun() => GetBool("opt_venta_producto_comun", true);
        public void SetVentaProductoComun(bool v) => SetBool("opt_venta_producto_comun", v);

        public bool GetCalcularMargenAutomatico() => GetBool("opt_calcular_margen_auto", true);
        public void SetCalcularMargenAutomatico(bool v) => SetBool("opt_calcular_margen_auto", v);

        public decimal GetMargenPorcentaje() => GetDecimal("opt_margen_porcentaje", 20m);
        public void SetMargenPorcentaje(decimal v) =>
            Set("opt_margen_porcentaje", Math.Max(0m, v).ToString(CultureInfo.InvariantCulture));

        public bool GetRedondeoHabilitado() => GetBool("opt_redondeo_habilitado", false);
        public void SetRedondeoHabilitado(bool v) => SetBool("opt_redondeo_habilitado", v);

        public string GetModoRedondeo() =>
            GetOrDefault("opt_modo_redondeo", "A décimas");
        public void SetModoRedondeo(string v) =>
            Set("opt_modo_redondeo", string.IsNullOrWhiteSpace(v) ? "A décimas" : v.Trim());

        public string GetAvisoOcasional() =>
            GetOrDefault("opt_aviso_ocasional", "Por favor, recuerda lavarte las manos");
        public void SetAvisoOcasional(string v) =>
            Set("opt_aviso_ocasional", (v ?? string.Empty).Trim());

        public int GetCadaNVentas() => GetInt("opt_aviso_cada_n_ventas", 0);
        public void SetCadaNVentas(int v) => Set("opt_aviso_cada_n_ventas", Math.Max(0, v).ToString());

        private string GetOrDefault(string clave, string porDefecto)
        {
            var v = Get(clave);
            return string.IsNullOrWhiteSpace(v) ? porDefecto : v;
        }

        private bool GetBool(string clave, bool porDefecto)
        {
            var v = Get(clave);
            if (string.IsNullOrWhiteSpace(v))
                return porDefecto;
            return string.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private void SetBool(string clave, bool valor) => Set(clave, valor ? "true" : "false");

        private int GetInt(string clave, int porDefecto)
        {
            var v = Get(clave);
            return int.TryParse(v, out var n) ? n : porDefecto;
        }

        private decimal GetDecimal(string clave, decimal porDefecto)
        {
            var v = Get(clave);
            return decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : porDefecto;
        }
    }
}