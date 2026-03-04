using System;
using System.Linq;
using System.Windows;
using PdfSharp.Fonts;
using GrunflexPOS2.Services;
using Microsoft.EntityFrameworkCore;
using GrunflexPOS2.Data;
using GrunflexPOS2.Models.Entities;

namespace GrunflexPOS2
{
    public partial class App : Application
    {
        public static VentaService VentaService { get; private set; } = null!;
        public static GrunflexDbContext DbContext { get; private set; } = null!;
        public static Guid CajaActualId { get; private set; }

        public App()
        {
            GlobalFontSettings.FontResolver = new CustomFontResolver();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            var config = AppConfig.Cargar();

            var connectionString =
                $"Host={config.Servidor};" +
                $"Port={config.Puerto};" +
                $"Database={config.BaseDatos};" +
                $"Username={config.Usuario};" +
                $"Password={config.Password}";

            var options = new DbContextOptionsBuilder<GrunflexDbContext>()
                .UseNpgsql(connectionString)
                .Options;

            DbContext = new GrunflexDbContext(options);

            try
            {
                DbContext.Database.CanConnect();

                InicializarSistema();

                ConfigurarCaja(config);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error conectando a PostgreSQL:\n" + ex.Message);
            }

            VentaService = new VentaService();

            base.OnStartup(e);
        }

        private void InicializarSistema()
        {
            if (!DbContext.Empresas.Any())
            {
                var empresa = new Empresa
                {
                    Nombre = "Empresa Principal"
                };

                DbContext.Empresas.Add(empresa);
                DbContext.SaveChanges();

                var caja = new Caja
                {
                    Nombre = "Caja Principal",
                    EmpresaId = empresa.Id
                };

                DbContext.Cajas.Add(caja);
                DbContext.SaveChanges();
            }
        }

        private void ConfigurarCaja(AppConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.CajaId))
            {
                var primeraCaja = DbContext.Cajas.First();

                config.CajaId = primeraCaja.Id.ToString();
                config.Guardar();
            }

            CajaActualId = Guid.Parse(config.CajaId);
        }
    }
}